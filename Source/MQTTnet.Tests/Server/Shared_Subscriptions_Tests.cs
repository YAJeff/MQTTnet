// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Internal;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MQTTnet.Server;
using MQTTnet.Tests.Helpers;

namespace MQTTnet.Tests.Server
{
    [TestClass]
    public sealed class Shared_Subscriptions_Tests : BaseTestClass
    {
        [TestMethod]
        public async Task Server_Reports_Shared_Subscriptions_Supported()
        {
            using (var testEnvironment = CreateTestEnvironment(MqttProtocolVersion.V500))
            {
                await testEnvironment.StartServer();

                var client = testEnvironment.CreateClient();
                var connectResult = await client.ConnectAsync(testEnvironment.Factory.CreateClientOptionsBuilder()
                    .WithProtocolVersion(MqttProtocolVersion.V500)
                    .WithTcpServer("127.0.0.1", testEnvironment.ServerPort).Build());

                Assert.IsTrue(connectResult.SharedSubscriptionAvailable);
            }
        }
        
        [TestMethod]
        public async Task Subscription_Of_Shared_Subscription_Is_Denied()
        {
            using (var testEnvironment = CreateTestEnvironment(MqttProtocolVersion.V500))
            {
                await testEnvironment.StartServer();

                var client = testEnvironment.CreateClient();
                await client.ConnectAsync(testEnvironment.Factory.CreateClientOptionsBuilder()
                    .WithProtocolVersion(MqttProtocolVersion.V500)
                    .WithTcpServer("127.0.0.1", testEnvironment.ServerPort).Build());

                var subscribeResult = await client.SubscribeAsync("$share/A");
                
                Assert.AreEqual(MqttClientSubscribeResultCode.GrantedQoS0, subscribeResult.Items.First().ResultCode);
            }
        }

        [DataTestMethod]
        [DataRow("A", "$share/mysharegroup/A", true)]
        [DataRow("A", "$share/mysharegroup/B", false)]
        [DataRow("A", "$share/mysharegroup/#", true)]
        [DataRow("A", "$share/mysharegroup/+", true)]
        [DataRow("A/B", "$share/mysharegroup/A/B", true)]
        [DataRow("A/B", "$share/mysharegroup/A/+", true)]
        [DataRow("A/B", "$share/mysharegroup/A/#", true)]
        [DataRow("A/B/C", "$share/mysharegroup/A/B/C", true)]
        [DataRow("A/B/C", "$share/mysharegroup/A/+/C", true)]
        [DataRow("A/B/C", "$share/mysharegroup/A/+/+", true)]
        [DataRow("A/B/C", "$share/mysharegroup/A/+/#", true)]
        [DataRow("A/B/C/D", "$share/mysharegroup/A/B/C/D", true)]
        [DataRow("A/B/C/D", "$share/mysharegroup/A/+/C/+", true)]
        [DataRow("A/B/C/D", "$share/mysharegroup/A/+/C/#", true)]
        [DataRow("A/B/C", "$share/mysharegroup/A/B/+", true)]
        [DataRow("A/B1/B2/C", "$share/mysharegroup/A/+/C", false)]
        public async Task Subscription_Roundtrip(string topic, string filter, bool shouldWork)
        {
            using (var testEnvironment = CreateTestEnvironment(MqttProtocolVersion.V500))
            {
                await testEnvironment.StartServer().ConfigureAwait(false);

                var receiver = await testEnvironment.ConnectClient();
                await receiver.SubscribeAsync(filter).ConfigureAwait(false);
                var receivedMessages = receiver.TrackReceivedMessages();

                var sender = await testEnvironment.ConnectClient();
                await sender.PublishStringAsync(topic, "PAYLOAD").ConfigureAwait(false);

                await LongTestDelay().ConfigureAwait(false);

                if (shouldWork)
                {
                    Assert.AreEqual(1, receivedMessages.Count, message: "The filter should work!");
                }
                else
                {
                    Assert.AreEqual(0, receivedMessages.Count, message: "The filter should not work!");
                }
            }
        }

