// SOURCE-ONLY test derivation proposal. No runtime invocation authorized.
// A separate Root-reviewed controller must prove the owned private network
// and silent SYN-drop prerequisite before setting this address.
using System.Net;
using System.Net.Sockets;

namespace MQTTnet.Tests.Clients.MqttClient;

static class OwnedCancellationFixture
{
    internal static string Address
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("MQTTNET_OWNED_CANCELLATION_IPV4");
            if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                throw new InvalidOperationException("Exact owned cancellation IPv4 fixture is required");
            var bytes = address.GetAddressBytes();
            var privateAddress = bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168);
            if (!privateAddress || address.ToString() != value)
                throw new InvalidOperationException("Cancellation fixture must use a canonical owned RFC1918 IPv4 address");
            // Private addressing alone is not ownership/enforcement proof.
            // The independent controller receipt is a required admission gate.
            return value;
        }
    }
}
