// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using MQTTnet.Adapter;
using MQTTnet.Diagnostics.Logger;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using MQTTnet.Internal;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MQTTnet.Server.Internal.Formatter;
using MqttDisconnectPacketFactory = MQTTnet.Server.Internal.Formatter.MqttDisconnectPacketFactory;
using MqttPubAckPacketFactory = MQTTnet.Server.Internal.Formatter.MqttPubAckPacketFactory;
using MqttPubCompPacketFactory = MQTTnet.Server.Internal.Formatter.MqttPubCompPacketFactory;
using MqttPublishPacketFactory = MQTTnet.Server.Internal.Formatter.MqttPublishPacketFactory;
using MqttPubRecPacketFactory = MQTTnet.Server.Internal.Formatter.MqttPubRecPacketFactory;
using MqttPubRelPacketFactory = MQTTnet.Server.Internal.Formatter.MqttPubRelPacketFactory;

namespace MQTTnet.Server.Internal;

public sealed class MqttConnectedClient : IDisposable
{
    readonly MqttServerEventContainer _eventContainer;
    readonly MqttNetSourceLogger _logger;
    readonly AsyncLock _qos2AcknowledgementLock = new();
    readonly AsyncLock _wireSendLock = new();
    readonly MqttServerOptions _serverOptions;
    readonly MqttClientSessionsManager _sessionsManager;
    readonly long _connectionGeneration;
    readonly Dictionary<ushort, string> _topicAlias = new();
    readonly int _initialSendQuota;

    CancellationTokenSource _cancellationToken = new();
    bool _disconnectPacketSent;
    int _sendQuota;
    Task _sendPacketsTask = Task.CompletedTask;

    public MqttConnectedClient(
        MqttConnectPacket connectPacket,
        IMqttChannelAdapter channelAdapter,
        MqttSession session,
        MqttServerOptions serverOptions,
        MqttServerEventContainer eventContainer,
        MqttClientSessionsManager sessionsManager,
        IMqttNetLogger logger)
    {
        _serverOptions = serverOptions ?? throw new ArgumentNullException(nameof(serverOptions));
        _eventContainer = eventContainer ?? throw new ArgumentNullException(nameof(eventContainer));
        _sessionsManager = sessionsManager ?? throw new ArgumentNullException(nameof(sessionsManager));
        ConnectPacket = connectPacket ?? throw new ArgumentNullException(nameof(connectPacket));

        ChannelAdapter = channelAdapter ?? throw new ArgumentNullException(nameof(channelAdapter));
        _initialSendQuota = ChannelAdapter.PacketFormatterAdapter.ProtocolVersion == MqttProtocolVersion.V500 && connectPacket.ReceiveMaximum > 0
            ? connectPacket.ReceiveMaximum
            : ushort.MaxValue;
        _sendQuota = _initialSendQuota;
        RemoteEndPoint = channelAdapter.RemoteEndPoint;
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionGeneration = Session.ActivateConnection();

        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger.WithSource(nameof(MqttConnectedClient));
    }

    public IMqttChannelAdapter ChannelAdapter { get; }

    public MqttConnectPacket ConnectPacket { get; }

    public MqttDisconnectPacket DisconnectPacket { get; private set; }

    public string Id => ConnectPacket.ClientId;

    public bool IsRunning { get; private set; }

    public bool IsTakenOver { get; set; }

    public EndPoint RemoteEndPoint { get; }

    public MqttSession Session { get; }
    public Guid ConnectionAttemptId { get; } = Guid.NewGuid();

    public MqttClientStatistics Statistics { get; } = new();

    public string UserName => ConnectPacket.Username;

    public void Dispose()
    {
        _cancellationToken?.Dispose();
        _qos2AcknowledgementLock.Dispose();
    }

    public void ResetStatistics()
    {
        ChannelAdapter.ResetStatistics();
        Statistics.ResetStatistics();
    }