        [TestMethod]
        public async Task Deny_Invalid_Topic()
        {
            using (var testEnvironment = CreateTestEnvironment(MqttProtocolVersion.V500))
            {
                var server = await testEnvironment.StartServer();

                server.InterceptingSubscriptionAsync += e =>
                {
                    if (e.TopicFilter.ShareName == null)
                    {
                        e.Response.ReasonCode = MqttSubscribeReasonCode.TopicFilterInvalid;
                    }
                    else if (e.TopicFilter.Topic == "not_allowed_topic" || e.TopicFilter.ShareName == "not_allowed_share_name")
                    {
                        e.Response.ReasonCode = MqttSubscribeReasonCode.TopicFilterInvalid;
                    }

                    return CompletedTask.Instance;
                };

                var client = await testEnvironment.ConnectClient();

                var subscribeResult = await client.SubscribeAsync("$share/allowed_share_name/allowed_topic");
                Assert.AreEqual(MqttClientSubscribeResultCode.GrantedQoS0, subscribeResult.Items.First().ResultCode);

                subscribeResult = await client.SubscribeAsync("$share/allowed_share_name/not_allowed_topic");
                Assert.AreEqual(MqttClientSubscribeResultCode.TopicFilterInvalid, subscribeResult.Items.First().ResultCode);

                subscribeResult = await client.SubscribeAsync("$share/not_allowed_share_name/allowed_topic");
                Assert.AreEqual(MqttClientSubscribeResultCode.TopicFilterInvalid, subscribeResult.Items.First().ResultCode);

                subscribeResult = await client.SubscribeAsync("$share/not_allowed_share_name/not_allowed_topic");
                Assert.AreEqual(MqttClientSubscribeResultCode.TopicFilterInvalid, subscribeResult.Items.First().ResultCode);
            }
        }

        [TestMethod]
        [ExpectedException(typeof(MqttClientDisconnectedException))]
        public async Task Disconnect_While_Subscribing()
        {
            using (var testEnvironment = CreateTestEnvironment())
            {
                var server = await testEnvironment.StartServer();

                // The client will be disconnect directly after subscribing!
                server.ClientSubscribedTopicAsync += ev => server.DisconnectClientAsync(ev.ClientId, MqttDisconnectReasonCode.NormalDisconnection);

                var client = await testEnvironment.ConnectClient();
                await client.SubscribeAsync("$share/mysharegroup/#");
            }
        }

        [TestMethod]
        public async Task Enqueue_Message_After_Subscription()
        {
            using (var testEnvironment = CreateTestEnvironment(MqttProtocolVersion.V500))
            {
                var server = await testEnvironment.StartServer();

                server.ClientSubscribedTopicAsync += e =>
                {
                    server.InjectApplicationMessage(new InjectedMqttApplicationMessage(new MqttApplicationMessageBuilder().WithTopic("test_topic").Build()));
                    return CompletedTask.Instance;
                };

                var client = await testEnvironment.ConnectClient();
                var receivedMessages = testEnvironment.CreateApplicationMessageHandler(client);

                await client.SubscribeAsync("$share/mysharegroup/test_topic");

                await LongTestDelay();

                Assert.AreEqual(1, receivedMessages.ReceivedEventArgs.Count);
            }
        }

        [TestMethod]
        public async Task Intercept_Subscription()
        {
            using (var testEnvironment = CreateTestEnvironment(MqttProtocolVersion.V500))
            {
                var server = await testEnvironment.StartServer();

                server.InterceptingSubscriptionAsync += e =>
                {
                    // Set the topic to "a" regards what the client wants to subscribe.
                    e.TopicFilter.Topic = "a";
                    e.TopicFilter.ShareName = "asharegroup";
                    return CompletedTask.Instance;
                };

                var topicAReceived = false;
                var topicBReceived = false;

                var client = await testEnvironment.ConnectClient();
                client.ApplicationMessageReceivedAsync += e =>
                {
                    if (e.ApplicationMessage.Topic == "a")
                    {
                        topicAReceived = true;
                    }
                    else if (e.ApplicationMessage.Topic == "b")
                    {
                        topicBReceived = true;
                    }

                    return CompletedTask.Instance;
                };

                await client.SubscribeAsync("$share/bsharegroup/b");

                await client.PublishStringAsync("a");

                await Task.Delay(500);

                Assert.IsTrue(topicAReceived);
                Assert.IsFalse(topicBReceived);
            }
        }

