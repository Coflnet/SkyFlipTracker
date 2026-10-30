using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Infrastructure;

public class DatabaseTlsTests
{
    [TestCase(0u, 300u)]
    [TestCase(40u, 40u)]
    [TestCase(900u, 300u)]
    public void ExplicitTlsPreservesDatabaseAuthenticationAndBoundsNewPhysicalConnections(uint lifetime, uint expected)
    {
        var configuration = Configuration(new()
        {
            ["DB_CONNECTION"] = $"Server=db-a.internal,db-b.internal;Port=3307;User ID=synthetic-tracker;Password=synthetic-only;Database=synthetic-tracker;MaximumPoolSize=23;ConnectionTimeout=90;ConnectionLifeTime={lifetime};SslMode=None;CertificateFile=old.pfx;CertificatePassword=old-pfx-only;CertificateStoreLocation=CurrentUser;CertificateThumbprint=old;SslCa=old-ca.pem;SslCert=old.pem;SslKey=old.key",
            ["DB:TLS:CA_CERT_PATH"] = "roots.pem",
            ["DB:TLS:CERT_PATH"] = "client.pem",
            ["DB:TLS:KEY_PATH"] = "client.key"
        });
        var options = new MySqlConnectionStringBuilder(DatabaseTls.ConnectionString(configuration));
        Assert.Multiple(() =>
        {
            Assert.That(options.Server, Is.EqualTo("db-a.internal,db-b.internal"));
            Assert.That(options.Port, Is.EqualTo(3307));
            Assert.That(options.UserID, Is.EqualTo("synthetic-tracker"));
            Assert.That(options.Password, Is.EqualTo("synthetic-only"));
            Assert.That(options.Database, Is.EqualTo("synthetic-tracker"));
            Assert.That(options.MaximumPoolSize, Is.EqualTo(23));
            Assert.That(options.ConnectionTimeout, Is.EqualTo(10));
            Assert.That(options.ConnectionLifeTime, Is.EqualTo(expected));
            Assert.That(options.SslMode, Is.EqualTo(MySqlSslMode.Required));
            Assert.That(options.SslCa, Is.Empty);
            Assert.That(options.SslCert, Is.Empty);
            Assert.That(options.SslKey, Is.Empty);
            Assert.That(options.CertificateFile, Is.Empty);
            Assert.That(options.CertificatePassword, Is.Empty);
            Assert.That(options.CertificateStoreLocation, Is.EqualTo(MySqlCertificateStoreLocation.None));
            Assert.That(options.CertificateThumbprint, Is.Empty);
        });
    }

    [Test]
    public void OptOutPreservesRawConnectionStringIncludingLongTimeoutAndLegacyTlsOptions()
    {
        const string original = "Server = legacy-a,legacy-b; Port=3310; User ID=synthetic; Password=synthetic-only; Database=synthetic; ConnectionTimeout=3600; ConnectionLifeTime=0; SslMode=Preferred; CertificateFile=legacy.pfx; CertificatePassword=old-pfx-only";
        Assert.That(DatabaseTls.ConnectionString(Configuration(new() { ["DB_CONNECTION"] = original })), Is.EqualTo(original));
    }

    [Test]
    public void OptOutPreservesLegacyTlsAndPartialConfigurationFailsClosed()
    {
        var options = new MySqlConnectionStringBuilder(DatabaseTls.ConnectionString(Configuration(new())));
        Assert.That(options.SslMode, Is.EqualTo(MySqlSslMode.None));
        Assert.That(options.ConnectionLifeTime, Is.Zero);
        foreach (var missing in new[] { "CA_CERT_PATH", "CERT_PATH", "KEY_PATH" })
        {
            var values = new Dictionary<string, string>
            {
                ["DB:TLS:CA_CERT_PATH"] = "roots.pem", ["DB:TLS:CERT_PATH"] = "client.pem",
                ["DB:TLS:KEY_PATH"] = "client.key"
            };
            values.Remove("DB:TLS:" + missing);
            Assert.Throws<InvalidOperationException>(() => DatabaseTls.ConnectionString(Configuration(values)));
        }
    }

