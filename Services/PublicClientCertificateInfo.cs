using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

internal static class PublicClientCertificateInfo
{
    internal static bool TryRun(string[] args, TextWriter output, string certificatePath = "/pki/client/tls.crt")
    {
        if (args.Length != 1 || args[0] != "--pki-client-certificate-info")
            return false;
        using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(certificatePath));
        output.WriteLine(JsonSerializer.Serialize(new
        {
            serial = certificate.SerialNumber,
            sha256 = certificate.GetCertHashString(HashAlgorithmName.SHA256),
            notBefore = certificate.NotBefore.ToUniversalTime(),
            notAfter = certificate.NotAfter.ToUniversalTime()
        }));
        return true;
    }
}
