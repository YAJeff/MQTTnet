using MQTTnet.Client;
using MQTTnet.Internal;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MQTTnet.Server.Internal;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MQTTnet.Server
{
    public sealed class MqttSharedSubscription : MqttSubscription
    {
        readonly MqttPacketBus _packetBus = new MqttPacketBus();
        readonly MqttServerOptions _serverOptions;
        readonly ISharedSubscriptionChangedNotification _sharedSubscriptionChangedNotification;

        // Do not use a dictionary in order to keep the ordering of the messages.
        readonly List<MqttPublishPacket> _unacknowledgedPublishPackets = new List<MqttPublishPacket>();
                
        readonly object _distributionLock = new object();
        List<MqttSession> _currentRotation;
        int _currentRotationPosition = -1;
        
        public MqttSharedSubscription(
            string shareName, 
            string topic,
            bool noLocal,
            MqttRetainHandling retainHandling,
            bool retainAsPublished,
            MqttQualityOfServiceLevel qualityOfServiceLevel,
            uint identifier,
            MqttServerOptions serverOptions,
            ISharedSubscriptionChangedNotification sharedSubscriptionChangedNotification)
            : base(
                topic,
                noLocal,
                retainHandling,
                retainAsPublished,
                qualityOfServiceLevel,
                identifier)
        {
            ShareName = shareName;
            _serverOptions = serverOptions;
            _sharedSubscriptionChangedNotification = sharedSubscriptionChangedNotification;
        }

        public string ShareName { get; }

        public HashSet<MqttSession> Sessions { get; } = new HashSet<MqttSession>();

        public void EnqueueDataPacket(MqttPacketBusItem packetBusItem)
        {
            _ = Task.Run(() => ExecuteDataPacketDeliveryAsync(packetBusItem));
        }

        private async Task ExecuteDataPacketDeliveryAsync(MqttPacketBusItem packetBusItem)
        {
            var sessionDeliveryAttempts = 0;
            var currentRotationSize = 0;
            do
            {
                MqttSession clientSession;
                lock (_distributionLock)
                {
                    if (_currentRotation == null || ++_currentRotationPosition >= _currentRotation.Count)
                    {
                        _currentRotation = Sessions.ToList();
                        _currentRotationPosition = 0;
                    }
                    currentRotationSize = _currentRotation.Count;
                    clientSession = _currentRotation[_currentRotationPosition];
                }

                clientSession.EnqueueDataPacket(packetBusItem);

                sessionDeliveryAttempts++;

                try
                {
                    await packetBusItem.WaitAsync().ConfigureAwait(false);

                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // TODO: Add logging
                }
            } while (sessionDeliveryAttempts < currentRotationSize);
        }

        public void AddSession(MqttSession session)
        {
            lock (_distributionLock)
            {
                Sessions.Add(session);
            }
        }

        public void RemoveSession(MqttSession session)
        {
            lock (_distributionLock)
            {
                Sessions.Remove(session);
                if (_currentRotation != null)
                {
                    var sessionIndex = _currentRotation.IndexOf(session);
                    if (sessionIndex != -1)
                        _currentRotation.RemoveAt(sessionIndex);
                    else if (sessionIndex < _currentRotationPosition)
                        _currentRotationPosition--;
                }
            }
        }

    }
}