    [Test]
    public async Task LogicalOpensDoNoPkiIoAndPhysicalCertificateProviderFailsClosed()
    {
        using var owner = new PemTlsOptions("missing-roots", "missing-cert", "missing-key");
        var interceptor = new DatabaseTlsInterceptor(owner);
        using var connection = new MySqlConnection();
        Assert.DoesNotThrow(() => interceptor.ConnectionOpening(connection, null, default));
        Assert.That(connection.RemoteCertificateValidationCallback, Is.Not.Null);
        Assert.That(connection.ProvideClientCertificatesCallback, Is.Not.Null);
        Assert.Throws<FileNotFoundException>(() => connection.ProvideClientCertificatesCallback(new()));
        connection.RemoteCertificateValidationCallback = null;
        connection.ProvideClientCertificatesCallback = null;
        await interceptor.ConnectionOpeningAsync(connection, null, default);
        Assert.That(connection.RemoteCertificateValidationCallback, Is.Not.Null);
        Assert.Throws<FileNotFoundException>(() => connection.ProvideClientCertificatesCallback(new()));
    }

    [Test]
    public async Task PhysicalProviderRegistersOnlyPublicIntermediateAndReloadsRenewedIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tracker-sql-pki-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var root = Issue(null, "root", true);
        using var intermediate = Issue(root, "intermediate", true);
        using var first = Issue(intermediate, "first", false);
        using var renewed = Issue(intermediate, "renewed", false);
        using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        try
        {
            File.WriteAllText(Path.Combine(directory, "roots.pem"), root.ExportCertificatePem());
            void Publish(X509Certificate2 certificate)
            {
                File.WriteAllText(Path.Combine(directory, "client.pem"), certificate.ExportCertificatePem() + "\n" + intermediate.ExportCertificatePem());
                using var key = certificate.GetECDsaPrivateKey();
                File.WriteAllText(Path.Combine(directory, "client.key"), key.ExportPkcs8PrivateKeyPem());
            }
            Publish(first);
            using var owner = new PemTlsOptions(Path.Combine(directory, "roots.pem"), Path.Combine(directory, "client.pem"), Path.Combine(directory, "client.key"));
            using var connection = new MySqlConnection();
            new DatabaseTlsInterceptor(owner).ConnectionOpening(connection, null, default);
            Assert.That(store.Certificates.Find(X509FindType.FindByThumbprint, intermediate.Thumbprint, false), Is.Empty);
            var certificates = new X509CertificateCollection();
            await connection.ProvideClientCertificatesCallback(certificates);
            Assert.That(certificates.Count, Is.EqualTo(1));
            Assert.That(certificates[0].GetCertHashString(), Is.EqualTo(first.Thumbprint));
            Assert.That(store.Certificates.Find(X509FindType.FindByThumbprint, intermediate.Thumbprint, false).Count, Is.EqualTo(1));
            Assert.That(store.Certificates.Find(X509FindType.FindByThumbprint, root.Thumbprint, false), Is.Empty);
            Publish(renewed);
            certificates.Clear();
            await connection.ProvideClientCertificatesCallback(certificates);
            Assert.That(certificates[0].GetCertHashString(), Is.EqualTo(renewed.Thumbprint));
        }
        finally
        {
            store.Remove(intermediate);
            Directory.Delete(directory, true);
        }
    }

    private static X509Certificate2 Issue(X509Certificate2 issuer, string name, bool authority)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA384);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(authority, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(authority ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature, true));
        if (!authority)
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        if (issuer == null)
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(5));
        using var certificate = request.Create(issuer, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(authority ? 24 : 12), RandomNumberGenerator.GetBytes(16));
        return certificate.CopyWithPrivateKey(key);
    }

    private static IConfiguration Configuration(Dictionary<string, string> values)
    {
        values.TryAdd("DB_CONNECTION", "Server=legacy;User ID=synthetic-tracker;Password=synthetic-only;SslMode=None");
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
