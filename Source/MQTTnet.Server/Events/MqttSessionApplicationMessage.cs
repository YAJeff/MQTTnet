// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using MQTTnet.Packets;

namespace MQTTnet.Server;

public sealed class MqttSessionApplicationMessage
{
    public MqttSessionApplicationMessage(MqttPublishPacket publishPacket, object enqueueState)
    {
        PublishPacket = publishPacket ?? throw new ArgumentNullException(nameof(publishPacket));
        EnqueueState = enqueueState;
    }

    public MqttPublishPacket PublishPacket { get; }

    public object EnqueueState { get; }
}
