using System;
using System.Collections.Generic;
using System.Text;

namespace MQTTnet.Server
{
    public class TopicHashMaskSharedSubscriptions
    {
        public Dictionary<ulong, Dictionary<MqttSharedSubscriptionTopicKey, MqttSharedSubscription>> SubscriptionsByHashMask { get; } = new Dictionary<ulong, Dictionary<MqttSharedSubscriptionTopicKey, MqttSharedSubscription>>();

        public void AddSubscription(MqttSharedSubscription subscription)
        {
            if (!SubscriptionsByHashMask.TryGetValue(subscription.TopicHashMask, out var subscriptions))
            {
                subscriptions = new Dictionary<MqttSharedSubscriptionTopicKey, MqttSharedSubscription>();
                SubscriptionsByHashMask.Add(subscription.TopicHashMask, subscriptions);
            }
            subscriptions.Add(MqttSharedSubscriptionTopicKey.Create(subscription.ShareName, subscription.Topic), subscription);
        }

        public void RemoveSubscription(MqttSharedSubscription subscription)
        {
            if (SubscriptionsByHashMask.TryGetValue(subscription.TopicHashMask, out var subscriptions))
            {
                subscriptions.Remove(MqttSharedSubscriptionTopicKey.Create(subscription.ShareName, subscription.Topic));
                if (subscriptions.Count == 0)
                {
                    SubscriptionsByHashMask.Remove(subscription.TopicHashMask);
                }
            }
        }


    }
}
