// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using System.Collections.Concurrent;
using MQTTnet.Adapter;
using MQTTnet.Diagnostics.Logger;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using MQTTnet.Internal;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MQTTnet.Server.Internal.Formatter;
using MqttPublishPacketFactory = MQTTnet.Server.Internal.Formatter.MqttPublishPacketFactory;

namespace MQTTnet.Server.Internal;

public sealed class MqttClientSessionsManager : ISubscriptionChangedNotification, IDisposable
{
    readonly Dictionary<string, MqttConnectedClient> _clients = new(4096);
    readonly AsyncLock _createConnectionSyncRoot = new();
    readonly MqttServerEventContainer _eventContainer;
    readonly MqttNetSourceLogger _logger;
    readonly MqttServerOptions _options;
    readonly MqttRetainedMessagesManager _retainedMessagesManager;
    readonly IMqttNetLogger _rootLogger;
    readonly ReaderWriterLockSlim _sessionsManagementLock = new();

    // The _sessions dictionary contains all session, the _subscriberSessions hash set contains subscriber sessions only.
    // See the MqttSubscription object for a detailed explanation.
    readonly MqttSessionsStorage _sessionsStorage = new();
    readonly HashSet<MqttSession> _subscriberSessions = [];
    readonly MqttWillMessagesManager _willMessages;

    public MqttClientSessionsManager(MqttServerOptions options, MqttRetainedMessagesManager retainedMessagesManager, MqttServerEventContainer eventContainer, IMqttNetLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger.WithSource(nameof(MqttClientSessionsManager));
        _rootLogger = logger;

        _options = options ?? throw new ArgumentNullException(nameof(options));
        _retainedMessagesManager = retainedMessagesManager ?? throw new ArgumentNullException(nameof(retainedMessagesManager));
        _eventContainer = eventContainer ?? throw new ArgumentNullException(nameof(eventContainer));
        _willMessages = new MqttWillMessagesManager(this, logger);
    }

    internal void EndWillSession(MqttSession session) => _willMessages.SessionEnded(session);

    internal Task StartWillsAsync() => _willMessages.StartAsync();

    internal Task StopWillsAsync(bool endSessions) => _willMessages.StopAsync(endSessions);

    public async Task CloseAllConnections(MqttServerClientDisconnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<MqttConnectedClient> connections;
        lock (_clients)
        {
            connections = _clients.Values.ToList();
            _clients.Clear();
        }

        foreach (var connection in connections)
        {
            await connection.StopAsync(options).ConfigureAwait(false);
        }
    }