    public async Task RunAsync()
    {
        if (IsTakenOver || !Session.IsCurrentConnection(_connectionGeneration)) return;
        _logger.Info("Client '{0}': Session started", Id);

        Session.LatestConnectPacket = ConnectPacket;
        Session.ExpiryInterval = ConnectPacket.SessionExpiryInterval;
        Session.WillMessageSent = false;

        try
        {
            var cancellationToken = _cancellationToken.Token;
            IsRunning = true;

            _sendPacketsTask = Task.Factory.StartNew(() => SendPacketsLoop(cancellationToken), cancellationToken, TaskCreationOptions.PreferFairness, TaskScheduler.Default).Unwrap();

            await ReceivePackagesLoop(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            IsRunning = false;

            if (Session.IsCurrentConnection(_connectionGeneration)) Session.DisconnectedTimestamp = DateTime.UtcNow;

            _cancellationToken?.TryCancel();
            try { await _sendPacketsTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _cancellationToken?.Dispose();
            _cancellationToken = null;
        }

        _logger.Info("Client '{0}': Connection stopped", Id);
    }

    internal long ConnectionGeneration => _connectionGeneration;

    public async Task SendPacketAsync(MqttPacket packet, CancellationToken cancellationToken)
    {
        if (packet is MqttPubRelPacket pubRelPacket)
        {
            using (await _qos2AcknowledgementLock.EnterAsync(cancellationToken).ConfigureAwait(false))
            {
                await SendPacketCoreAsync(packet, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await SendPacketCoreAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    async Task<MqttPacket> SendPacketCoreAsync(MqttPacket packet, CancellationToken cancellationToken)
    {
        var originalPublish = packet as MqttPublishPacket;
        var isolatePublish = originalPublish != null && _eventContainer.InterceptingOutboundPacketEvent.HasHandlers;
        if (isolatePublish) packet = MqttPublishPacketSnapshot.Clone(originalPublish);
        packet = await InterceptPacketAsync(packet, cancellationToken).ConfigureAwait(false);
        if (packet == null)
        {
            Session.CompleteUntrackedPublish(originalPublish, _connectionGeneration);
            // The interceptor has decided that this packet will not used at all.
            // This might break the protocol but the user wants that.
            return null;
        }
        if (isolatePublish && packet is MqttPublishPacket editedPublish) packet = MqttPublishPacketSnapshot.Clone(editedPublish);
        if (packet is MqttPublishPacket durablePublish)
            await Session.PrepareDurablePublishAsync(originalPublish ?? durablePublish, durablePublish, _connectionGeneration, cancellationToken).ConfigureAwait(false);
        if (packet is MqttPubRelPacket durablePubRel)
            await Session.PrepareDurablePubRelAsync(durablePubRel.PacketIdentifier, _connectionGeneration, cancellationToken).ConfigureAwait(false);

        // User callbacks are outside this gate. Takeover cancels and joins actual wire writes before session recovery.
        using (await _wireSendLock.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            if (packet is not MqttDisconnectPacket && (IsTakenOver || !Session.IsCurrentConnection(_connectionGeneration))) return null;
            if (packet is MqttPubRelPacket pubRel && !Session.CanSendPubRel(pubRel.PacketIdentifier, _connectionGeneration)) return null;
            if (packet is MqttPublishPacket) Session.CompleteUntrackedPublish(originalPublish, _connectionGeneration);
            if (packet is MqttPublishPacket trackedPublish && trackedPublish.QualityOfServiceLevel > MqttQualityOfServiceLevel.AtMostOnce &&
                !Session.MarkPublishSent(originalPublish ?? trackedPublish, _connectionGeneration, isolatePublish ? trackedPublish : null)) return null;

            if (packet is MqttPublishPacket publishPacket && publishPacket.QualityOfServiceLevel > MqttQualityOfServiceLevel.AtMostOnce &&
                ChannelAdapter.PacketFormatterAdapter.ProtocolVersion == MqttProtocolVersion.V500)
            {
                Interlocked.Decrement(ref _sendQuota);
            }

            await ChannelAdapter.SendPacketAsync(packet, cancellationToken).ConfigureAwait(false);
            if (packet is MqttPubRelPacket sentPubRel) Session.MarkPubRelSent(sentPubRel.PacketIdentifier, _connectionGeneration);
            Statistics.HandleSentPacket(packet);
            return packet;
        }
    }

    internal async Task QuiesceForRecoveryAsync(CancellationToken cancellationToken)
    {
        IsTakenOver = true;
        StopInternal();
        using (await _wireSendLock.EnterAsync(cancellationToken).ConfigureAwait(false)) { }
        // Keep this managed gate alive with the connection object: callers can still join after RunAsync completed.
    }

    public async Task StopAsync(MqttServerClientDisconnectOptions disconnectOptions)
    {
        IsRunning = false;

        if (!_disconnectPacketSent)
        {
            // // Sending DISCONNECT packets from the server to the client is only supported when using MQTTv5+.
            if (ChannelAdapter.PacketFormatterAdapter.ProtocolVersion == MqttProtocolVersion.V500)
            {
                // From RFC: The Client or Server MAY send a DISCONNECT packet before closing the Network Connection.
                // This library does not sent a DISCONNECT packet for a normal disconnection.
                // TODO: Maybe adding a configuration option is requested in the future.
                if (disconnectOptions != null)
                {
                    if (disconnectOptions.ReasonCode != MqttDisconnectReasonCode.NormalDisconnection || disconnectOptions.UserProperties?.Count > 0 ||
                        !string.IsNullOrEmpty(disconnectOptions.ReasonString) || !string.IsNullOrEmpty(disconnectOptions.ServerReference))
                    {
                        // It is very important to send the DISCONNECT packet here BEFORE cancelling the
                        // token because the entire connection is closed (disposed) as soon as the cancellation
                        // token is cancelled. To there is no chance that the DISCONNECT packet will ever arrive
                        // at the client!
                        await TrySendDisconnectPacket(disconnectOptions).ConfigureAwait(false);
                    }
                }
            }
        }

        StopInternal();
    }

    Task ClientAcknowledgedPublishPacket(MqttPublishPacket publishPacket, MqttPacketWithIdentifier acknowledgePacket)
    {
        if (_eventContainer.ClientAcknowledgedPublishPacketEvent.HasHandlers)
        {
            var eventArgs = new ClientAcknowledgedPublishPacketEventArgs(Id, UserName, Session.Items, publishPacket, acknowledgePacket, Session.GetEnqueueState(publishPacket));
            eventArgs.DurableCommitStatus = Session.GetDurableCompletionStatus(publishPacket);
            return _eventContainer.ClientAcknowledgedPublishPacketEvent.TryInvokeAsync(eventArgs, _logger);
        }

        return CompletedTask.Instance;
    }

    void HandleIncomingPingReqPacket()
    {
        // See: The Server MUST send a PINGRESP packet in response to a PINGREQ packet [MQTT-3.12.4-1].
        Session.EnqueueHealthPacket(new MqttPacketBusItem(MqttPingRespPacket.Instance));
    }

    async Task HandleIncomingPubAckPacket(MqttPubAckPacket pubAckPacket)
    {
        var acknowledgedPublishPacket = Session.HasDurablePersistence
            ? await Session.CompleteDurableAcknowledgementAsync(pubAckPacket.PacketIdentifier, MqttQualityOfServiceLevel.AtLeastOnce, (byte)pubAckPacket.ReasonCode, false, _connectionGeneration, _cancellationToken.Token).ConfigureAwait(false)
            : Session.AcknowledgePublishPacket(pubAckPacket.PacketIdentifier, MqttQualityOfServiceLevel.AtLeastOnce, _connectionGeneration);

        if (acknowledgedPublishPacket != null)
        {
            ReplenishSendQuota();
            await ClientAcknowledgedPublishPacket(acknowledgedPublishPacket, pubAckPacket).ConfigureAwait(false);
        }

    }

    async Task HandleIncomingPubCompPacket(MqttPubCompPacket pubCompPacket)
    {
        using (await _qos2AcknowledgementLock.EnterAsync().ConfigureAwait(false))
        {
            var acknowledgedPublishPacket = Session.HasDurablePersistence
                ? await Session.CompleteDurableAcknowledgementAsync(pubCompPacket.PacketIdentifier, MqttQualityOfServiceLevel.ExactlyOnce, (byte)pubCompPacket.ReasonCode, false, _connectionGeneration, _cancellationToken.Token).ConfigureAwait(false)
                : Session.AcknowledgePublishPacket(pubCompPacket.PacketIdentifier, MqttQualityOfServiceLevel.ExactlyOnce, _connectionGeneration);

            if (acknowledgedPublishPacket != null)
            {
                ReplenishSendQuota();
                await ClientAcknowledgedPublishPacket(acknowledgedPublishPacket, pubCompPacket).ConfigureAwait(false);
            }
        }
    }

    async Task HandleIncomingPublishPacket(MqttPublishPacket publishPacket, CancellationToken cancellationToken)
    {
        HandleTopicAlias(publishPacket);

        var applicationMessage = MqttApplicationMessageFactory.Create(publishPacket);
        // Topic aliases are scoped to this network connection and must not be forwarded to other clients.
        applicationMessage.TopicAlias = 0;

        var dispatchApplicationMessageResult =
            await _sessionsManager.DispatchApplicationMessage(Id, UserName, Session.Items, applicationMessage, cancellationToken).ConfigureAwait(false);

        if (dispatchApplicationMessageResult.CloseConnection)
        {
            await StopAsync(new MqttServerClientDisconnectOptions { ReasonCode = MqttDisconnectReasonCode.UnspecifiedError });
            return;
        }

        switch (publishPacket.QualityOfServiceLevel)
        {
            case MqttQualityOfServiceLevel.AtMostOnce:
            {
                // Do nothing since QoS 0 has no ACK at all!
                break;
            }
            case MqttQualityOfServiceLevel.AtLeastOnce:
            {
                var pubAckPacket = MqttPubAckPacketFactory.Create(publishPacket, dispatchApplicationMessageResult);
                Session.EnqueueControlPacket(new MqttPacketBusItem(pubAckPacket));
                break;
            }
            case MqttQualityOfServiceLevel.ExactlyOnce:
            {
                var pubRecPacket = MqttPubRecPacketFactory.Create(publishPacket, dispatchApplicationMessageResult);
                Session.EnqueueControlPacket(new MqttPacketBusItem(pubRecPacket));
                break;
            }
            default:
            {
                throw new MqttCommunicationException("Received a not supported QoS level");
            }
        }
    }

    async Task HandleIncomingPubRecPacket(MqttPubRecPacket pubRecPacket)
    {
        if (Session.HasDurablePersistence)
        {
            var durablePacket = await Session.CompleteDurableAcknowledgementAsync(pubRecPacket.PacketIdentifier, MqttQualityOfServiceLevel.ExactlyOnce,
                (byte)pubRecPacket.ReasonCode, true, _connectionGeneration, _cancellationToken.Token).ConfigureAwait(false);
            if (durablePacket == null) return;
            if ((byte)pubRecPacket.ReasonCode >= 0x80)
            {
                ReplenishSendQuota();
                await ClientAcknowledgedPublishPacket(durablePacket, pubRecPacket).ConfigureAwait(false);
            }
            else Session.EnqueueControlPacket(new MqttPacketBusItem(MqttPubRelPacketFactory.Create(pubRecPacket, MqttApplicationMessageReceivedReasonCode.Success)));
            return;
        }
        using (await _qos2AcknowledgementLock.EnterAsync().ConfigureAwait(false))
        {
            var sendPubRel = Session.ProcessPubRec(pubRecPacket.PacketIdentifier, (int)pubRecPacket.ReasonCode >= 0x80,
                _connectionGeneration, out var completed);
            if (completed != null)
            {
                ReplenishSendQuota();
                await ClientAcknowledgedPublishPacket(completed, pubRecPacket).ConfigureAwait(false);
            }
            if (!sendPubRel) return;
        }
        var pubRelPacket = MqttPubRelPacketFactory.Create(pubRecPacket, MqttApplicationMessageReceivedReasonCode.Success);
        Session.EnqueueControlPacket(new MqttPacketBusItem(pubRelPacket));
    }

    void HandleIncomingPubRelPacket(MqttPubRelPacket pubRelPacket)
    {
        var pubCompPacket = MqttPubCompPacketFactory.Create(pubRelPacket, MqttApplicationMessageReceivedReasonCode.Success);
        Session.EnqueueControlPacket(new MqttPacketBusItem(pubCompPacket));
    }

    async Task HandleIncomingSubscribePacket(MqttSubscribePacket subscribePacket, MqttSubscriptionRequestSnapshot request, CancellationToken cancellationToken)
    {
        var subscribeResult = await Session.SubscribeForConnectionAsync(subscribePacket, _connectionGeneration, request, cancellationToken).ConfigureAwait(false);
        if (!Session.IsCurrentConnection(_connectionGeneration)) return;

        var subAckPacket = MqttSubAckPacketFactory.Create(subscribePacket, subscribeResult);

        Session.EnqueueControlPacket(new MqttPacketBusItem(subAckPacket));

        if (subscribeResult.CloseConnection)
        {
            StopInternal();
            return;
        }

        if (subscribeResult.RetainedMessages == null)
        {
            return;
        }

        foreach (var retainedMessageMatch in subscribeResult.RetainedMessages)
        {
            await Session.EnqueueApplicationMessageAsync(string.Empty, retainedMessageMatch.ApplicationMessage,
                () => MqttPublishPacketFactory.Create(retainedMessageMatch)).ConfigureAwait(false);
        }
    }

    async Task HandleIncomingUnsubscribePacket(MqttUnsubscribePacket unsubscribePacket, MqttSubscriptionRequestSnapshot request, CancellationToken cancellationToken)
    {
        var unsubscribeResult = await Session.UnsubscribeForConnectionAsync(unsubscribePacket, _connectionGeneration, request, cancellationToken).ConfigureAwait(false);
        if (!Session.IsCurrentConnection(_connectionGeneration)) return;

        var unsubAckPacket = MqttUnsubAckPacketFactory.Create(unsubscribePacket, unsubscribeResult);

        Session.EnqueueControlPacket(new MqttPacketBusItem(unsubAckPacket));

        if (unsubscribeResult.CloseConnection)
        {
            StopInternal();
        }
    }

    void HandleTopicAlias(MqttPublishPacket publishPacket)
    {
        if (publishPacket.TopicAlias == 0)
        {
            return;
        }

        lock (_topicAlias)
        {
            if (!string.IsNullOrEmpty(publishPacket.Topic))
            {
                _topicAlias[publishPacket.TopicAlias] = publishPacket.Topic;
            }
            else
            {
                if (_topicAlias.TryGetValue(publishPacket.TopicAlias, out var topic))
                {
                    publishPacket.Topic = topic;
                }
                else
                {
                    _logger.Warning("Client '{0}': Received invalid topic alias ({1})", Id, publishPacket.TopicAlias);
                }
            }
        }
    }

    async Task<MqttPacket> InterceptPacketAsync(MqttPacket packet, CancellationToken cancellationToken)
    {
        if (!_eventContainer.InterceptingOutboundPacketEvent.HasHandlers)
        {
            return packet;
        }

        var interceptingPacketEventArgs = new InterceptingPacketEventArgs(Id, UserName, RemoteEndPoint, packet, Session.Items, cancellationToken);
        interceptingPacketEventArgs.ConnectionAttemptId = ConnectionAttemptId;
        await _eventContainer.InterceptingOutboundPacketEvent.InvokeAsync(interceptingPacketEventArgs).ConfigureAwait(false);

        if (!interceptingPacketEventArgs.ProcessPacket || packet == null)
        {
            return null;
        }

        return interceptingPacketEventArgs.Packet;
    }

    async Task ReceivePackagesLoop(CancellationToken cancellationToken)
    {
        MqttPacket currentPacket = null;
        try
        {
            // We do not listen for the cancellation token here because the internal buffer might still
            // contain data to be read even if the TCP connection was already dropped. So we rely on an
            // own exception in the reading loop!
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Yield();

                currentPacket = await ChannelAdapter.ReceivePacketAsync(cancellationToken).ConfigureAwait(false);
                if (currentPacket == null)
                {
                    return;
                }

                // Check for cancellation again because receive packet might block some time.
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                // The TCP connection of this client may be still open but the client has already been taken over by
                // a new TCP connection. So we must exit here to make sure to no longer process any message.
                if (IsTakenOver || !IsRunning)
                {
                    return;
                }

                var processPacket = true;
                var subscriptionRequest = currentPacket is MqttSubscribePacket or MqttUnsubscribePacket ? new MqttSubscriptionRequestSnapshot(ConnectionAttemptId, currentPacket) : null;

                if (_eventContainer.InterceptingInboundPacketEvent.HasHandlers)
                {
                    var interceptingPacketEventArgs = new InterceptingPacketEventArgs(Id, UserName, RemoteEndPoint, currentPacket, Session.Items, cancellationToken);
                    interceptingPacketEventArgs.ConnectionAttemptId = ConnectionAttemptId;
                    interceptingPacketEventArgs.RequestId = subscriptionRequest?.RequestId ?? Guid.Empty;
                    await _eventContainer.InterceptingInboundPacketEvent.InvokeAsync(interceptingPacketEventArgs).ConfigureAwait(false);
                    if (subscriptionRequest != null) subscriptionRequest.RequestState = interceptingPacketEventArgs.RequestState;
                    currentPacket = interceptingPacketEventArgs.Packet;
                    processPacket = interceptingPacketEventArgs.ProcessPacket;
                }

                if (!processPacket || currentPacket == null)
                {
                    // Restart the receiving process to get the next packet ignoring the current one..
                    continue;
                }

                Statistics.HandleReceivedPacket(currentPacket);

                if (currentPacket is MqttPublishPacket publishPacket)
                {
                    await HandleIncomingPublishPacket(publishPacket, cancellationToken).ConfigureAwait(false);
                }
                else if (currentPacket is MqttPubAckPacket pubAckPacket)
                {
                    await HandleIncomingPubAckPacket(pubAckPacket).ConfigureAwait(false);
                }
                else if (currentPacket is MqttPubCompPacket pubCompPacket)
                {
                    await HandleIncomingPubCompPacket(pubCompPacket).ConfigureAwait(false);
                }
                else if (currentPacket is MqttPubRecPacket pubRecPacket)
                {
                    await HandleIncomingPubRecPacket(pubRecPacket).ConfigureAwait(false);
                }
                else if (currentPacket is MqttPubRelPacket pubRelPacket)
                {
                    HandleIncomingPubRelPacket(pubRelPacket);
                }
                else if (currentPacket is MqttSubscribePacket subscribePacket)
                {
                    await HandleIncomingSubscribePacket(subscribePacket, subscriptionRequest, cancellationToken).ConfigureAwait(false);
                }
                else if (currentPacket is MqttUnsubscribePacket unsubscribePacket)
                {
                    await HandleIncomingUnsubscribePacket(unsubscribePacket, subscriptionRequest, cancellationToken).ConfigureAwait(false);
                }
                else if (currentPacket is MqttPingReqPacket)
                {
                    HandleIncomingPingReqPacket();
                }
                else if (currentPacket is MqttPingRespPacket)
                {
                    throw new MqttProtocolViolationException("A PINGRESP Packet is sent by the Server to the Client in response to a PINGREQ Packet only.");
                }
                else if (currentPacket is MqttDisconnectPacket disconnectPacket)
                {
                    if (disconnectPacket.HasSessionExpiryInterval)
                    {
                        // MQTT 5.0 section 3.14.2.2.2 prohibits extending a zero CONNECT interval.
                        if (ConnectPacket.SessionExpiryInterval == 0 && disconnectPacket.SessionExpiryInterval != 0)
                        {
                            await StopAsync(new MqttServerClientDisconnectOptions { ReasonCode = MqttDisconnectReasonCode.ProtocolError }).ConfigureAwait(false);
                            return;
                        }

                        Session.ExpiryInterval = disconnectPacket.SessionExpiryInterval;
                    }

                    DisconnectPacket = disconnectPacket;
                    return;
                }
                else
                {
                    throw new MqttProtocolViolationException("Packet not allowed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (exception is MqttCommunicationException)
            {
                _logger.Warning(exception, "Client '{0}': Communication exception while receiving packets", Id);
                return;
            }

            var logLevel = MqttNetLogLevel.Error;

            if (!IsRunning)
            {
                // There was an exception but the connection is already closed. So there is no chance to send a response to the client etc.
                logLevel = MqttNetLogLevel.Warning;
            }

            if (currentPacket == null)
            {
                _logger.Publish(logLevel, exception, "Client '{0}': Error while receiving packets", Id);
            }
            else
            {
                _logger.Publish(logLevel, exception, "Client '{0}': Error while processing {1} packet", Id, currentPacket.GetRfcName());
            }
        }
    }

    async Task SendPacketsLoop(CancellationToken cancellationToken)
    {
        MqttPacketBusItem packetBusItem = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested && !IsTakenOver && IsRunning)
            {
                packetBusItem = await Session.DequeuePacketAsync(CanDequeuePacket, cancellationToken).ConfigureAwait(false);

                // Also check the cancellation token here because the dequeue is blocking and may take some time.
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (IsTakenOver || !IsRunning)
                {
                    return;
                }

                try
                {
                    await SendPacketAsync(packetBusItem.Packet, cancellationToken).ConfigureAwait(false);
                    packetBusItem.Complete();
                }
                catch (OperationCanceledException)
                {
                    packetBusItem.Cancel();
                    StopInternal();
                    return;
                }
                catch (Exception exception)
                {
                    packetBusItem.Fail(exception);
                    // A failed write may have sent part of the packet. End this connection
                    // rather than waiting for an acknowledgement that can never replenish quota.
                    StopInternal();
                    return;
                }
                finally
                {
                    await Task.Yield();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (exception is MqttCommunicationTimedOutException)
            {
                _logger.Warning(exception, "Client '{0}': Sending PUBLISH packet failed due to timeout", Id);
            }
            else if (exception is MqttCommunicationException)
            {
                _logger.Warning(exception, "Client '{0}': Sending PUBLISH packet failed due to communication exception", Id);
            }
            else
            {
                _logger.Error(exception, "Client '{0}': Sending PUBLISH packet failed", Id);
            }

            // Tracked publishes remain session-owned and are retried by Recover with their original IDs/phases.
            StopInternal();
        }
    }

    void StopInternal()
    {
        _cancellationToken?.TryCancel();
    }

    bool CanDequeuePacket(MqttPacketBusItem item)
    {
        if (IsTakenOver || !Session.IsCurrentConnection(_connectionGeneration)) return false;
        if (item.Packet is MqttPublishPacket && Session.IsDataRecoveryPaused) return false;
        // MQTT 5.0 section 4.9 permits suspending all PUBLISH packets at zero quota.
        // Control and health traffic must still progress, including QoS 2 PUBREL.
        return item.Packet is not MqttPublishPacket || ChannelAdapter.PacketFormatterAdapter.ProtocolVersion != MqttProtocolVersion.V500 ||
            Volatile.Read(ref _sendQuota) > 0;
    }

    void ReplenishSendQuota()
    {
        int quota;
        do
        {
            quota = Volatile.Read(ref _sendQuota);
            if (quota >= _initialSendQuota)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _sendQuota, quota + 1, quota) != quota);

        Session.SignalPacketBus();
    }

    async Task TrySendDisconnectPacket(MqttServerClientDisconnectOptions options)
    {
        try
        {
            // This also indicates that it was tried at least!
            _disconnectPacketSent = true;

            var disconnectPacket = MqttDisconnectPacketFactory.Create(options);

            using var timeout = new CancellationTokenSource(_serverOptions.DefaultCommunicationTimeout);
            await SendPacketAsync(disconnectPacket, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Warning(exception, "Client '{0}': Error while sending DISCONNECT packet (ReasonCode = {1})", Id, options.ReasonCode);
        }
    }
}
