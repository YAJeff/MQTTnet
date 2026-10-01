// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using MQTTnet.Server;
using MQTTnet.Tests.Mockups;

namespace MQTTnet.Tests.Server;

// Proposal gap probes: real routes run before assertions. Reflection keeps these
// tests compilable against abfa without inventing a production API implementation.
[TestClass]
public sealed class TrustedIngressContext_Tests
{
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [TestMethod]
    [DataRow(MqttQualityOfServiceLevel.AtMostOnce)]
    [DataRow(MqttQualityOfServiceLevel.AtLeastOnce)]
    [DataRow(MqttQualityOfServiceLevel.ExactlyOnce)]
    public async Task Network_Route_Carries_Exact_Attempt_And_Same_Publication_To_Recipient(MqttQualityOfServiceLevel qos)
    {
        using var environment = new TestEnvironment(null, MqttProtocolVersion.V500);
        var server = await environment.StartServer(o => o.WithPersistentSessions());
        var attempts = new ConcurrentDictionary<string, Guid>();
        server.ClientConnectedAsync += e => { attempts[e.ClientId] = e.ConnectionAttemptId; return Task.CompletedTask; };
        var publishing = new TaskCompletionSource<InterceptingPublishEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueuing = new TaskCompletionSource<InterceptingClientApplicationMessageEnqueueEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.InterceptingPublishAsync += e => { publishing.TrySetResult(e); return Task.CompletedTask; };
        server.InterceptingClientEnqueueAsync += e => { enqueuing.TrySetResult(e); return Task.CompletedTask; };
        var receiver = await environment.ConnectClient(o => o.WithClientId("recipient"));
        await receiver.SubscribeAsync(new MqttTopicFilter { Topic = "context/network", QualityOfServiceLevel = qos });
        var sender = await environment.ConnectClient(o => o.WithClientId("publisher"));
        await sender.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("context/network").WithPayload("canonical").WithQualityOfServiceLevel(qos).Build());
        var ingress = await publishing.Task.WaitAsync(Bound);
        var recipient = await enqueuing.Task.WaitAsync(Bound);
        var context = RequireContext(ingress);
        Assert.AreEqual(attempts["publisher"], RequireProperty(context, "ConnectionAttemptId"));
        Assert.AreSame(context, RequireContext(recipient));
        Assert.AreNotEqual(Guid.Empty, RequireProperty(context, "PublicationId"));
    }

    [TestMethod]
    public async Task Injection_Has_Server_Issued_Origin_Without_Fabricated_Network_Attempt()
    {
        using var environment = new TestEnvironment();
        var server = await environment.StartServer();
        InterceptingPublishEventArgs captured = null;
        server.InterceptingPublishAsync += e => { captured = e; return Task.CompletedTask; };
        await server.InjectApplicationMessage(new InjectedMqttApplicationMessage(new MqttApplicationMessageBuilder().WithTopic("context/injection").Build()) { SenderClientId = "untrusted-sender-label" });
        Assert.IsNotNull(captured);
        var context = RequireContext(captured);
        Assert.IsNull(RequireProperty(context, "ConnectionAttemptId"));
        Assert.AreEqual("Injection", RequireProperty(context, "RouteKind").ToString());
        Assert.AreNotEqual(Guid.Empty, RequireProperty(context, "ServerInstanceId"));
    }

    [TestMethod]
    public async Task Native_Will_Has_Dedicated_Ownership_After_Origin_Disconnect()
    {
        using var environment = new TestEnvironment(null, MqttProtocolVersion.V500);
        var server = await environment.StartServer(o => o.WithPersistentSessions());
        var published = new TaskCompletionSource<InterceptingPublishEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.InterceptingPublishAsync += e => { published.TrySetResult(e); return Task.CompletedTask; };
        var sender = await environment.ConnectClient(o => o.WithClientId("will-origin").WithSessionExpiryInterval(30).WithWillTopic("context/will").WithWillPayload("will").WithWillDelayInterval(0));
        await sender.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().WithReason(MqttClientDisconnectOptionsReason.DisconnectWithWillMessage).Build());
        var context = RequireContext(await published.Task.WaitAsync(Bound));
        Assert.AreEqual("Will", RequireProperty(context, "RouteKind").ToString());
        Assert.AreNotEqual(Guid.Empty, RequireProperty(context, "PublicationId"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Network_And_Server_Subscribe_Replay_Carry_Snapshot_Authority(bool serverSubscribe)
    {
        using var environment = new TestEnvironment(null, MqttProtocolVersion.V500);
        var server = await environment.StartServer();
        var sender = await environment.ConnectClient(o => o.WithClientId("retained-origin"));
        await sender.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("context/retained").WithPayload("old-snapshot").WithRetainFlag().WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());
        var receiver = await environment.ConnectClient(o => o.WithClientId("replay-recipient"));
        var replay = new TaskCompletionSource<InterceptingClientApplicationMessageEnqueueEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.InterceptingClientEnqueueAsync += e => { replay.TrySetResult(e); return Task.CompletedTask; };
        var republished = 0;
        server.InterceptingPublishAsync += _ => { Interlocked.Increment(ref republished); return Task.CompletedTask; };
        var filter = new MqttTopicFilter { Topic = "context/retained" };
        if (serverSubscribe) await server.SubscribeAsync("replay-recipient", new[] { filter });
        else await receiver.SubscribeAsync(filter);
        var captured = await replay.Task.WaitAsync(Bound);
        Assert.AreEqual(0, Volatile.Read(ref republished));
        Assert.AreEqual("old-snapshot", captured.ApplicationMessage.ConvertPayloadToString());
        var context = RequireContext(captured);
        Assert.AreEqual("RetainedReplay", RequireProperty(context, "RouteKind").ToString());
        Assert.IsNotNull(RequireProperty(context, "RetainedRecordIdentity"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Retained_Replacement_Cannot_Rewrite_Already_Captured_Replay(bool serverSubscribe)
    {
        using var environment = new TestEnvironment(null, MqttProtocolVersion.V500);
        var server = await environment.StartServer();
        var sender = await environment.ConnectClient(o => o.WithClientId("snapshot-origin"));
        var oldMessage = new MqttApplicationMessageBuilder().WithTopic("context/snapshot").WithPayload("old").WithRetainFlag().WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build();
        await sender.PublishAsync(oldMessage);
        var receiver = await environment.ConnectClient(o => o.WithClientId("snapshot-recipient"));
        var entered = new TaskCompletionSource<InterceptingClientApplicationMessageEnqueueEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = Signal();
        server.InterceptingClientEnqueueAsync += async e =>
        {
            if (e.ApplicationMessage.ConvertPayloadToString() != "old") return;
            entered.TrySetResult(e);
            await release.Task;
        };
        var filter = new MqttTopicFilter { Topic = "context/snapshot" };
        var subscribe = serverSubscribe ? server.SubscribeAsync("snapshot-recipient", new[] { filter }) : receiver.SubscribeAsync(filter);
        InterceptingClientApplicationMessageEnqueueEventArgs captured;
        try
        {
            captured = await entered.Task.WaitAsync(Bound);
            await sender.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("context/snapshot").WithPayload("new").WithRetainFlag().WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());
            Assert.AreEqual("old", captured.ApplicationMessage.ConvertPayloadToString());
            Assert.AreEqual("new", (await server.GetRetainedMessagesAsync()).Single(m => m.Topic == "context/snapshot").ConvertPayloadToString());
        }
        finally { release.TrySetResult(); }
        await subscribe.WaitAsync(Bound);
        var context = RequireContext(captured);
        Assert.AreEqual("RetainedReplay", RequireProperty(context, "RouteKind").ToString());
        Assert.IsNotNull(RequireProperty(context, "RetainedRecordIdentity"));
    }

    [TestMethod]
    public async Task Takeover_Fences_Late_Callback_Before_Retained_Write_And_Recipient_Enqueue()
    {
        using var environment = new TestEnvironment(null, MqttProtocolVersion.V500) { IgnoreClientLogErrors = true };
        var server = await environment.StartServer(o => o.WithPersistentSessions());
        var entered = Signal();
        var release = Signal();
        InterceptingPublishEventArgs oldIngress = null;
        var recipientCallback = Signal();
        var enqueues = 0;
        server.InterceptingPublishAsync += async e =>
        {
            if (e.ApplicationMessage.Topic != "context/stale") return;
            oldIngress = e;
            entered.TrySetResult();
            // Deliberately ignore cancellation: late completion must not authorize delivery.
            await release.Task;
        };
        server.InterceptingClientEnqueueAsync += e =>
        {
            if (e.ApplicationMessage.Topic == "context/stale") Interlocked.Increment(ref enqueues);
            return Task.CompletedTask;
        };
        var receiver = await environment.ConnectClient(o => o.WithClientId("takeover-recipient"));
        receiver.ApplicationMessageReceivedAsync += _ => { recipientCallback.TrySetResult(); return Task.CompletedTask; };
        await receiver.SubscribeAsync(new MqttTopicFilter { Topic = "context/stale" });
        var old = await environment.ConnectClient(o => o.WithClientId("same-sender").WithCleanSession(false).WithSessionExpiryInterval(30));
        var publish = old.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("context/stale").WithPayload("old-attempt").WithRetainFlag().WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());
        try
        {
            await entered.Task.WaitAsync(Bound);
            await environment.ConnectClient(o => o.WithClientId("same-sender").WithCleanSession(false).WithSessionExpiryInterval(30), Bound);
        }
        finally { release.TrySetResult(); }
        try { await publish.WaitAsync(Bound); } catch (MQTTnet.Exceptions.MqttClientDisconnectedException) { }
        // A disconnected client task cannot prove that its abandoned native route
        // drained. Require the proposed exact-route quiescence proof before counting.
        var context = RequireContext(oldIngress);
        var quiesced = RequireProperty(context, "Quiesced") as Task;
        Assert.IsNotNull(quiesced);
        await quiesced.WaitAsync(Bound);
        Assert.AreEqual(0, Volatile.Read(ref enqueues));
        Assert.IsFalse(recipientCallback.Task.IsCompleted);
        Assert.IsFalse((await server.GetRetainedMessagesAsync()).Any(m => m.Topic == "context/stale"));
    }

    [TestMethod]
    public async Task Disabled_Mode_Legacy_Constructors_And_Injection_Still_Deliver()
    {
        var message = new MqttApplicationMessageBuilder().WithTopic("context/legacy").WithPayload("legacy").Build();
        var oldPublish = new InterceptingPublishEventArgs(message, "origin", null, new System.Collections.Hashtable(), CancellationToken.None);
        var oldEnqueue = new InterceptingClientApplicationMessageEnqueueEventArgs("origin", "recipient", message);
        Assert.IsTrue(oldPublish.ProcessPublish);
        Assert.IsTrue(oldEnqueue.AcceptEnqueue);
        using var environment = new TestEnvironment();
        var server = await environment.StartServer();
        var receiver = await environment.ConnectClient();
        var delivered = Signal();
        receiver.ApplicationMessageReceivedAsync += e => { if (e.ApplicationMessage.ConvertPayloadToString() == "legacy") delivered.TrySetResult(); return Task.CompletedTask; };
        await receiver.SubscribeAsync(new MqttTopicFilter { Topic = "context/legacy" });
        await server.InjectApplicationMessage(new InjectedMqttApplicationMessage(message));
        await delivered.Task.WaitAsync(Bound);
    }

    internal static object RequireContext(object args)
    {
        var property = args.GetType().GetProperty("PublicationContext");
        Assert.IsNotNull(property, $"Actual {args.GetType().Name} route has no native PublicationContext surface.");
        var context = property.GetValue(args);
        Assert.IsNotNull(context, "Actual native route did not issue a publication context.");
        return context;
    }

    static object RequireProperty(object context, string name)
    {
        var property = context.GetType().GetProperty(name);
        Assert.IsNotNull(property, $"Publication context is missing {name}.");
        return property.GetValue(context);
    }

    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
