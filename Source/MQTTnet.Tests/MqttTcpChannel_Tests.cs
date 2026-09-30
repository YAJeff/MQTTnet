// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using MQTTnet.Implementations;

namespace MQTTnet.Tests;

// ReSharper disable InconsistentNaming
[TestClass]
public class MqttTcpChannel_Tests
{
    [TestMethod]
    public void Certificate_Validation_Uses_Default_Handler_When_No_Custom_Handler()
    {
        var options = new MqttClientOptionsBuilder().WithTcpServer("localhost")
            .WithTlsOptions(o => o.WithAllowUntrustedCertificates().WithIgnoreCertificateRevocationErrors())
            .Build();

        var tcpChannel = new MqttTcpChannel(options);

        using var chain = new X509Chain();
        var isValid = InvokeCertificateValidationCallback(tcpChannel, chain, SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.IsTrue(isValid);
    }

    [TestMethod]
    public async Task Dispose_Channel_While_Used()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var serverSocket = new CrossPlatformSocket(AddressFamily.InterNetwork, ProtocolType.Tcp);
        serverSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var serverPort = ((IPEndPoint)serverSocket.LocalEndPoint).Port;
        serverSocket.Listen(1);

        var accepted = serverSocket.AcceptAsync(timeout.Token);
        var remoteEndPoint = new DnsEndPoint("localhost", serverPort);
        using var clientSocket = new CrossPlatformSocket(AddressFamily.InterNetwork, ProtocolType.Tcp);
        await clientSocket.ConnectAsync(remoteEndPoint, timeout.Token);
        // Keep the peer owned and alive until the pending read has completed.
        using var peer = await accepted;
        using var tcpChannel = new MqttTcpChannel(clientSocket.GetStream(), new DnsEndPoint("localhost", 50000), remoteEndPoint, null);
        await peer.SendAsync(new ArraySegment<byte>(new byte[] { 128 }), SocketFlags.None);
        var buffer = new byte[1];
        Assert.AreEqual(1, await tcpChannel.ReadAsync(buffer, 0, 1, timeout.Token));
        Assert.AreEqual(128, buffer[0]);

        // Start the read before disposal, without racing a detached task or delay.
        var read = tcpChannel.ReadAsync(buffer, 0, 1, CancellationToken.None);
        Assert.IsFalse(read.IsCompleted);
        tcpChannel.Dispose();
        var exception = await Assert.ThrowsExactlyAsync<SocketException>(async () => await read.WaitAsync(timeout.Token));
        Assert.AreEqual(SocketError.OperationAborted, exception.SocketErrorCode);
    }
    static bool InvokeCertificateValidationCallback(MqttTcpChannel tcpChannel, X509Chain chain, SslPolicyErrors sslPolicyErrors)
    {
        var method = typeof(MqttTcpChannel).GetMethod("InternalUserCertificateValidationCallback", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        return (bool)method.Invoke(tcpChannel, [null, null, chain, sslPolicyErrors]);
    }
}
