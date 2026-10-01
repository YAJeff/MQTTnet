using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using MQTTnet.Certificates;
using MQTTnet.Server;
using MQTTnet.Qualification;

RunChecks(args);

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static void RunChecks(string[] args)
{
if (args.Length != 1) throw new ArgumentException("Exact unchanged old-provider fixture DLL path required");
var providerType = typeof(ICertificateContextProvider);
var leaseType = typeof(ICertificateContextLease);
Check(providerType.IsPublic && providerType.IsInterface && providerType.GetInterfaces().Contains(typeof(ICertificateProvider)) &&
    providerType.GetMethod(nameof(ICertificateContextProvider.AcquireCertificateContext))?.ReturnType == leaseType,
    "OptionalProviderContract");
Check(leaseType.IsPublic && leaseType.IsInterface && leaseType.GetInterfaces().Contains(typeof(IDisposable)) &&
    leaseType.GetProperty(nameof(ICertificateContextLease.CertificateContext))?.PropertyType == typeof(SslStreamCertificateContext) &&
    leaseType.GetProperty(nameof(ICertificateContextLease.CertificateContext))?.SetMethod == null,
    "ImmutableLeaseContract");
var provider = new NoCreateProvider();
var options = new MqttServerOptionsBuilder().WithEncryptionCertificate(provider).Build();
Check(ReferenceEquals(options.TlsEndpointOptions.CertificateProvider, provider) && provider.AcquireCalls == 0,
    "LegacyOptionAcceptsOptionalCapabilityWithoutAcquisition");
var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
var fixture = assembly.GetType("MQTTnet.CompatibilityFixtures.LegacyCertificateProvider", throwOnError: true);
var oldProvider = (ICertificateProvider)fixture.GetConstructor(new[] { typeof(X509Certificate2) }).Invoke(new object[] { null });
Check(oldProvider.GetCertificate() == null && oldProvider is not ICertificateContextProvider,
    "UnchangedOldBinaryProviderBindsToCandidate");
Console.WriteLine(JsonSerializer.Serialize(new { passed = 4, failed = 0, skipped = 0, contextCreated = 0, socketsOpened = 0,
    candidate = typeof(ICertificateProvider).Assembly.Location, fixture = assembly.Location,
    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    runtimeVersion = Environment.Version.ToString(), processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString() }));

}

static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); Console.WriteLine("PASS " + name); }
