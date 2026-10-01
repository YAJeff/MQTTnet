using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MQTTnet.Certificates;
using MQTTnet.Diagnostics.Logger;
using MQTTnet.Packets;
using MQTTnet.Server;
using MQTTnet.Server.Internal.Adapter;

namespace MQTTnet.Tests.Server;

[TestClass]
public sealed class CertificateContextLease_Tests
{
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [TestMethod]
    public void Windows_Synthetic_Context_Is_Rejected_Before_Create()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows-only conservative creation guard.");
        Assert.Throws<PlatformNotSupportedException>(() => new Material());
    }

    [TestMethod]
    public async Task Legacy_Binary_Provider_Still_Serves_Tls()
    {
        using var material = new Material();
        var path = Environment.GetEnvironmentVariable("MQTTNET_LEGACY_CERTIFICATE_PROVIDER_DLL");
        Assert.IsFalse(string.IsNullOrWhiteSpace(path), "Hosted qualification must compile the provider against the exact original abfa DLL, then supply its path.");
        var assembly = Assembly.LoadFrom(path);
        var provider = (ICertificateProvider)Activator.CreateInstance(assembly.GetType("MQTTnet.CompatibilityFixtures.LegacyCertificateProvider", true), material.Leaf);
        await using var fixture = new Listener(provider);
        using var peer = await Peer.Connect(fixture.Port, material);
        await peer.Ping();
        Assert.IsFalse(provider is ICertificateContextProvider);
    }

    [TestMethod]
    [DataRow(SslProtocols.Tls12)]
    [DataRow(SslProtocols.Tls13)]
    public async Task Explicit_Context_Serves_Supplied_Intermediate(SslProtocols protocol)
    {
        using var provider = new Provider();
        await using var fixture = new Listener(provider, protocol);
        using var peer = await Peer.Connect(fixture.Port, provider.Current, protocol: protocol);
        await peer.Ping();
        Assert.AreEqual(provider.Current.Leaf.Thumbprint, peer.LeafThumbprint);
        CollectionAssert.Contains(peer.ChainThumbprints, provider.Current.Intermediate.Thumbprint);
        Assert.AreEqual(protocol, peer.Stream.SslProtocol);
        Assert.AreEqual(0, provider.LegacyCalls);
        Assert.AreEqual(1, provider.Active);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Wrong_Trust_Or_Hostname_Rejects(bool wrongHostname)
    {
        using var provider = new Provider();
        using var wrongRoot = new Material();
        await using var fixture = new Listener(provider);
        await Assert.ThrowsExactlyAsync<AuthenticationException>(() => Peer.Connect(fixture.Port,
            wrongHostname ? provider.Current : wrongRoot, host: wrongHostname ? "wrong.example" : "localhost"));
        await provider.Released.Task.WaitAsync(Bound);
        Assert.AreEqual(0, provider.Active);
        Assert.AreEqual(0, fixture.Pings);
    }

    [TestMethod]
    public async Task Configured_Protocol_Is_Not_Overridden_By_Context()
    {
        using var provider = new Provider();
        await using var fixture = new Listener(provider, SslProtocols.Tls12);
        await Assert.ThrowsExactlyAsync<AuthenticationException>(() => Peer.Connect(fixture.Port, provider.Current, protocol: SslProtocols.Tls13));
        await provider.Released.Task.WaitAsync(Bound);
        Assert.AreEqual(0, fixture.Pings);
    }

    [TestMethod]
    public async Task Required_Client_Certificate_Is_Not_Bypassed()
    {
        using var provider = new Provider();
        await using var fixture = new Listener(provider, requireClientCertificate: true);
        try
        {
            using var peer = await Peer.Connect(fixture.Port, provider.Current);
            try { await peer.Ping(); Assert.Fail("mTLS must reject a peer without a client certificate."); }
            catch (IOException) { }
            catch (AuthenticationException) { }
        }
        catch (AuthenticationException) { }
        await provider.Released.Task.WaitAsync(Bound);
        Assert.AreEqual(0, fixture.Pings);
    }

    [TestMethod]
    public async Task Rotation_Preserves_Handshake_And_Existing_Connection_Snapshot()
    {
        using var provider = new Provider();
        await using var fixture = new Listener(provider);
        var firstMaterial = provider.Current;
        provider.AcquireGate.Reset();
        var connect = Peer.Connect(fixture.Port, firstMaterial);
        await provider.Acquired.Task.WaitAsync(Bound);
        var replacement = new Material();
        provider.Rotate(replacement);
        Assert.AreEqual(0, firstMaterial.Disposals, "Retirement must not dispose an acquired handshake snapshot.");
        provider.AcquireGate.Set();
        using var first = await connect;
        using var second = await Peer.Connect(fixture.Port, replacement);
        await first.Ping();
        await second.Ping();
        Assert.AreEqual(firstMaterial.Leaf.Thumbprint, first.LeafThumbprint);
        Assert.AreEqual(replacement.Leaf.Thumbprint, second.LeafThumbprint);
        Assert.AreEqual(0, firstMaterial.Disposals);
        Assert.AreEqual(2, provider.Active);
        first.Dispose();
        await provider.Released.Task.WaitAsync(Bound);
        Assert.AreEqual(1, firstMaterial.Disposals);
    }

    [TestMethod]
    public async Task Failed_Handshake_Releases_Material_After_Transport_Close()
    {
        using var provider = new Provider();
        await using var fixture = new Listener(provider);
        using var wrong = new Material();
        await Assert.ThrowsExactlyAsync<AuthenticationException>(() => Peer.Connect(fixture.Port, wrong));
        await provider.Released.Task.WaitAsync(Bound);
        Assert.AreEqual(1, provider.ReleaseCalls);
        Assert.AreEqual(0, provider.Active);
        Assert.AreEqual(0, provider.LegacyCalls);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task Invalid_Capability_Does_Not_Fall_Back_To_Leaf_Or_Plaintext(int mode)
    {
        using var provider = new Provider { InvalidMode = mode };
        await using var fixture = new Listener(provider);
        try
        {
            using var peer = await Peer.Connect(fixture.Port, provider.Current);
            Assert.Fail("Invalid context capability must fail closed.");
        }
        catch (AuthenticationException) { }
        catch (IOException) { }
        Assert.AreEqual(0, provider.LegacyCalls);
        Assert.AreEqual(0, fixture.Pings);
        if (mode == 2) await provider.Released.Task.WaitAsync(Bound);
        Assert.AreEqual(0, provider.Active);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Missing_Or_Faulted_Handler_Releases_Exactly_One_Lease(bool faulted)
    {
        using var provider = new Provider();
        await using var fixture = new Listener(provider, handlerMode: faulted ? 1 : 2);
        using var peer = await Peer.Connect(fixture.Port, provider.Current);
        await provider.Released.Task.WaitAsync(Bound);
        Assert.AreEqual(1, provider.ReleaseCalls);
        Assert.AreEqual(0, provider.Active);
    }

    [TestMethod]
    public async Task Legacy_Null_Certificate_Keeps_Plaintext_Behavior()
    {
        await using var fixture = new Listener(new NullProvider());
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, fixture.Port).WaitAsync(Bound);
        await Peer.Ping(tcp.GetStream());
        Assert.AreEqual(1, fixture.Pings);
    }

    [TestMethod]
    public async Task Owned_Handler_Shutdown_Releases_After_Stream_Teardown()
    {
        using var provider = new Provider();
        await using var fixture = new Listener(provider);
        using var peer = await Peer.Connect(fixture.Port, provider.Current);
        await peer.Ping();
        provider.Current.Retire();
        Assert.AreEqual(0, provider.Current.Disposals);
        fixture.ShutdownHandlers();
        await provider.Released.Task.WaitAsync(Bound);
        Assert.AreEqual(0, provider.Active);
        Assert.AreEqual(1, provider.Current.Disposals);
        Assert.AreEqual(1, provider.ReleaseCalls);
    }

    [TestMethod]
    public async Task Unknown_Lease_Release_Remains_Bounded_And_Closes_Admission()
    {
        using var provider = new Provider { FailRelease = true, Capacity = 1 };
        await using var fixture = new Listener(provider);
        using (var first = await Peer.Connect(fixture.Port, provider.Current)) { await first.Ping(); }
        await provider.ReleaseFault.Task.WaitAsync(Bound);
        Assert.AreEqual(1, provider.Active, "A failed release is not settled ownership.");
        try { using var second = await Peer.Connect(fixture.Port, provider.Current); Assert.Fail("Unresolved lease capacity must block another acquire."); }
        catch (AuthenticationException) { }
        catch (IOException) { }
        Assert.AreEqual(1, provider.AcquireCalls);
        Assert.AreEqual(1, fixture.Pings);
        Assert.AreEqual(0, provider.Current.Disposals);
        provider.ResolveForFixtureCleanup();
    }

    sealed class NullProvider : ICertificateProvider
    {
        public X509Certificate2 GetCertificate() => null;
    }

    sealed class Material : IDisposable
    {
        readonly object _gate = new();
        int _leases;
        bool _retired;
        public int Disposals { get; private set; }
        public X509Certificate2 Root { get; }
        public X509Certificate2 Intermediate { get; }
        public X509Certificate2 Leaf { get; }
        public SslStreamCertificateContext Context { get; }
        public Material()
        {
            // Windows Create can import intermediates into CA stores even offline.
            // Reject before generating certificates or calling context Create.
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Synthetic chain context creation is disabled on Windows.");
            var now = DateTimeOffset.UtcNow;
            using var rootKey = RSA.Create(2048);
            var rootRequest = Request("CN=OwnedRoot", rootKey, true);
            Root = rootRequest.CreateSelfSigned(now.AddMinutes(-1), now.AddHours(1));
            using var issuerKey = RSA.Create(2048);
            var issuerRequest = Request("CN=OwnedIntermediate", issuerKey, true);
            using var issuer = issuerRequest.Create(Root, now.AddMinutes(-1), now.AddMinutes(45), RandomNumberGenerator.GetBytes(16));
            Intermediate = issuer.CopyWithPrivateKey(issuerKey);
            using var leafKey = RSA.Create(2048);
            var leafRequest = Request("CN=localhost", leafKey, false);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); leafRequest.CertificateExtensions.Add(san.Build());
            leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            using var leaf = leafRequest.Create(Intermediate, now.AddMinutes(-1), now.AddMinutes(30), RandomNumberGenerator.GetBytes(16));
            Leaf = leaf.CopyWithPrivateKey(leafKey);
            Context = SslStreamCertificateContext.Create(Leaf, new X509Certificate2Collection(new[] { Intermediate, Root }), offline: true);
        }
        static CertificateRequest Request(string name, RSA key, bool ca)
        {
            var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(ca ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign : X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            return request;
        }
        public void Acquire() { lock (_gate) { if (_retired) throw new InvalidOperationException("Retired snapshot"); _leases++; } }
        public void Release() { lock (_gate) { _leases--; if (_retired && _leases == 0) DisposeMaterial(); } }
        public void Retire() { lock (_gate) { _retired = true; if (_leases == 0) DisposeMaterial(); } }
        void DisposeMaterial()
        {
            if (Disposals != 0) return;
            Disposals++;
            foreach (var certificate in Context.IntermediateCertificates) certificate.Dispose();
            Leaf.Dispose(); Intermediate.Dispose(); Root.Dispose();
        }
        public void Dispose() => Retire();
    }

    sealed class Provider : ICertificateContextProvider, IDisposable
    {
        readonly object _gate = new();
        readonly List<Lease> _leases = new();
        public Material Current { get; private set; } = new();
        public int Active { get { lock (_gate) return _leases.Count; } }
        public int AcquireCalls { get; private set; }
        public int LegacyCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public int Capacity { get; set; } = 8;
        public int InvalidMode { get; set; }
        public bool FailRelease { get; set; }
        public ManualResetEventSlim AcquireGate { get; } = new(true);
        public TaskCompletionSource Acquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFault { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource _allReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task AllReleased { get { lock (_gate) return _allReleased.Task; } }
        public X509Certificate2 GetCertificate() { LegacyCalls++; throw new InvalidOperationException("Optional context must not call the legacy path."); }
        public ICertificateContextLease AcquireCertificateContext()
        {
            Lease lease;
            lock (_gate)
            {
                if (InvalidMode == 1) return null;
                if (InvalidMode == 3) throw new InvalidOperationException("Expected provider acquisition fault");
                if (_leases.Count >= Capacity) throw new InvalidOperationException("Certificate lease capacity exhausted");
                if (_leases.Count == 0) _allReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Current.Acquire(); lease = new Lease(this, Current, InvalidMode == 2); _leases.Add(lease); AcquireCalls++;
            }
            Acquired.TrySetResult();
            if (!AcquireGate.Wait(Bound)) throw new TimeoutException("Fixture acquisition barrier was not released");
            return lease;
        }
        public void Rotate(Material replacement) { lock (_gate) { var old = Current; Current = replacement; old.Retire(); } }
        void Release(Lease lease)
        {
            lock (_gate)
            {
                if (FailRelease) { ReleaseFault.TrySetResult(); throw new IOException("Expected unresolved lease release fault"); }
                if (!_leases.Remove(lease)) return;
                lease.Material.Release(); ReleaseCalls++;
                if (_leases.Count == 0) _allReleased.TrySetResult();
            }
            Released.TrySetResult();
        }
        public void ResolveForFixtureCleanup() { lock (_gate) { FailRelease = false; foreach (var lease in _leases.ToArray()) Release(lease); } }
        public void Dispose() { AcquireGate.Set(); ResolveForFixtureCleanup(); Current.Retire(); AcquireGate.Dispose(); }
        sealed class Lease(Provider owner, Material material, bool nullContext) : ICertificateContextLease
        {
            public Material Material { get; } = material;
            public SslStreamCertificateContext CertificateContext => nullContext ? null : Material.Context;
            public void Dispose() => owner.Release(this);
        }
    }

    sealed class Listener : IAsyncDisposable
    {
        readonly CancellationTokenSource _stop = new();
        readonly CancellationTokenSource _handlers = new();
        readonly MqttTcpServerListener _listener;
        readonly Provider _provider;
        readonly List<Task> _handlerCompletions = new();
        public int Pings;
        public int Port { get; }
        public Listener(ICertificateProvider provider, SslProtocols protocol = SslProtocols.Tls12, bool requireClientCertificate = false, int handlerMode = 0)
        {
            _provider = provider as Provider;
            var options = new MqttServerTlsTcpEndpointOptions { Port = 0, BoundInterNetworkAddress = IPAddress.Loopback, CertificateProvider = provider, SslProtocol = protocol, ClientCertificateRequired = requireClientCertificate, CheckCertificateRevocation = false, LingerState = new LingerOption(false, 0) };
            _listener = new MqttTcpServerListener(AddressFamily.InterNetwork, new MqttServerOptionsBuilder().Build(), options, MqttNetNullLogger.Instance);
            if (handlerMode != 2) _listener.ClientHandler = async adapter =>
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_handlerCompletions) _handlerCompletions.Add(completion.Task);
                try
                {
                if (handlerMode == 1) throw new IOException("Expected client handler fault");
                adapter.PacketFormatterAdapter.DetectProtocolVersion(new MQTTnet.Adapter.ReceivedMqttPacket(0x10, new ArraySegment<byte>(new byte[] { 0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 4 }), 9));
                try
                {
                    while (!_handlers.IsCancellationRequested)
                    {
                        var packet = await adapter.ReceivePacketAsync(_handlers.Token);
                        if (packet == null) break;
                        Assert.IsInstanceOfType<MqttPingReqPacket>(packet);
                        Interlocked.Increment(ref Pings);
                        await adapter.SendPacketAsync(new MqttPingRespPacket(), _handlers.Token);
                    }
                }
                catch (OperationCanceledException) { }
                }
                finally { completion.TrySetResult(); }
            };
            Assert.IsTrue(_listener.Start(false, _stop.Token)); Port = options.Port;
        }
        public void ShutdownHandlers() => _handlers.Cancel();
        public async ValueTask DisposeAsync()
        {
            _handlers.Cancel(); _stop.Cancel(); _listener.Dispose();
            Task[] completions;
            lock (_handlerCompletions) completions = _handlerCompletions.ToArray();
            await Task.WhenAll(completions).WaitAsync(Bound);
            if (_provider != null && _provider.Active != 0 && !_provider.FailRelease)
                await _provider.AllReleased.WaitAsync(Bound);
            _handlers.Dispose(); _stop.Dispose();
        }
    }

    sealed class Peer : IDisposable
    {
        readonly TcpClient _tcp;
        public SslStream Stream { get; }
        public string LeafThumbprint { get; private set; }
        public string[] ChainThumbprints { get; private set; } = [];
        Peer(TcpClient tcp, SslStream stream) { _tcp = tcp; Stream = stream; }
        public static async Task<Peer> Connect(int port, Material trust, string host = "localhost", SslProtocols protocol = SslProtocols.Tls12)
        {
            using var timeout = new CancellationTokenSource(Bound);
            using var root = X509Certificate2.CreateFromPem(trust.Root.ExportCertificatePem());
            var tcp = new TcpClient();
            SslStream ssl = null;
            try
            {
                await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                ssl = new SslStream(tcp.GetStream());
                var peer = new Peer(tcp, ssl);
                var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck, DisableCertificateDownloads = true };
                policy.CustomTrustStore.Add(root);
                var options = new SslClientAuthenticationOptions { TargetHost = host, EnabledSslProtocols = protocol, CertificateChainPolicy = policy, RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                {
                    peer.LeafThumbprint = ((X509Certificate2)certificate).Thumbprint;
                    peer.ChainThumbprints = chain.ChainElements.Cast<X509ChainElement>().Select(e => e.Certificate.Thumbprint).ToArray();
                    return errors == SslPolicyErrors.None;
                }};
                await ssl.AuthenticateAsClientAsync(options, timeout.Token);
                return peer;
            }
            catch { ssl?.Dispose(); tcp.Dispose(); throw; }
        }
        public Task Ping() => Ping(Stream);
        public static async Task Ping(Stream stream)
        {
            using var timeout = new CancellationTokenSource(Bound);
            await stream.WriteAsync(new byte[] { 0xC0, 0 }, timeout.Token);
            var reply = new byte[2]; await stream.ReadExactlyAsync(reply, timeout.Token);
            CollectionAssert.AreEqual(new byte[] { 0xD0, 0 }, reply);
        }
        public void Dispose() { Stream.Dispose(); _tcp.Dispose(); }
    }
}
