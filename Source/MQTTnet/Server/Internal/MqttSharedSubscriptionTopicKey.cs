using System;
using System.Collections.Generic;
using System.Text;

namespace MQTTnet.Server
{
    public struct MqttSharedSubscriptionTopicKey : IComparable<MqttSharedSubscriptionTopicKey>
    {

        private MqttSharedSubscriptionTopicKey(string shareName, string topic)
        {
            ShareName = shareName;
            Topic = topic;
        }

        public static MqttSharedSubscriptionTopicKey Create(string shareName, string topic)
        {
            return new MqttSharedSubscriptionTopicKey(shareName, topic);
        }

        public static bool TryParse(string topic, out MqttSharedSubscriptionTopicKey key)
        {
            if (!topic.StartsWith("$share/"))
            {
                key = default;
                return false;
            }
            
            var shareNameEndSlashIndex = topic.IndexOf('/', 7);
            if (shareNameEndSlashIndex < 0)
            {
                key = default;
                return false;
            }

            key = Create(topic.Substring(7, shareNameEndSlashIndex - 7), topic.Substring(shareNameEndSlashIndex + 1));
            return true;
        }

        public string ShareName { get; }

        public string Topic { get; }

        public override bool Equals(object obj)
        {
            if (!(obj is MqttSharedSubscriptionTopicKey sharedSubscriptionKey))
                return false;
            return sharedSubscriptionKey.ShareName == ShareName && sharedSubscriptionKey.Topic == Topic;
        }

        public override int GetHashCode()
        {
            return ShareName.GetHashCode() ^ Topic.GetHashCode();
        }

        public override string ToString()
        {
            return $"$share/{ShareName}/{Topic}";
        }

        public int CompareTo(MqttSharedSubscriptionTopicKey other)
        {
            var result = ShareName.CompareTo(other.ShareName);
            if (result == 0)
            {
                result = Topic.CompareTo(other.Topic);
            }
            return result;
        }
    }
}
