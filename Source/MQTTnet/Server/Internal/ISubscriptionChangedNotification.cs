using System.Collections.Generic;

namespace MQTTnet.Server
{
    public interface ISubscriptionChangedNotification
    {
        void OnSubscriptionsAdded(MqttSession clientSession, List<string> subscriptionsTopics);

        void OnSubscriptionsRemoved(MqttSession clientSession, List<string> subscriptionTopics);

        MqttSharedSubscription OnResolveSharedSubscription(MqttSharedSubscriptionTopicKey sharedSubscription);

        void OnSharedSubscriptionsAdded(MqttSession clientSession, List<MqttSharedSubscription> sharedSubscriptions);
        
        void OnSharedSubscriptionsRemoved(MqttSession clientSession, List<MqttSharedSubscriptionTopicKey> sharedSubscriptions);

    }
}