    public async Task DeleteSessionAsync(string clientId)
    {
        if (_options.SessionPersistence != null)
        {
            await DeleteDurableSessionCoreAsync(clientId).ConfigureAwait(false);
            return;
        }
        _logger.Verbose("Deleting session for client '{0}'.", clientId);

        MqttConnectedClient connection;
        lock (_clients)
        {
            _clients.TryGetValue(clientId, out connection);
        }

        MqttSession session;
        _sessionsManagementLock.EnterWriteLock();
        try
        {
            if (_sessionsStorage.TryRemoveSession(clientId, out session))
            {
                _subscriberSessions.Remove(session);
            }
        }
        finally
        {
            _sessionsManagementLock.ExitWriteLock();
        }

        try
        {
            if (connection != null)
            {
                await connection.StopAsync(new MqttServerClientDisconnectOptions { ReasonCode = MqttDisconnectReasonCode.NormalDisconnection }).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Error while deleting session '{0}'", clientId);
        }

        try
        {
            if (_eventContainer.SessionDeletedEvent.HasHandlers && session != null)
            {
                var eventArgs = new SessionDeletedEventArgs(clientId, session.UserName, session.Items);
                await _eventContainer.SessionDeletedEvent.TryInvokeAsync(eventArgs, _logger).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Error while executing session deleted event for session '{0}'", clientId);
        }

        session?.Dispose();

        _logger.Verbose("Session of client '{0}' deleted", clientId);
    }

    async Task DeleteDurableSessionCoreAsync(string clientId, MqttSession capturedSession = null, long? expectedGeneration = null)
    {
        MqttSession session = capturedSession;
        _sessionsManagementLock.EnterReadLock();
        try { if (session == null && !_sessionsStorage.TryGetSession(clientId, out session)) return; }
        finally { _sessionsManagementLock.ExitReadLock(); }
        var persist = session.HasDurablePersistence;
        var generation = session.BeginDurableDeletion(expectedGeneration);
        if (generation < 0) return;
        MqttConnectedClient connection;
        lock (_clients) { _clients.TryGetValue(clientId, out connection); }
        using var timeout = new CancellationTokenSource(_options.DefaultCommunicationTimeout);
        if (connection != null && ReferenceEquals(connection.Session, session))
            await connection.QuiesceForRecoveryAsync(timeout.Token).ConfigureAwait(false);
        if (persist) await session.DeleteDurableSessionAsync(timeout.Token).ConfigureAwait(false);
        _sessionsManagementLock.EnterWriteLock();
        try
        {
            if (!session.IsCurrentConnection(generation) || !_sessionsStorage.TryGetSession(clientId, out var current) || !ReferenceEquals(current, session)) return;
            _sessionsStorage.TryRemoveSession(clientId, out _);
            _subscriberSessions.Remove(session);
        }
        finally { _sessionsManagementLock.ExitWriteLock(); }
        lock (_clients)
            if (_clients.TryGetValue(clientId, out var registered) && ReferenceEquals(registered, connection)) _clients.Remove(clientId);
        if (_eventContainer.SessionDeletedEvent.HasHandlers)
            await _eventContainer.SessionDeletedEvent.TryInvokeAsync(new SessionDeletedEventArgs(clientId, session.UserName, session.Items), _logger).ConfigureAwait(false);
        session.Dispose();
    }

    public async Task<DispatchApplicationMessageResult> DispatchApplicationMessage(
        string senderId,
        string senderUserName,
        IDictionary senderSessionItems,
        MqttApplicationMessage applicationMessage,
        CancellationToken cancellationToken)
    {
        var processPublish = true;
        var closeConnection = false;
        string reasonString = null;
        List<MqttUserProperty> userProperties = null;
        var reasonCode = 0; // The reason code is later converted into several different but compatible enums!

        // Allow the user to intercept application message...
        if (_eventContainer.InterceptingPublishEvent.HasHandlers)
        {
            var interceptingPublishEventArgs = new InterceptingPublishEventArgs(applicationMessage, senderId, senderUserName, senderSessionItems, cancellationToken);
            if (string.IsNullOrEmpty(interceptingPublishEventArgs.ApplicationMessage.Topic))
            {
                // This can happen if a topic alias us used but the topic is
                // unknown to the server.
                interceptingPublishEventArgs.Response.ReasonCode = MqttPubAckReasonCode.TopicNameInvalid;
                interceptingPublishEventArgs.ProcessPublish = false;
            }

            await _eventContainer.InterceptingPublishEvent.InvokeAsync(interceptingPublishEventArgs).ConfigureAwait(false);

            applicationMessage = interceptingPublishEventArgs.ApplicationMessage;
            closeConnection = interceptingPublishEventArgs.CloseConnection;
            processPublish = interceptingPublishEventArgs.ProcessPublish;
            reasonString = interceptingPublishEventArgs.Response.ReasonString;
            userProperties = interceptingPublishEventArgs.Response.UserProperties;
            reasonCode = (int)interceptingPublishEventArgs.Response.ReasonCode;
        }

        // Process the application message...
        if (processPublish && applicationMessage != null)
        {
            var matchingSubscribersCount = 0;
            try
            {
                if (applicationMessage.Retain)
                {
                    await _retainedMessagesManager.UpdateMessage(senderId, applicationMessage).ConfigureAwait(false);
                }

                List<MqttSession> subscriberSessions;
                _sessionsManagementLock.EnterReadLock();
                try
                {
                    subscriberSessions = _subscriberSessions.ToList();
                }
                finally
                {
                    _sessionsManagementLock.ExitReadLock();
                }

                // Calculate application message topic hash once for subscription checks
                MqttTopicHash.Calculate(applicationMessage.Topic, out var topicHash, out _, out _);

                foreach (var session in subscriberSessions)
                {
                    if (!session.TryCheckSubscriptions(applicationMessage.Topic, topicHash, applicationMessage.QualityOfServiceLevel, senderId, out var checkSubscriptionsResult))
                    {
                        // Checking the subscriptions has failed for the session. The session
                        // will be ignored.
                        continue;
                    }

                    if (!checkSubscriptionsResult.IsSubscribed)
                    {
                        continue;
                    }

                    var accepted = await session.EnqueueApplicationMessageAsync(senderId, applicationMessage, () =>
                    {
                        var publishPacketCopy = MqttPublishPacketFactory.Create(applicationMessage);
                        publishPacketCopy.QualityOfServiceLevel = checkSubscriptionsResult.QualityOfServiceLevel;
                        publishPacketCopy.SubscriptionIdentifiers = checkSubscriptionsResult.SubscriptionIdentifiers;
                        if (publishPacketCopy.QualityOfServiceLevel > 0)
                        {
                            publishPacketCopy.PacketIdentifier = session.PacketIdentifierProvider.GetNextPacketIdentifier();
                        }

                        publishPacketCopy.Retain = checkSubscriptionsResult.RetainAsPublished && applicationMessage.Retain;
                        return publishPacketCopy;
                    }).ConfigureAwait(false);
                    if (!accepted)
                    {
                        continue;
                    }

                    matchingSubscribersCount++;
                    _logger.Verbose("Client '{0}': Queued PUBLISH packet with topic '{1}'", session.Id, applicationMessage.Topic);
                }

                if (matchingSubscribersCount == 0)
                {
                    if (reasonCode == (int)MqttPubAckReasonCode.Success)
                    {
                        // Only change the value if it was success. Otherwise, we would hide an error or not authorized status.
                        reasonCode = (int)MqttPubAckReasonCode.NoMatchingSubscribers;
                    }

                    await FireApplicationMessageNotConsumedEvent(applicationMessage, senderId).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Error while processing next queued application message");
            }
        }

        return new DispatchApplicationMessageResult(reasonCode, closeConnection, reasonString, userProperties);
    }

    public void Dispose()
    {
        CloseAllConnections(new MqttServerClientDisconnectOptions()).GetAwaiter().GetResult();
        _willMessages.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _createConnectionSyncRoot.Dispose();

        _sessionsManagementLock.EnterWriteLock();
        try
        {
            _sessionsStorage.Dispose();
        }
        finally
        {
            _sessionsManagementLock.ExitWriteLock();
        }

        _sessionsManagementLock?.Dispose();
    }

    public MqttConnectedClient GetClient(string id)
    {
        lock (_clients)
        {
            if (!_clients.TryGetValue(id, out var client))
            {
                throw new InvalidOperationException($"Client with ID '{id}' not found.");
            }

            return client;
        }
    }

    public List<MqttConnectedClient> GetClients()
    {
        lock (_clients)
        {
            return _clients.Values.ToList();
        }
    }

    public Task<IList<MqttClientStatus>> GetClientsStatus()
    {
        var result = new List<MqttClientStatus>();

        lock (_clients)
        {
            foreach (var client in _clients.Values)
            {
                var clientStatus = new MqttClientStatus(client)
                {
                    Session = new MqttSessionStatus(client.Session)
                };

                result.Add(clientStatus);
            }
        }

        return Task.FromResult((IList<MqttClientStatus>)result);
    }

    public Task<IList<MqttSessionStatus>> GetSessionsStatus()
    {
        var result = new List<MqttSessionStatus>();

        _sessionsManagementLock.EnterReadLock();
        try
        {
            foreach (var session in _sessionsStorage.ReadAllSessions())
            {
                var sessionStatus = new MqttSessionStatus(session);
                result.Add(sessionStatus);
            }
        }
        finally
        {
            _sessionsManagementLock.ExitReadLock();
        }

        return Task.FromResult((IList<MqttSessionStatus>)result);
    }

    public Task<MqttSessionStatus> GetSessionStatus(string id)
    {
        _sessionsManagementLock.EnterReadLock();
        try
        {
            if (!_sessionsStorage.TryGetSession(id, out var session))
            {
                throw new InvalidOperationException($"Session with ID '{id}' not found.");
            }

            var sessionStatus = new MqttSessionStatus(session);
            return Task.FromResult(sessionStatus);
        }
        finally
        {
            _sessionsManagementLock.ExitReadLock();
        }
    }

    public async Task HandleClientConnectionAsync(IMqttChannelAdapter channelAdapter, CancellationToken cancellationToken)
    {
        MqttConnectedClient connectedClient = null;

        try
        {
            var connectPacket = await ReceiveConnectPacket(channelAdapter, cancellationToken).ConfigureAwait(false);
            if (connectPacket == null)
            {
                // Nothing was received in time etc.
                return;
            }

            var validatingConnectionEventArgs = await ValidateConnection(connectPacket, channelAdapter, cancellationToken).ConfigureAwait(false);
            var connAckPacket = MqttConnAckPacketFactory.Create(validatingConnectionEventArgs);

            if (validatingConnectionEventArgs.ReasonCode != MqttConnectReasonCode.Success)
            {
                // Send failure response here without preparing a connection and session!
                await channelAdapter.SendPacketAsync(connAckPacket, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Pass connAckPacket so that IsSessionPresent flag can be set if the client session already exists.
            connectedClient = await CreateClientConnection(connectPacket, connAckPacket, channelAdapter, validatingConnectionEventArgs).ConfigureAwait(false);

            // The exclusive application owner must never run under either global connection/session gate.
            if (_eventContainer.PreparingSessionRecoveryHandler != null)
                await PrepareSessionRecoveryAsync(connectedClient, connAckPacket, validatingConnectionEventArgs, cancellationToken).ConfigureAwait(false);

            await connectedClient.SendPacketAsync(connAckPacket, cancellationToken).ConfigureAwait(false);

            if (_eventContainer.ClientConnectedEvent.HasHandlers)
            {
                var eventArgs = new ClientConnectedEventArgs(
                    connectPacket,
                    channelAdapter.PacketFormatterAdapter.ProtocolVersion,
                    channelAdapter.RemoteEndPoint,
                    connectedClient.Session.Items) { ConnectionAttemptId = connectedClient.ConnectionAttemptId };

                await _eventContainer.ClientConnectedEvent.TryInvokeAsync(eventArgs, _logger).ConfigureAwait(false);
            }

            await connectedClient.RunAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Error(exception, exception.Message);
        }
        finally
        {
            if (connectedClient != null)
            {
                if (connectedClient.Session.HasDurablePersistence)
                {
                    using var persistenceTimeout = new CancellationTokenSource(_options.DefaultCommunicationTimeout);
                    try { await connectedClient.Session.PersistDisconnectedAsync(connectedClient.ConnectionGeneration, persistenceTimeout.Token).ConfigureAwait(false); }
                    catch (Exception exception) { _logger.Error(exception, "Durable disconnection failed; the native session remains blocked pending authoritative restoration."); }
                }
                _willMessages.Disconnected(connectedClient);
                if (connectedClient.Id != null)
                {
                    // in case it is a takeover _clientConnections already contains the new connection
                    var removed = false;
                    lock (_clients)
                    {
                        if (!connectedClient.IsTakenOver && _clients.TryGetValue(connectedClient.Id, out var registered) && ReferenceEquals(registered, connectedClient))
                        {
                            _clients.Remove(connectedClient.Id);
                            removed = true;
                        }
                    }
                    if (removed && (!_options.EnablePersistentSessions || !ShouldPersistSession(connectedClient)))
                        await DeleteDisconnectedSessionAsync(connectedClient).ConfigureAwait(false);
                }

                var endpoint = connectedClient.RemoteEndPoint;

                if (connectedClient.Id != null && !connectedClient.IsTakenOver && _eventContainer.ClientDisconnectedEvent.HasHandlers)
                {
                    var disconnectType = connectedClient.DisconnectPacket != null ? MqttClientDisconnectType.Clean : MqttClientDisconnectType.NotClean;
                    var eventArgs = new ClientDisconnectedEventArgs(
                        connectedClient.ConnectPacket,
                        connectedClient.DisconnectPacket,
                        disconnectType,
                        endpoint,
                        connectedClient.Session.Items) { ConnectionAttemptId = connectedClient.ConnectionAttemptId };

                    await _eventContainer.ClientDisconnectedEvent.InvokeAsync(eventArgs).ConfigureAwait(false);
                }
            }

            using var timeout = new CancellationTokenSource(_options.DefaultCommunicationTimeout);
            await channelAdapter.DisconnectAsync(timeout.Token).ConfigureAwait(false);
        }
    }

    public void OnSubscriptionsAdded(MqttSession clientSession, List<string> subscriptionsTopics)
    {
        _sessionsManagementLock.EnterWriteLock();
        try
        {
            if (!clientSession.HasSubscribedTopics)
            {
                // first subscribed topic
                _subscriberSessions.Add(clientSession);
            }

            foreach (var topic in subscriptionsTopics)
            {
                clientSession.AddSubscribedTopic(topic);
            }
        }
        finally
        {
            _sessionsManagementLock.ExitWriteLock();
        }
    }

    async Task DeleteDisconnectedSessionAsync(MqttConnectedClient client)
    {
        if (_options.SessionPersistence != null)
        {
            await DeleteDurableSessionCoreAsync(client.Id, client.Session, client.ConnectionGeneration).ConfigureAwait(false);
            return;
        }
        var session = client.Session;
        _sessionsManagementLock.EnterWriteLock();
        try
        {
            if (!session.IsCurrentConnection(client.ConnectionGeneration) || !_sessionsStorage.TryGetSession(client.Id, out var current) || !ReferenceEquals(current, session)) return;
            _sessionsStorage.TryRemoveSession(client.Id, out _);
            _subscriberSessions.Remove(session);
        }
        finally { _sessionsManagementLock.ExitWriteLock(); }
        if (_eventContainer.SessionDeletedEvent.HasHandlers)
            await _eventContainer.SessionDeletedEvent.TryInvokeAsync(new SessionDeletedEventArgs(client.Id, session.UserName, session.Items), _logger).ConfigureAwait(false);
        session.Dispose();
    }

    internal void RefreshRestoredSubscriptions(MqttSession session, long generation)
    {
        _sessionsManagementLock.EnterWriteLock();
        try
        {
            if (!session.IsCurrentConnection(generation)) return;
            if (session.HasSubscribedTopics) _subscriberSessions.Add(session);
            else _subscriberSessions.Remove(session);
        }
        finally { _sessionsManagementLock.ExitWriteLock(); }
    }

    public void OnSubscriptionsRemoved(MqttSession clientSession, List<string> subscriptionTopics)
    {
        _sessionsManagementLock.EnterWriteLock();
        try
        {
            foreach (var subscriptionTopic in subscriptionTopics)
            {
                clientSession.RemoveSubscribedTopic(subscriptionTopic);
            }

            if (!clientSession.HasSubscribedTopics)
            {
                // last subscription removed
                _subscriberSessions.Remove(clientSession);
            }
        }
        finally
        {
            _sessionsManagementLock.ExitWriteLock();
        }
    }

    internal Task NotifyApplicationMessagesInvalidatedAsync(SessionApplicationMessagesInvalidatedEventArgs eventArgs)
    {
        return _eventContainer.SessionApplicationMessagesInvalidatedEvent.TryInvokeAsync(eventArgs, _logger);
    }

    public void Start()
    {
        if (!_options.EnablePersistentSessions)
        {
            _sessionsStorage.Clear();
        }
    }

    public async Task SubscribeAsync(string clientId, ICollection<MqttTopicFilter> topicFilters)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(topicFilters);

        var fakeSubscribePacket = new MqttSubscribePacket();
        fakeSubscribePacket.TopicFilters.AddRange(topicFilters);

        var clientSession = GetClientSession(clientId);

        var subscribeResult = await clientSession.Subscribe(fakeSubscribePacket, CancellationToken.None).ConfigureAwait(false);

        if (subscribeResult.RetainedMessages == null)
        {
            return;
        }

        foreach (var retainedMessageMatch in subscribeResult.RetainedMessages)
        {
            await clientSession.EnqueueApplicationMessageAsync(string.Empty, retainedMessageMatch.ApplicationMessage,
                () => MqttPublishPacketFactory.Create(retainedMessageMatch)).ConfigureAwait(false);
        }
    }

    public Task UnsubscribeAsync(string clientId, ICollection<string> topicFilters)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(topicFilters);

        var fakeUnsubscribePacket = new MqttUnsubscribePacket();
        fakeUnsubscribePacket.TopicFilters.AddRange(topicFilters);

        return GetClientSession(clientId).Unsubscribe(fakeUnsubscribePacket, CancellationToken.None);
    }

    MqttConnectedClient CreateClient(MqttConnectPacket connectPacket, IMqttChannelAdapter channelAdapter, MqttSession session)
    {
        return new MqttConnectedClient(connectPacket, channelAdapter, session, _options, _eventContainer, this, _rootLogger);
    }

    async Task PrepareSessionRecoveryAsync(MqttConnectedClient client, MqttConnAckPacket connAck, ValidatingConnectionEventArgs validation, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.DefaultCommunicationTimeout);
        await client.Session.WaitForRecoveryOwnerAsync(timeout.Token).ConfigureAwait(false);
        if (client.Session.HasDurablePersistence)
            connAck.IsSessionPresent = await client.Session.RestoreDurableStateAsync(new MqttSessionPersistenceRequest(client.Id, client.ConnectionAttemptId, validation.SessionItems,
                client.ConnectPacket.CleanSession, client.ChannelAdapter.PacketFormatterAdapter.ProtocolVersion, client.ConnectPacket.SessionExpiryInterval), client.ConnectionGeneration, timeout.Token).ConfigureAwait(false);
        var lease = client.Session.BeginRecovery(client.ConnectionGeneration, timeout.Token);
        var preparation = new PreparingSessionRecoveryEventArgs(
            new MqttSessionStatus(client.Session), lease, connAck.IsSessionPresent, client.ConnectPacket.CleanSession, validation.SessionItems,
            new MqttClientStatus(client) { Session = new MqttSessionStatus(client.Session) });
        Task ownerTask;
        bool externalWill;
        try
        {
            ownerTask = _eventContainer.PreparingSessionRecoveryHandler(preparation);
            if (ownerTask == null) throw new InvalidOperationException("The session recovery owner returned no task.");
        }
        catch
        {
            preparation.CloseWillPreparation();
            lease.Cancel();
            await client.Session.FinalizeRecoveryAsync(lease, false).ConfigureAwait(false);
            throw;
        }

        try
        {
            await ownerTask.WaitAsync(lease.CancellationToken).ConfigureAwait(false);
            lease.CancellationToken.ThrowIfCancellationRequested();
            if (!lease.IsCommitted) throw new InvalidOperationException("The session recovery owner did not commit its preparation.");
            externalWill = preparation.CloseWillPreparation();
        }
        catch
        {
            preparation.CloseWillPreparation();
            lease.Cancel();
            if (ownerTask.IsCompleted) await client.Session.FinalizeRecoveryAsync(lease, false).ConfigureAwait(false);
            else _ = FinalizeAbandonedRecoveryAsync(client.Session, lease, ownerTask);
            throw;
        }
        await client.Session.FinalizeRecoveryAsync(lease, true).ConfigureAwait(false);
        lease.CancellationToken.ThrowIfCancellationRequested();
        if (!client.Session.IsCurrentConnection(lease.ConnectionGeneration)) throw new InvalidOperationException("Session recovery was superseded.");
        _willMessages.CompletePreparation(client, externalWill);
    }

    async Task FinalizeAbandonedRecoveryAsync(MqttSession session, MqttSessionRecoveryLease lease, Task ownerTask)
    {
        try { await ownerTask.ConfigureAwait(false); }
        catch (Exception exception) { _logger.Error(exception, "Abandoned session recovery owner failed."); }
        try { await session.FinalizeRecoveryAsync(lease, false).ConfigureAwait(false); }
        catch (Exception exception) { _logger.Error(exception, "Abandoned session recovery finalization failed."); }
    }

    async Task<MqttConnectedClient> CreateClientConnection(
        MqttConnectPacket connectPacket,
        MqttConnAckPacket connAckPacket,
        IMqttChannelAdapter channelAdapter,
        ValidatingConnectionEventArgs validatingConnectionEventArgs)
    {
        MqttConnectedClient connectedClient;

        using (await _createConnectionSyncRoot.EnterAsync().ConfigureAwait(false))
        {
            MqttSession oldSession;
            MqttConnectedClient oldConnectedClient;

            lock (_clients) { _clients.TryGetValue(connectPacket.ClientId, out oldConnectedClient); }
            if (oldConnectedClient != null)
            {
                using var quiesceTimeout = new CancellationTokenSource(_options.DefaultCommunicationTimeout);
                await oldConnectedClient.QuiesceForRecoveryAsync(quiesceTimeout.Token).ConfigureAwait(false);
            }

            _sessionsManagementLock.EnterWriteLock();
            try
            {
                MqttSession session;

                // Create a new session (if required).
                if (!_sessionsStorage.TryGetSession(connectPacket.ClientId, out oldSession))
                {
                    session = CreateSession(connectPacket, validatingConnectionEventArgs);
                }
                else
                {
                    if (connectPacket.CleanSession)
                    {
                        _logger.Verbose("Deleting existing session of client '{0}' due to clean start", connectPacket.ClientId);
                        _subscriberSessions.Remove(oldSession);
                        session = CreateSession(connectPacket, validatingConnectionEventArgs);
                    }
                    else
                    {
                        _logger.Verbose("Reusing existing session of client '{0}'", connectPacket.ClientId);
                        session = oldSession;
                        oldSession = null;

                        session.DisconnectedTimestamp = null;
                        // Fence old wire callbacks before rebuilding the persistent session's queue/state.
                        session.ActivateConnection();
                        if (_eventContainer.PreparingSessionRecoveryHandler == null) session.Recover();

                        connAckPacket.IsSessionPresent = true;
                    }
                }

                _sessionsStorage.UpdateSession(connectPacket.ClientId, session);

                // Create a new client (always required).
                lock (_clients)
                {
                    _clients.TryGetValue(connectPacket.ClientId, out oldConnectedClient);
                    if (oldConnectedClient != null)
                    {
                        // This will stop the current client from sending and receiving but remains the connection active
                        // for a later DISCONNECT packet.
                        oldConnectedClient.IsTakenOver = true;
                    }

                    connectedClient = CreateClient(connectPacket, channelAdapter, session);
                    connectedClient.ConnectionAttemptId = validatingConnectionEventArgs.ConnectionAttemptId;
                    connectedClient.WillMessage = validatingConnectionEventArgs.WillMessage;
                    _clients[connectPacket.ClientId] = connectedClient;
                    _willMessages.Connected(connectedClient, _eventContainer.PreparingSessionRecoveryHandler != null);
                }
            }
            finally
            {
                _sessionsManagementLock.ExitWriteLock();
            }

            if (!connAckPacket.IsSessionPresent)
            {
                // TODO: This event is not yet final. It can already be used but restoring sessions from storage will be added later!
                var preparingSessionEventArgs = new PreparingSessionEventArgs();
                await _eventContainer.PreparingSessionEvent.TryInvokeAsync(preparingSessionEventArgs, _logger).ConfigureAwait(false);
            }

            if (oldConnectedClient != null)
            {
                // TODO: Consider event here for session takeover to allow manipulation of user properties etc.
                await oldConnectedClient.StopAsync(new MqttServerClientDisconnectOptions { ReasonCode = MqttDisconnectReasonCode.SessionTakenOver }).ConfigureAwait(false);

                if (_eventContainer.ClientDisconnectedEvent.HasHandlers)
                {
                    var eventArgs = new ClientDisconnectedEventArgs(
                        oldConnectedClient.ConnectPacket,
                        null,
                        MqttClientDisconnectType.Takeover,
                        oldConnectedClient.RemoteEndPoint,
                        oldConnectedClient.Session.Items) { ConnectionAttemptId = oldConnectedClient.ConnectionAttemptId };

                    await _eventContainer.ClientDisconnectedEvent.TryInvokeAsync(eventArgs, _logger).ConfigureAwait(false);
                }
            }

            oldSession?.Dispose();
        }

        return connectedClient;
    }

    MqttSession CreateSession(MqttConnectPacket connectPacket, ValidatingConnectionEventArgs validatingConnectionEventArgs)
    {
        _logger.Verbose("Created new session for client '{0}'", connectPacket.ClientId);

        return new MqttSession(connectPacket, validatingConnectionEventArgs.SessionItems, _options, _eventContainer, _retainedMessagesManager, this);
    }

    async Task FireApplicationMessageNotConsumedEvent(MqttApplicationMessage applicationMessage, string senderId)
    {
        if (!_eventContainer.ApplicationMessageNotConsumedEvent.HasHandlers)
        {
            return;
        }

        var eventArgs = new ApplicationMessageNotConsumedEventArgs(applicationMessage, senderId);
        await _eventContainer.ApplicationMessageNotConsumedEvent.InvokeAsync(eventArgs).ConfigureAwait(false);
    }

    MqttSession GetClientSession(string clientId)
    {
        _sessionsManagementLock.EnterReadLock();
        try
        {
            if (!_sessionsStorage.TryGetSession(clientId, out var session))
            {
                throw new InvalidOperationException($"Client session '{clientId}' is unknown.");
            }

            return session;
        }
        finally
        {
            _sessionsManagementLock.ExitReadLock();
        }
    }

    async Task<MqttConnectPacket> ReceiveConnectPacket(IMqttChannelAdapter channelAdapter, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutToken = new CancellationTokenSource(_options.DefaultCommunicationTimeout);
            using var effectiveCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(timeoutToken.Token, cancellationToken);
            var firstPacket = await channelAdapter.ReceivePacketAsync(effectiveCancellationToken.Token).ConfigureAwait(false);
            if (firstPacket is MqttConnectPacket connectPacket)
            {
                return connectPacket;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("Client '{0}': Connected but did not sent a CONNECT packet.", channelAdapter.RemoteEndPoint);
        }
        catch (MqttCommunicationTimedOutException)
        {
            _logger.Warning("Client '{0}': Connected but did not sent a CONNECT packet.", channelAdapter.RemoteEndPoint);
        }

        _logger.Warning("Client '{0}': First received packet was no 'CONNECT' packet [MQTT-3.1.0-1].", channelAdapter.RemoteEndPoint);
        return null;
    }

    static bool ShouldPersistSession(MqttConnectedClient connectedClient)
    {
        switch (connectedClient.ChannelAdapter.PacketFormatterAdapter.ProtocolVersion)
        {
            case MqttProtocolVersion.V500:
            {
                // MQTT 5.0 section 3.1.2.11.2
                // The Client and Server MUST store the Session State after the Network Connection is closed if the Session Expiry Interval is greater than 0 [MQTT-3.1.2-23].
                //
                // A Client that only wants to process messages while connected will set the Clean Start to 1 and set the Session Expiry Interval to 0.
                // It will not receive Application Messages published before it connected and has to subscribe afresh to any topics that it is interested
                // in each time it connects.

                return connectedClient.Session.ExpiryInterval != 0U;
            }

            case MqttProtocolVersion.V311:
            {
                // MQTT 3.1.1 section 3.1.2.4: persist only if 'not CleanSession'
                //
                // If CleanSession is set to 1, the Client and Server MUST discard any previous Session and start a new one.
                // This Session lasts as long as the Network Connection. State data associated with this Session MUST NOT be
                // reused in any subsequent Session [MQTT-3.1.2-6].

                return !connectedClient.ConnectPacket.CleanSession;
            }

            case MqttProtocolVersion.V310:
            {
                return true;
            }

            default:
                throw new NotSupportedException();
        }
    }

    async Task<bool> ShouldSkipEnqueue(string senderId, string clientId, MqttApplicationMessage applicationMessage)
    {
        if (!_eventContainer.InterceptingClientEnqueueEvent.HasHandlers)
        {
            return false;
        }

        var eventArgs = new InterceptingClientApplicationMessageEnqueueEventArgs(senderId, clientId, applicationMessage);
        await _eventContainer.InterceptingClientEnqueueEvent.InvokeAsync(eventArgs).ConfigureAwait(false);

        return !eventArgs.AcceptEnqueue;
    }

    async Task<ValidatingConnectionEventArgs> ValidateConnection(MqttConnectPacket connectPacket, IMqttChannelAdapter channelAdapter, CancellationToken cancellationToken)
    {
        // TODO: Load session items from persisted sessions in the future.
        var sessionItems = new ConcurrentDictionary<object, object>();
        var eventArgs = new ValidatingConnectionEventArgs(connectPacket, channelAdapter, sessionItems, cancellationToken);
        await _eventContainer.ValidatingConnectionEvent.InvokeAsync(eventArgs).ConfigureAwait(false);

        // Check the client ID and set a random one if supported.
        if (string.IsNullOrEmpty(connectPacket.ClientId) && channelAdapter.PacketFormatterAdapter.ProtocolVersion == MqttProtocolVersion.V500)
        {
            connectPacket.ClientId = eventArgs.AssignedClientIdentifier;
        }

        if (string.IsNullOrEmpty(connectPacket.ClientId))
        {
            eventArgs.ReasonCode = MqttConnectReasonCode.ClientIdentifierNotValid;
        }

        return eventArgs;
    }
}
