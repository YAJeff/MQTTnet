// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace MQTTnet.Certificates;

// Optional capability. Existing ICertificateProvider implementations are unchanged.
public interface ICertificateContextProvider : ICertificateProvider
{
    ICertificateContextLease AcquireCertificateContext();
}
