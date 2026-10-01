// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.Security;

namespace MQTTnet.Certificates;

// One immutable context/material snapshot per connection. Dispose releases the
// provider's lease; material must remain valid until the listener releases it.
public interface ICertificateContextLease : IDisposable
{
    SslStreamCertificateContext CertificateContext { get; }
}
