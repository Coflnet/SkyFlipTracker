using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Cassandra;
using Coflnet.Sky.Core;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

class CassandraSmokeTests
{
    [Test]
    public void RunnerPlanPinsLifecycleTimeoutsAndExplicitTest()
    {
        var projectDirectory = FindProjectDirectory();
        var runner = Path.Combine(projectDirectory, "scripts", "cassandra-smoke.sh");
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "bash",
            ArgumentList = { runner, "--plan" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });

        Assert.That(process, Is.Not.Null);
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.That(process.WaitForExit(5_000), Is.True, "Runner plan timed out.");
        Assert.Multiple(() =>
        {
            Assert.That(process.ExitCode, Is.Zero, error);
            Assert.That(output, Does.Contain("cassandra:4.1.7@sha256:"));
            Assert.That(output, Does.Contain("READINESS_TIMEOUT_SECONDS=180"));
            Assert.That(output, Does.Contain("TOTAL_TIMEOUT_SECONDS=420"));
            Assert.That(output, Does.Contain("RESOURCES=container,network,volume"));
            Assert.That(output, Does.Contain("CLEANUP=always"));
            Assert.That(output, Does.Contain("TIMEOUT_CLEANUP=surviving-parent"));
            Assert.That(output, Does.Contain("docker logs --tail 40"));
            Assert.That(output, Does.Contain("STARTUP_OUTPUT=bounded"));
            Assert.That(output, Does.Contain("TEST_OUTPUT=bounded"));
            Assert.That(output, Does.Contain("FullyQualifiedName=Coflnet.Sky.SkyAuctionTracker.Services.CassandraSmokeTests.CassandraServerRoundTrip"));
        });
    }

    [Test]
    [Explicit("Run through scripts/cassandra-smoke.sh; it requires a disposable Cassandra container.")]
    public async Task CassandraServerRoundTrip()
    {
        var host = Environment.GetEnvironmentVariable("CASSANDRA_SMOKE_HOST")
            ?? throw new InvalidOperationException("CASSANDRA_SMOKE_HOST is required.");
        var port = int.Parse(Environment.GetEnvironmentVariable("CASSANDRA_SMOKE_PORT")
            ?? throw new InvalidOperationException("CASSANDRA_SMOKE_PORT is required."));
        using var cluster = Cluster.Builder()
            .AddContactPoint(host)
            .WithPort(port)
            .WithSocketOptions(new SocketOptions().SetConnectTimeoutMillis(30_000).SetReadTimeoutMillis(30_000))
            .Build();
        using var bootstrapSession = cluster.Connect();
        bootstrapSession.Execute(
            "CREATE KEYSPACE smoke WITH replication = {'class': 'SimpleStrategy', 'replication_factor': 1}");
        using var session = cluster.Connect("smoke");
        var service = new FlipStorageService(
            NullLogger<FlipStorageService>.Instance, new ConfigurationBuilder().Build(), session);
        var ensureTable = typeof(FlipStorageService).GetMethod(
            "EnsureUnknownFlipsTable", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(FlipStorageService), "EnsureUnknownFlipsTable");
        await (Task)ensureTable.Invoke(service, [session]);

        var start = new DateTime(2026, 8, 31, 10, 0, 0, DateTimeKind.Utc);
        var end = start.AddMinutes(10);
        var unknown = LowPricedAuction.FinderType.UNKOWN;
        var nonzero = Enum.GetValues<LowPricedAuction.FinderType>().First(value => (int)value != 0);
        var expectedUnknown = CreateFlip(unknown, start.AddMinutes(2), 1001, "UNKNOWN_SMOKE");
        var expectedNonzero = CreateFlip(nonzero, start.AddMinutes(3), 1002, "NONZERO_SMOKE");
        var inserted = new[]
        {
            expectedUnknown,
            CreateFlip(unknown, start.AddMinutes(1), 1003, "UNKNOWN_OLDER_SMOKE"),
            CreateFlip(unknown, end.AddSeconds(1), 1004, "UNKNOWN_OUTSIDE_SMOKE"),
            expectedNonzero
        };
        foreach (var flip in inserted)
            await service.SaveUnknownFlip(flip);

        var timeBoundResult = await service.GetMissedFlips(start, end, "finder_unknown", 5);
        var limitedUnknownResult = await service.GetMissedFlips(start, end, "finder_unknown", 1);
        var limitedNonzeroResult = await service.GetMissedFlips(start, end, "blocked_or_outsped", 1);
        Assert.Multiple(() =>
        {
            Assert.That(timeBoundResult, Has.Count.EqualTo(2));
            Assert.That(timeBoundResult, Has.None.Matches<PastFlip>(flip => flip.Uid == 1004));
            Assert.That(limitedUnknownResult, Has.Count.EqualTo(1));
            Assert.That(limitedNonzeroResult, Has.Count.EqualTo(1));
            AssertRoundTrip(limitedUnknownResult.Single(), expectedUnknown);
            AssertRoundTrip(limitedNonzeroResult.Single(), expectedNonzero);
        });
    }

    private static PastFlip CreateFlip(
        LowPricedAuction.FinderType finderType, DateTime sellTime, long uid, string itemTag)
    {
        return new PastFlip
        {
            Flipper = Guid.Parse("10000000-0000-0000-0000-000000000001"),
            ItemName = itemTag,
            ItemTier = Tier.RARE,
            ItemTag = itemTag,
            PurchaseAuctionId = Guid.Parse($"20000000-0000-0000-0000-{uid:D12}"),
            PurchaseCost = 1_000,
            PurchaseTime = sellTime.AddMinutes(-1),
            TargetPrice = 1_500,
            FinderType = finderType,
            SellAuctionId = Guid.Parse($"30000000-0000-0000-0000-{uid:D12}"),
            SellPrice = 1_400,
            SellTime = sellTime,
            Uid = uid,
            Profit = 400,
            Version = 1,
            Flags = FlipFlags.None
        };
    }

    private static void AssertRoundTrip(PastFlip actual, PastFlip expected)
    {
        Assert.That(actual.FinderType, Is.EqualTo(expected.FinderType));
        Assert.That(actual.ItemTag, Is.EqualTo(expected.ItemTag));
        Assert.That(actual.PurchaseAuctionId, Is.EqualTo(expected.PurchaseAuctionId));
        Assert.That(actual.SellAuctionId, Is.EqualTo(expected.SellAuctionId));
        Assert.That(actual.SellTime, Is.EqualTo(expected.SellTime));
        Assert.That(actual.Uid, Is.EqualTo(expected.Uid));
    }

    private static string FindProjectDirectory()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "SkyFlipTracker.csproj")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the project source directory.");
    }
}