        [TestMethod]
        public async Task Response_Contains_Equal_Reason_Codes()
        {
            using (var testEnvironment = CreateTestEnvironment(MqttProtocolVersion.V500))
            {
                await testEnvironment.StartServer();
                var client = await testEnvironment.ConnectClient();

                var subscribeOptions = new MqttClientSubscribeOptionsBuilder().WithTopicFilter("$share/mysharegroup/a")
                    .WithTopicFilter("$share/mysharegroup/b", MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithTopicFilter("$share/mysharegroup/c", MqttQualityOfServiceLevel.ExactlyOnce)
                    .WithTopicFilter("$share/mysharegroup/d")
                    .Build();

                var response = await client.SubscribeAsync(subscribeOptions);

                Assert.AreEqual(subscribeOptions.TopicFilters.Count, response.Items.Count);
            }
        }

        [TestMethod]
        public async Task Subscribe_Lots_In_Multiple_Requests()
        {
            using (var testEnvironment = CreateTestEnvironment(MqttProtocolVersion.V500))
            {
                var receivedMessagesCount = 0;

                await testEnvironment.StartServer();

                var c1 = await testEnvironment.ConnectClient();
                c1.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessagesCount);
                    return CompletedTask.Instance;
                };

                for (var i = 0; i < 500; i++)
                {
                    var so = new MqttClientSubscribeOptionsBuilder().WithTopicFilter($"$share/mysharegroup/{i}").Build();

                    await c1.SubscribeAsync(so).ConfigureAwait(false);

                    await Task.Delay(10);
                }

                var c2 = await testEnvironment.ConnectClient();

                var messageBuilder = new MqttApplicationMessageBuilder();
                for (var i = 0; i < 500; i++)
                {
                    messageBuilder.WithTopic(i.ToString());

                    await c2.PublishAsync(messageBuilder.Build()).ConfigureAwait(false);

                    await Task.Delay(10);
                }

                SpinWait.SpinUntil(() => receivedMessagesCount == 500, 5000);

                Assert.AreEqual(500, receivedMessagesCount);
            }
        }

        [TestMethod]
        public async Task Subscribe_Lots_In_Single_Request()
        {
            using (var testEnvironment = CreateTestEnvironment(MqttProtocolVersion.V500))
            {
                var receivedMessagesCount = 0;

                await testEnvironment.StartServer();

                var c1 = await testEnvironment.ConnectClient();
                c1.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessagesCount);
                    return CompletedTask.Instance;
                };

                var optionsBuilder = new MqttClientSubscribeOptionsBuilder();
                for (var i = 0; i < 500; i++)
                {
                    optionsBuilder.WithTopicFilter($"$share/mysharegroup/{i}");
                }

                await c1.SubscribeAsync(optionsBuilder.Build()).ConfigureAwait(false);

                var c2 = await testEnvironment.ConnectClient();

                var messageBuilder = new MqttApplicationMessageBuilder();
                for (var i = 0; i < 500; i++)
                {
                    messageBuilder.WithTopic(i.ToString());

                    await c2.PublishAsync(messageBuilder.Build()).ConfigureAwait(false);
                }

                SpinWait.SpinUntil(() => receivedMessagesCount == 500, TimeSpan.FromSeconds(20));

