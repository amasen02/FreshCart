using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace FreshCart.ServiceDefaults;

/// <summary>
/// Development-only TLS validation for internal HTTPS hops (gateway-to-service and service-to-service).
/// Services present the ASP.NET Core HTTPS development certificate, whose self-signed root is absent
/// from the machine trust store on a fresh checkout (and <c>dotnet dev-certs --trust</c> needs an
/// interactive prompt). This accepts that one certificate — identified by the ASP.NET Core
/// development-certificate OID — and only when the sole chain problem is the untrusted root; every
/// other certificate or error is rejected. Staging and Production keep full chain validation against
/// real certificates.
/// </summary>
public static class DevelopmentCertificateValidation
{
    private const string AspNetCoreHttpsDevelopmentCertificateOid = "1.3.6.1.4.1.311.84.1.1";

    public static bool AcceptAspNetCoreDevelopmentCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
        {
            return true;
        }

        if (sslPolicyErrors != SslPolicyErrors.RemoteCertificateChainErrors
            || certificate is not X509Certificate2 inspectedCertificate)
        {
            return false;
        }

        var onlyUntrustedRoot = chain is null || chain.ChainStatus.All(status =>
            status.Status is X509ChainStatusFlags.NoError or X509ChainStatusFlags.UntrustedRoot);

        return onlyUntrustedRoot && inspectedCertificate.Extensions.Any(extension =>
            string.Equals(extension.Oid?.Value, AspNetCoreHttpsDevelopmentCertificateOid, StringComparison.Ordinal));
    }
}
