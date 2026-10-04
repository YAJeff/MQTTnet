using System.Security.Cryptography.X509Certificates;
using MQTTnet.Certificates;

namespace MQTTnet.Qualification;

sealed class NoCreateProvider : ICertificateContextProvider
{
    public int AcquireCalls { get; private set; }
    public X509Certificate2 GetCertificate() => null;
    public ICertificateContextLease AcquireCertificateContext() { AcquireCalls++; throw new InvalidOperationException("No TLS acquisition in compile/API phase"); }
}