                Assert.AreEqual(500, receivedMessagesCount);
            }
        }

        [TestMethod]
        public async Task Subscribe_Multiple_In_Multiple_Request()
        {
            using (var testEnvironment = CreateTestEnvironment(MqttProtocolVersion.V500))
            {
                var receivedMessagesCount = 0;

                await testEnvironment.StartServer();

                var c1 = await testEnvironment.ConnectClient();
                c1.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessagesCount);
                    return CompletedTask.Instance;
                };

                await c1.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("$share/mysharegroup/a").Build());

                await c1.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("$share/mysharegroup/b").Build());

                await c1.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("$share/mysharegroup/c").Build());

                var c2 = await testEnvironment.ConnectClient();

                await c2.PublishStringAsync("a");
                await Task.Delay(100);
                Assert.AreEqual(receivedMessagesCount, 1);

                await c2.PublishStringAsync("b");
                await Task.Delay(100);
                Assert.AreEqual(receivedMessagesCount, 2);

                await c2.PublishStringAsync("c");
                await Task.Delay(100);
                Assert.AreEqual(receivedMessagesCount, 3);
            }
        }

        [TestMethod]
        public async Task Subscribe_Multiple_In_Single_Request()
        {
            using (var testEnvironment = CreateTestEnvironment(MqttProtocolVersion.V500))
            {
                var receivedMessagesCount = 0;

                await testEnvironment.StartServer();

                var c1 = await testEnvironment.ConnectClient();
                c1.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessagesCount);
                    return CompletedTask.Instance;
                };

                await c1.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("$share/mysharegroup/a").WithTopicFilter("$share/mysharegroup/b").WithTopicFilter("$share/mysharegroup/c").Build());

                var c2 = await testEnvironment.ConnectClient();

                await c2.PublishStringAsync("a");
                await Task.Delay(100);
                Assert.AreEqual(receivedMessagesCount, 1);

                await c2.PublishStringAsync("b");
                await Task.Delay(100);
                Assert.AreEqual(receivedMessagesCount, 2);

                await c2.PublishStringAsync("c");
                await Task.Delay(100);
                Assert.AreEqual(receivedMessagesCount, 3);
            }
        }

        [TestMethod]
        public async Task Subscribe_Unsubscribe()
        {
            using (var testEnvironment = CreateTestEnvironment(MqttProtocolVersion.V500))
            {
                var receivedMessagesCount = 0;

                var server = await testEnvironment.StartServer();

                var c1 = await testEnvironment.ConnectClient(
                    new MqttClientOptionsBuilder()
                        .WithClientId("c1")
                        .WithProtocolVersion(MqttProtocolVersion.V500));
                c1.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessagesCount);
                    return CompletedTask.Instance;
                };

                var c2 = await testEnvironment.ConnectClient(
                    new MqttClientOptionsBuilder()
                        .WithClientId("c2")
                        .WithProtocolVersion(MqttProtocolVersion.V500));

                var message = new MqttApplicationMessageBuilder().WithTopic("a").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce).Build();
                await c2.PublishAsync(message);

                await Task.Delay(500);
                Assert.AreEqual(0, receivedMessagesCount);

                var subscribeEventCalled = false;
                server.ClientSubscribedTopicAsync += e =>
                {
                    subscribeEventCalled = e.TopicFilter.Topic == "a" && e.TopicFilter.ShareName == "mysharegroup" && e.ClientId == c1.Options.ClientId;
                    return CompletedTask.Instance;
                };

                await c1.SubscribeAsync(new MqttTopicFilter { Topic = "$share/mysharegroup/a", QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce });
                await Task.Delay(250);
                Assert.IsTrue(subscribeEventCalled, "Subscribe event not called.");

                await c2.PublishAsync(message);
                await Task.Delay(250);
                Assert.AreEqual(1, receivedMessagesCount);

                var unsubscribeEventCalled = false;
                server.ClientUnsubscribedTopicAsync += e =>
                {
                    unsubscribeEventCalled = e.TopicFilter == "a" && e.ShareName == "mysharegroup" && e.ClientId == c1.Options.ClientId;
                    return CompletedTask.Instance;
                };

                await c1.UnsubscribeAsync("$share/mysharegroup/a");
                await Task.Delay(250);
                Assert.IsTrue(unsubscribeEventCalled, "Unsubscribe event not called.");

                await c2.PublishAsync(message);
                await Task.Delay(500);
                Assert.AreEqual(1, receivedMessagesCount);

                await Task.Delay(500);

                Assert.AreEqual(1, receivedMessagesCount);
            }
        }

        [TestMethod]
        public async Task Subscribe_Multiple_Share_Names()
        {
            using (var testEnvironment = CreateTestEnvironment(MqttProtocolVersion.V500))
            {
                var receivedMessagesCount = 0;

                await testEnvironment.StartServer();

                var c1 = await testEnvironment.ConnectClient();
                c1.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessagesCount);
                    return CompletedTask.Instance;
                };

                await c1.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("$share/asharegroup/#").Build());

                var c2 = await testEnvironment.ConnectClient();
                c2.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessagesCount);
                    return CompletedTask.Instance;
                };

                await c2.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("$share/bsharegroup/#").Build());

                var c3 = await testEnvironment.ConnectClient();

                await c3.PublishStringAsync("test_topic");
                await Task.Delay(100);
                Assert.AreEqual(receivedMessagesCount, 2);
            }
        }

        [TestMethod]
        public async Task Round_Robin_Delivery_To_Single_Share_Name()
        {
            using (var testEnvironment = CreateTestEnvironment(MqttProtocolVersion.V500))
            {
                var receivedMessagesCount = 0;
                var receivedMessagesCount1 = 0;
                var receivedMessagesCount2 = 0;
                var receivedMessagesCount3 = 0;
                var receivedMessagesCount4 = 0;

                await testEnvironment.StartServer();

                var c1 = await testEnvironment.ConnectClient();
                c1.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessagesCount);
                    Interlocked.Increment(ref receivedMessagesCount1);
                    return CompletedTask.Instance;
                };

                await c1.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("$share/asharegroup/#").Build());

                var c2 = await testEnvironment.ConnectClient();
                c2.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessagesCount);
                    Interlocked.Increment(ref receivedMessagesCount2);
                    return CompletedTask.Instance;
                };

                await c2.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("$share/asharegroup/#").Build());

                var c3 = await testEnvironment.ConnectClient();
                c3.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessagesCount);
                    Interlocked.Increment(ref receivedMessagesCount3);
                    return CompletedTask.Instance;
                };

                await c3.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("$share/asharegroup/#").Build());

                var c4 = await testEnvironment.ConnectClient();
                c4.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessagesCount);
                    Interlocked.Increment(ref receivedMessagesCount4);
                    return CompletedTask.Instance;
                };

                await c4.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("$share/asharegroup/#").Build());

                var c5 = await testEnvironment.ConnectClient();

                await c5.PublishStringAsync("a");
                await c5.PublishStringAsync("a");
                await c5.PublishStringAsync("a");
                await c5.PublishStringAsync("a");
                await c5.PublishStringAsync("a");
                await c5.PublishStringAsync("a");
                await c5.PublishStringAsync("a");
                await c5.PublishStringAsync("a");
                await Task.Delay(100);
                Assert.AreEqual(8, receivedMessagesCount);
                Assert.AreEqual(2, receivedMessagesCount1);
                Assert.AreEqual(2, receivedMessagesCount2);
                Assert.AreEqual(2, receivedMessagesCount3);
                Assert.AreEqual(2, receivedMessagesCount4);
            }
        }

        [TestMethod]
        public async Task Round_Robin_Delivery_To_Multiple_Share_Names()
        {
            using (var testEnvironment = CreateTestEnvironment(MqttProtocolVersion.V500))
            {
                var receivedMessagesCount = 0;
                var receivedMessagesCountShareA_1 = 0;
                var receivedMessagesCountShareA_2 = 0;
                var receivedMessagesCountShareA_3 = 0;
                var receivedMessagesCountShareA_4 = 0;
                var receivedMessagesCountShareB_1 = 0;
                var receivedMessagesCountShareB_2 = 0;
                var receivedMessagesCountShareB_3 = 0;
                var receivedMessagesCountShareB_4 = 0;

                await testEnvironment.StartServer();

                var c1 = await testEnvironment.ConnectClient();
                c1.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessagesCount);
                    Interlocked.Increment(ref receivedMessagesCountShareA_1);
                    return CompletedTask.Instance;
                };

                await c1.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("$share/asharegroup/#").Build());

                var c2 = await testEnvironment.ConnectClient();
                c2.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessagesCount);
                    Interlocked.Increment(ref receivedMessagesCountShareA_2);
                    return CompletedTask.Instance;
                };

                await c2.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("$share/asharegroup/#").Build());

                var c3 = await testEnvironment.ConnectClient();
                c3.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessagesCount);
                    Interlocked.Increment(ref receivedMessagesCountShareA_3);
                    return CompletedTask.Instance;
                };

                await c3.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("$share/asharegroup/#").Build());

                var c4 = await testEnvironment.ConnectClient();
                c4.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessagesCount);
                    Interlocked.Increment(ref receivedMessagesCountShareA_4);
                    return CompletedTask.Instance;
                };

                await c4.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("$share/asharegroup/#").Build());

                var c5 = await testEnvironment.ConnectClient();
                c5.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessagesCount);
                    Interlocked.Increment(ref receivedMessagesCountShareB_1);
                    return CompletedTask.Instance;
                };

                await c5.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("$share/bsharegroup/#").Build());

                var c6 = await testEnvironment.ConnectClient();
                c6.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessagesCount);
                    Interlocked.Increment(ref receivedMessagesCountShareB_2);
                    return CompletedTask.Instance;
                };

                await c6.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("$share/bsharegroup/#").Build());

                var c7 = await testEnvironment.ConnectClient();
                c7.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessagesCount);
                    Interlocked.Increment(ref receivedMessagesCountShareB_3);
                    return CompletedTask.Instance;
                };

                await c7.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("$share/bsharegroup/#").Build());

                var c8 = await testEnvironment.ConnectClient();
                c8.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessagesCount);
                    Interlocked.Increment(ref receivedMessagesCountShareB_4);
                    return CompletedTask.Instance;
                };

                await c8.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("$share/bsharegroup/#").Build());

                var c9 = await testEnvironment.ConnectClient();

                for (var i = 0; i < 8; i++)
                {
                    await c9.PublishStringAsync("a");
                }

                await Task.Delay(100);
                Assert.AreEqual(16, receivedMessagesCount);
                Assert.AreEqual(2, receivedMessagesCountShareA_1);
                Assert.AreEqual(2, receivedMessagesCountShareA_2);
                Assert.AreEqual(2, receivedMessagesCountShareA_3);
                Assert.AreEqual(2, receivedMessagesCountShareA_4);
                Assert.AreEqual(2, receivedMessagesCountShareB_1);
                Assert.AreEqual(2, receivedMessagesCountShareB_2);
                Assert.AreEqual(2, receivedMessagesCountShareB_3);
                Assert.AreEqual(2, receivedMessagesCountShareB_4);
            }
        }

        [TestMethod]
        public async Task Intercept_Publish_To_Shared_Subscription()
        {
            using (var testEnvironment = CreateTestEnvironment(MqttProtocolVersion.V500))
            {
                var server = await testEnvironment.StartServer();
                var interceptedMessageCount = 0;

                server.InterceptingPublishToSharedSubscriptionAsync += e =>
                {
                    Interlocked.Increment(ref interceptedMessageCount);

                    return CompletedTask.Instance;
                };


                var client = await testEnvironment.ConnectClient();
                client.ApplicationMessageReceivedAsync += e =>
                {
                    return CompletedTask.Instance;
                };

                await client.SubscribeAsync("$share/asharegroup/#");

                await client.PublishStringAsync("a");

                await Task.Delay(500);

                Assert.AreEqual(1, interceptedMessageCount);
            }
        }

        [DataTestMethod]
        [DataRow("allow_share", "#", "allow_topic", true)]
        [DataRow("allow_share", "allow_topic", "allow_topic", true)]
        [DataRow("allow_share", "#", "deny_topic", false)]
        [DataRow("allow_share", "deny_topic", "deny_topic", false)]
        [DataRow("deny_share", "#", "allow_topic", false)]
        [DataRow("deny_share", "allow_topic", "allow_topic", false)]
        [DataRow("deny_share", "#", "deny_topic", false)]
        [DataRow("deny_share", "deny_topic", "deny_topic", false)]
        public async Task Allow_Deny_Publish_To_Shared_Subscription(string shareName, string subscribeTopic, string publishTopic, bool allowed)
        {
            using (var testEnvironment = CreateTestEnvironment(MqttProtocolVersion.V500))
            {
                var server = await testEnvironment.StartServer();
                var receivedMessageCount = 0;

                server.InterceptingPublishToSharedSubscriptionAsync += e =>
                {
                    if (e.ApplicationMessage.Topic == "deny_topic" || e.ShareName == "deny_share")
                        e.ProcessPublish = false;

                    return CompletedTask.Instance;
                };


                var client = await testEnvironment.ConnectClient();
                client.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessageCount);
                    
                    return CompletedTask.Instance;
                };

                await client.SubscribeAsync($"$share/{shareName}/{subscribeTopic}");

                await client.PublishStringAsync(publishTopic);

                await Task.Delay(500);

                if (allowed)
                    Assert.AreEqual(1, receivedMessageCount);
                else
                    Assert.AreEqual(0, receivedMessageCount);
            }
        }

        [DataTestMethod]
        [DataRow("mysharegroup", "#", "mysharegroup", "publish_topic", true)]
        [DataRow("mysharegroup", "publish_topic", "mysharegroup", "publish_topic", true)]
        [DataRow("mysharegroup", "receive_topic", "mysharegroup", "publish_topic", false)]
        [DataRow("mysharegroup", "#", "othersharegroup", "publish_topic", false)]
        [DataRow("mysharegroup", "publish_topic", "othersharegroup", "publish_topic", false)]
        [DataRow("mysharegroup", "receive_topic", "othersharegroup", "publish_topic", false)]
        public async Task Publish_Directly_To_Shared_Subscription(string subscribeShareName, string subscribeTopic, string publishShareName, string publishTopic, bool allowed)
        {
            using (var testEnvironment = CreateTestEnvironment(MqttProtocolVersion.V500))
            {
                var server = await testEnvironment.StartServer();
                var receivedMessageCount = 0;

                var client = await testEnvironment.ConnectClient();
                client.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessageCount);

                    return CompletedTask.Instance;
                };

                await client.SubscribeAsync($"$share/{subscribeShareName}/{subscribeTopic}");

                await client.PublishStringAsync($"$share/{publishShareName}/{publishTopic}");

                await Task.Delay(500);

                if (allowed)
                    Assert.AreEqual(1, receivedMessageCount);
                else
                    Assert.AreEqual(0, receivedMessageCount);
            }
        }

        [DataTestMethod]
        [DataRow("mysharegroup", "#", "mysharegroup", "publish_topic", true)]
        [DataRow("mysharegroup", "publish_topic", "mysharegroup", "publish_topic", true)]
        [DataRow("mysharegroup", "receive_topic", "mysharegroup", "publish_topic", false)]
        [DataRow("mysharegroup", "#", "othersharegroup", "publish_topic", false)]
        [DataRow("mysharegroup", "publish_topic", "othersharegroup", "publish_topic", false)]
        [DataRow("mysharegroup", "receive_topic", "othersharegroup", "publish_topic", false)]
        [DataRow("mysharegroup", "#", "mysharegroup", "deny_topic", false)]
        [DataRow("mysharegroup", "publish_topic", "mysharegroup", "deny_topic", false)]
        [DataRow("mysharegroup", "receive_topic", "mysharegroup", "deny_topic", false)]
        [DataRow("mysharegroup", "#", "othersharegroup", "deny_topic", false)]
        [DataRow("mysharegroup", "publish_topic", "othersharegroup", "deny_topic", false)]
        [DataRow("mysharegroup", "receive_topic", "othersharegroup", "deny_topic", false)]
        [DataRow("mysharegroup", "#", "deny_share", "publish_topic", false)]
        [DataRow("mysharegroup", "publish_topic", "deny_share", "publish_topic", false)]
        [DataRow("mysharegroup", "receive_topic", "deny_share", "publish_topic", false)]
        [DataRow("mysharegroup", "#", "deny_share", "publish_topic", false)]
        [DataRow("mysharegroup", "publish_topic", "deny_share", "publish_topic", false)]
        [DataRow("mysharegroup", "receive_topic", "deny_share", "publish_topic", false)]
        public async Task Intercept_Publish_Directly_To_Shared_Subscription(string subscribeShareName, string subscribeTopic, string publishShareName, string publishTopic, bool allowed)
        {
            using (var testEnvironment = CreateTestEnvironment(MqttProtocolVersion.V500))
            {
                var server = await testEnvironment.StartServer();
                var receivedMessageCount = 0;

                server.InterceptingPublishToSharedSubscriptionAsync += e =>
                {
                    if (e.ApplicationMessage.Topic == "deny_topic" || e.ShareName == "deny_share")
                        e.ProcessPublish = false;

                    return CompletedTask.Instance;
                };

                var client = await testEnvironment.ConnectClient();
                client.ApplicationMessageReceivedAsync += e =>
                {
                    Interlocked.Increment(ref receivedMessageCount);

                    return CompletedTask.Instance;
                };

                await client.SubscribeAsync($"$share/{subscribeShareName}/{subscribeTopic}");

                await client.PublishStringAsync($"$share/{publishShareName}/{publishTopic}");

                await Task.Delay(500);

                if (allowed)
                    Assert.AreEqual(1, receivedMessageCount);
                else
                    Assert.AreEqual(0, receivedMessageCount);
            }
        }

    }
}