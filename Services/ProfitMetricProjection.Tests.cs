using System.Threading.Tasks;
using Cassandra;
using Cassandra.Data.Linq;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

public class ProfitMetricProjectionTests
{
    [Test]
    public void UnitsAndIdentityPreserveCorrectionsAndLosses()
    {
        var flip = Flip(DateTime.UtcNow.Date, 1, Guid.NewGuid(), -100);
        Assert.That(ProfitMetricProjection.MeasuredProfit(flip), Is.EqualTo(-100));
        var identity = ProfitMetricProjection.SaleId(flip);
        flip.Profit = 500;
        Assert.That(ProfitMetricProjection.SaleId(flip), Is.EqualTo(identity));
        flip.Flags = FlipFlags.UnknownCost;
        Assert.That(ProfitMetricProjection.MeasuredProfit(flip), Is.Zero);
        flip.Flags = FlipFlags.UncertainCost;
        Assert.That(ProfitMetricProjection.MeasuredProfit(flip), Is.Zero);
        Assert.That(ProfitMetricProjection.Shard(identity), Is.InRange(0, ProfitMetricProjection.Shards - 1));
    }

    [Test, Explicit("Run scripts/cassandra-smoke.sh against its disposable pinned Cassandra instance.")]
    public async Task CassandraServerRoundTrip()
    {
        using var cluster = Cluster.Builder().AddContactPoint(Environment.GetEnvironmentVariable("CASSANDRA_SMOKE_HOST"))
            .WithPort(int.Parse(Environment.GetEnvironmentVariable("CASSANDRA_SMOKE_PORT"))).Build();
        using var bootstrap = cluster.Connect();
        bootstrap.Execute("CREATE KEYSPACE profit_smoke WITH replication = {'class': 'SimpleStrategy', 'replication_factor': 1}");
        using var session = cluster.Connect("profit_smoke");
        var today = DateTime.UtcNow.Date;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["ProfitMetrics:Enabled"] = "true", ["ProfitMetrics:CoverageStartUtc"] = today.AddDays(-4).ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["ProfitMetrics:MaximumRowsPerDay"] = "100"
        }).Build();
        var projection = new ProfitMetricProjection(session, config);
        var storage = new FlipStorageService(NullLogger<FlipStorageService>.Instance, config, session, projection);
        storage.GetTable(session).CreateIfNotExists();
        Assert.That((await projection.Read(today)).Complete, Is.False, "No data before privacy has loaded.");
        projection.PrivacyLoaded();
        Assert.That((await projection.Read(today.AddDays(-5))).Complete, Is.False, "No invented zero before coverage.");
        var secondIssuer = new ProfitMetricProjection(session, config);
        var sharedSale = Guid.NewGuid(); var sharedOwner = Guid.NewGuid();
        var sharedClock = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;
        var revisions = await Task.WhenAll(Enumerable.Range(0, 24).Select(index => (index % 2 == 0 ? projection : secondIssuer)
            .Reserve(sharedSale, today, sharedOwner, sharedClock)));
        Assert.That(revisions.Select(revision => revision.Timestamp).Distinct().Count(), Is.EqualTo(24), "Independent replica clocks cannot issue duplicate source timestamps.");
        Assert.That(revisions.Max(revision => revision.Timestamp) - revisions.Min(revision => revision.Timestamp), Is.EqualTo(46), "CAS revisions advance two microseconds, reserving the pending tombstone timestamp.");

        var owner = Guid.NewGuid();
        var first = Flip(today.AddHours(1), 1, owner, 100);
        await storage.SaveFlip(first);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => storage.SaveFlip(first)));
        Assert.That((await projection.Read(today)).Coins, Is.EqualTo(100), "Retries never increment the sum.");
        var historical = Flip(today.AddSeconds(-30), 8, Guid.NewGuid(), 50);
        await storage.SaveFlip(historical);
        historical.Profit = 75;
        await storage.SaveFlip(historical);
        Assert.That((await projection.Read(today.AddDays(-1))).Coins, Is.EqualTo(75), "A late correction updates the closed previous UTC day, including its final minute.");
        await VerifyPrivateListener(projection, config, owner);
        first.Profit = 75;
        await storage.SaveFlip(first);
        var loss = Flip(today.AddHours(2), 2, owner, -25);
        await storage.SaveFlips([loss]);
        var uncertain = Flip(today.AddHours(3), 3, owner, 900);
        uncertain.Flags = FlipFlags.UncertainCost;
        await storage.SaveFlip(uncertain);
        Assert.That((await projection.Read(today)).Coins, Is.EqualTo(50), "Corrections, losses and uncertain costs retain their units and meaning.");

        var retry = Flip(today.AddHours(4), 4, owner, 10);
        try { await projection.Write(retry, new Table<MissingSource>(session).Insert(new MissingSource { Id = Guid.NewGuid() })); Assert.Fail("Missing source table should fail."); }
        catch (InvalidQueryException) { }
        Assert.That((await projection.Read(today)).Reason, Is.EqualTo("pending_write"), "Failed source write cannot publish a partial aggregate.");
        await storage.SaveFlip(retry);
        Assert.That((await projection.Read(today)).Coins, Is.EqualTo(60), "Retry clears the durable pending marker.");

        var other = Guid.NewGuid();
        await storage.SaveFlip(Flip(today.AddHours(5), 5, other, 20));
        try { await projection.Erase(owner, () => throw new InvalidOperationException("fixture source failure")); }
        catch (InvalidOperationException) { }
        Assert.That((await projection.Read(today)).Coins, Is.EqualTo(20), "Erasure excludes an owner even when physical deletion must retry.");
        await storage.DeleteAllFlips(owner);
        await storage.SaveFlip(first);
        Assert.That(await storage.GetAllFlips(owner), Is.Empty, "An in-flight or stale opt-out writer cannot resurrect erased data.");
        Assert.That((await projection.Read(today)).Coins, Is.EqualTo(20));

        var raceDay = today.AddDays(-2);
        var raceOwner = Guid.NewGuid();
        await Task.WhenAll(Enumerable.Range(0, 24).Select(value => storage.SaveFlip(Flip(raceDay.AddHours(2), 6, raceOwner, value))));
        var persisted = (await storage.GetAllFlips(raceOwner)).Single();
        Assert.That((await projection.Read(raceDay)).Coins, Is.EqualTo(persisted.Profit), "Concurrent corrections have the same last-write ordering as the source.");

        var moved = Flip(today.AddDays(-1).AddHours(23), 7, Guid.NewGuid(), 30);
        await storage.SaveFlip(moved);
        moved.SellTime = today.AddHours(1);
        await storage.SaveFlip(moved);
        Assert.That((await projection.Read(today.AddDays(-1))).Complete, Is.False, "A cross-midnight identity correction blocks its previous day.");
        Assert.That((await projection.Read(today)).Complete, Is.False, "A cross-midnight identity correction blocks its destination day.");

        var boundedDay = today.AddDays(-3);
        for (var i = 0; i < 101; i++)
            await session.ExecuteAsync(new SimpleStatement("INSERT INTO profit_metric_days (day, shard, kind, sale_id, flipper, profit) VALUES (?, 0, 1, ?, ?, 1)", boundedDay, Guid.NewGuid(), other));
        Assert.That((await projection.Read(boundedDay)).Reason, Is.EqualTo("row_limit"), "The exporter refuses oversized day partitions.");
    }

    private static PastFlip Flip(DateTime time, long uid, Guid owner, long profit) => new()
    {
        Flipper = owner, SellTime = DateTime.SpecifyKind(time, DateTimeKind.Utc), PurchaseTime = time.AddMinutes(-1), Uid = uid,
        SellAuctionId = Guid.Parse($"30000000-0000-0000-0000-{uid:D12}"), PurchaseAuctionId = Guid.Parse($"20000000-0000-0000-0000-{uid:D12}"),
        ItemName = "fixture", ItemTag = "FIXTURE", PurchaseCost = 1000, SellPrice = 1000 + profit, Profit = profit
    };

    private static async Task VerifyPrivateListener(ProfitMetricProjection projection, IConfiguration config, Guid owner)
    {
        var reservation = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((System.Net.IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var privateConfig = new ConfigurationBuilder().AddConfiguration(config)
            .AddInMemoryCollection(new Dictionary<string, string> { ["ProfitMetrics:Port"] = port.ToString() }).Build();
        using var exporter = new ProfitMetricExporter(projection, privateConfig, NullLogger<ProfitMetricExporter>.Instance);
        await exporter.StartAsync(default);
        try
        {
            using var http = new System.Net.Http.HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) };
            var body = await http.GetStringAsync("/metrics");
            Assert.That(body, Does.Contain($"sky_fliptracker_realized_profit_coins{{day=\"{DateTime.UtcNow:yyyy-MM-dd}\"}} 100"));
            Assert.That(body, Does.Contain($"sky_fliptracker_realized_profit_coins{{day=\"{DateTime.UtcNow.AddDays(-1):yyyy-MM-dd}\"}} 75"), "Closed days remain exported after midnight and receive late corrections.");
            Assert.That(body, Does.Not.Contain(owner.ToString()));
            Assert.That((await http.GetAsync("/anything-else")).StatusCode, Is.EqualTo(System.Net.HttpStatusCode.NotFound));
            using var registry = new System.IO.MemoryStream();
            await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(registry);
            Assert.That(System.Text.Encoding.UTF8.GetString(registry.ToArray()), Does.Not.Contain("sky_fliptracker_realized_profit_coins"),
                "The normal application's public metric registry must not acquire business data.");
        }
        finally { using var stop = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5)); await exporter.StopAsync(stop.Token); }
    }

    private sealed class MissingSource
    {
        [global::Cassandra.Mapping.Attributes.PartitionKey] public Guid Id { get; set; }
    }
}
