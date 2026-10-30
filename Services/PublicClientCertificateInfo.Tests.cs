using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

public class PublicClientCertificateInfoTests
{
    [Test]
    public void ExplicitModeEmitsOnlyPublicMetadataFromCertificateOnlyFile()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var request = new CertificateRequest("CN=synthetic-public-only", key, HashAlgorithmName.SHA384);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, certificate.ExportCertificatePem());
            using var output = new StringWriter();
            Assert.That(PublicClientCertificateInfo.TryRun(new[] { "--pki-client-certificate-info" }, output, path), Is.True);
            using var document = JsonDocument.Parse(output.ToString());
            var root = document.RootElement;
            Assert.That(root.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "serial", "sha256", "notBefore", "notAfter" }));
            Assert.That(root.GetProperty("serial").GetString(), Is.EqualTo(certificate.SerialNumber));
            Assert.That(root.GetProperty("sha256").GetString(), Is.EqualTo(certificate.GetCertHashString(HashAlgorithmName.SHA256)));
            Assert.That(root.GetProperty("notBefore").GetDateTime(), Is.EqualTo(certificate.NotBefore.ToUniversalTime()));
            Assert.That(root.GetProperty("notAfter").GetDateTime(), Is.EqualTo(certificate.NotAfter.ToUniversalTime()));
            Assert.That(output.ToString(), Does.Not.Contain("PRIVATE").And.Not.Contain("BEGIN").And.Not.Contain("synthetic-public-only"));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void NormalArgumentsPerformNoFileOrOutputAccess()
    {
        foreach (var args in new[] { Array.Empty<string>(), new[] { "--urls", "http://localhost:8000" }, new[] { "--pki-client-certificate-info-extra" } })
        {
            using var output = new StringWriter();
            Assert.That(PublicClientCertificateInfo.TryRun(args, output, "/nonexistent/no-certificate"), Is.False);
            Assert.That(output.ToString(), Is.Empty);
        }
    }

    [Test]
    public void ExplicitMissingCertificateFailsInsteadOfStartingHost()
    {
        Assert.Throws<DirectoryNotFoundException>(() => PublicClientCertificateInfo.TryRun(new[] { "--pki-client-certificate-info" }, TextWriter.Null, "/nonexistent/no-certificate"));
    }
}
