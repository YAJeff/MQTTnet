// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace MQTTnet.Server
{
    public sealed class MqttServerOptions
    {
        public TimeSpan DefaultCommunicationTimeout { get; set; } = TimeSpan.FromSeconds(100);

        public MqttServerTcpEndpointOptions DefaultEndpointOptions { get; } = new MqttServerTcpEndpointOptions();

        public bool EnablePersistentSessions { get; set; }

        public TimeSpan KeepAliveMonitorInterval { get; set; } = TimeSpan.FromMilliseconds(500);

        public int MaxPendingMessagesPerClient { get; set; } = 250;

        public MqttPendingMessagesOverflowStrategy PendingMessagesOverflowStrategy { get; set; } = MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage;

        public MqttServerTlsTcpEndpointOptions TlsEndpointOptions { get; } = new MqttServerTlsTcpEndpointOptions();

        /// <summary>
        ///     Gets or sets the default and initial size of the packet write buffer.
        ///     It is recommended to set this to a value close to the usual expected packet size * 1.5.
        ///     Do not change this value when no memory issues are experienced.
        /// </summary>
        public int WriterBufferSize { get; set; } = 4096;

        /// <summary>
        ///     Gets or sets the maximum size of the buffer writer. The writer will reduce its internal buffer
        ///     to this value after serializing a packet.
        ///     Do not change this value when no memory issues are experienced.
        /// </summary>
        public int WriterBufferSizeMax { get; set; } = 65535;

        /// <summary>
        /// Gets or sets a value that indicates whether messages published to standard topics should be passed
        /// along to shared subscriptions.
        /// </summary>
        public bool RelayMessagesToSharedSubscriptions { get; set; } = true;

        /// <summary>
        /// Gets or sets a value that indicates if publishing directly to shared subscription topics is allowed.
        /// </summary>
        public bool AllowPublishingToSharedSubscriptions { get; set; } = false;

    }
}