using System.Security.Cryptography.X509Certificates;
using MQTTnet.Certificates;

namespace MQTTnet.CompatibilityFixtures;

// Compile against the original exact abfa client DLL, then consume with the
// candidate DLL. This fixture must not be rebuilt against candidate references.
public sealed class LegacyCertificateProvider(X509Certificate2 certificate) : ICertificateProvider
{
    public X509Certificate2 GetCertificate() => certificate;
}
