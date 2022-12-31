using System;
using System.Collections.Generic;
using System.Text;

namespace MQTTnet.Server.Internal
{
    public interface ISharedSubscriptionChangedNotification
    {

        MqttSession ResolveClientSession(string sessionId);

    }
}
