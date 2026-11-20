using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OpenRTS.Network;

internal static class MatchCertificate
{
    internal static X509Certificate2 Create()
    {
        using var key = ECDsa.Create();
        var now = DateTimeOffset.UtcNow;
        var certificate = new CertificateRequest("CN=openrts.local", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(now.AddMinutes(-5), now.AddDays(1));
        if (!OperatingSystem.IsWindows()) return certificate;

        // Schannel needs a named private key. The temporary user key is owned by the imported certificate.
        using (certificate)
        {
            byte[] pfx = certificate.Export(X509ContentType.Pkcs12);
            try
            {
                return X509CertificateLoader.LoadPkcs12(pfx, (string?)null, X509KeyStorageFlags.UserKeySet);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }
    }
}
