using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Cassandra;
using Cassandra.Data.Linq;
using Coflnet.Sky.Core;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

/// <summary>
/// A forward-only, private, UTC-day projection of corrected persisted profits.
/// Durable logged batches copy the same value and write timestamp as the source.
/// Pending rows suppress an incomplete day instead of publishing a partial sum.
/// </summary>
public sealed class ProfitMetricProjection
{
    public const int Shards = 32;
    public const int RetentionDays = 400;
    private readonly ISession session;
    private readonly DateTime coverage;
    private readonly int maximumRows;
    private DateTime privacyLoadedAt;
    public bool Enabled { get; }

    public ProfitMetricProjection(ISession session, IConfiguration configuration)
    {
        this.session = session;
        Enabled = configuration.GetValue<bool>("ProfitMetrics:Enabled");
        if (!Enabled) return;
        if (!DateTime.TryParse(configuration["ProfitMetrics:CoverageStartUtc"], CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out coverage) || coverage.TimeOfDay != TimeSpan.Zero)
            throw new InvalidOperationException("ProfitMetrics:CoverageStartUtc must be an explicit UTC midnight after every writer has this projection enabled.");
        maximumRows = Math.Clamp(configuration.GetValue("ProfitMetrics:MaximumRowsPerDay", 100_000), 100, 1_000_000);
        session.Execute("CREATE TABLE IF NOT EXISTS profit_metric_days (day timestamp, shard int, kind int, sale_id uuid, flipper uuid, sell_time timestamp, uid bigint, profit bigint, PRIMARY KEY ((day, shard), kind, sale_id)) WITH default_time_to_live = 34560000");
        session.Execute("CREATE TABLE IF NOT EXISTS profit_metric_owners (flipper uuid, day timestamp, shard int, sale_id uuid, PRIMARY KEY (flipper, day, shard, sale_id)) WITH default_time_to_live = 34560000");
        session.Execute("CREATE TABLE IF NOT EXISTS profit_metric_sales (sale_id uuid PRIMARY KEY, day timestamp, flipper uuid, revision bigint) WITH default_time_to_live = 34560000");
        session.Execute("CREATE TABLE IF NOT EXISTS profit_metric_erased (flipper uuid PRIMARY KEY)");
    }

    public void PrivacyLoaded() => privacyLoadedAt = DateTime.UtcNow;
    public DateTime CoverageStart => coverage;

    internal sealed record SaleRevision(long Timestamp, DateTime Day, Guid Owner);

    // Cassandra CAS allocates a shared clock across replicas before any source mutation.
    internal async Task<SaleRevision> Reserve(Guid sale, DateTime day, Guid owner, long clock)
    {
        var first = Statement("INSERT INTO profit_metric_sales (sale_id, day, flipper, revision) VALUES (?, ?, ?, 0) IF NOT EXISTS", sale, day, owner);
        first.SetSerialConsistencyLevel(ConsistencyLevel.Serial);
        await session.ExecuteAsync(first);
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var row = (await Execute("SELECT day, flipper, revision FROM profit_metric_sales WHERE sale_id = ?", sale)).Single();
            var previous = row.GetValue<long>("revision");
            var next = Math.Max(checked(previous + 2), clock);
            var reserve = Statement("UPDATE profit_metric_sales SET revision = ? WHERE sale_id = ? IF revision = ?", next, sale, previous);
            reserve.SetSerialConsistencyLevel(ConsistencyLevel.Serial);
            if ((await session.ExecuteAsync(reserve)).Single().GetValue<bool>("[applied]"))
                return new(next, row.GetValue<DateTimeOffset>("day").UtcDateTime.Date, row.GetValue<Guid>("flipper"));
        }
        throw new InvalidOperationException("Profit revision contention exceeded the bounded retry budget.");
    }

    internal static Guid SaleId(PastFlip flip) => flip.SellAuctionId != Guid.Empty ? flip.SellAuctionId : flip.PurchaseAuctionId;
    internal static int Shard(Guid sale) => (int)(BitConverter.ToUInt32(SHA256.HashData(sale.ToByteArray()), 0) % Shards);
    internal static long MeasuredProfit(PastFlip flip) => flip.PurchaseCost < 0
        || (flip.Flags & (FlipFlags.UnknownCost | FlipFlags.UncertainCost)) != 0 ? 0 : flip.Profit;

    private static SimpleStatement Statement(string cql, params object[] values)
    {
        var statement = new SimpleStatement(cql, values);
        statement.SetConsistencyLevel(ConsistencyLevel.Quorum);
        return statement;
    }
    private Task<RowSet> Execute(string cql, params object[] values) => session.ExecuteAsync(Statement(cql, values));

    private SimpleStatement Pending(PastFlip flip, DateTime day, Guid sale, int shard, long revision) => Statement(
        "INSERT INTO profit_metric_days (day, shard, kind, sale_id, flipper, sell_time, uid, profit) VALUES (?, ?, 0, ?, ?, ?, ?, 0) USING TIMESTAMP ?",
        day, shard, sale, flip.Flipper, flip.SellTime, flip.Uid, revision);

    /// <summary>Commit the source and its projection using one durable logged batch.</summary>
    public async Task Write(PastFlip flip, CqlCommand source)
    {
        if (!Enabled)
        {
            await source.ExecuteAsync();
            return;
        }
        if ((await Execute("SELECT flipper FROM profit_metric_erased WHERE flipper = ?", flip.Flipper)).Any()) return;
        if (flip.SellTime.ToUniversalTime().Date < coverage) { await source.ExecuteAsync(); return; }
        var day = DateTime.SpecifyKind(flip.SellTime.ToUniversalTime().Date, DateTimeKind.Utc);
        var sale = SaleId(flip);
        var reserved = await Reserve(sale, day, flip.Flipper, (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10);
        var revision = reserved.Timestamp;
        var writtenAt = new DateTimeOffset(DateTime.UnixEpoch.AddTicks(revision * 10));
        source.SetTimestamp(writtenAt);
        var shard = Shard(sale);
        await session.ExecuteAsync(new BatchStatement().SetBatchType(BatchType.Logged).Add(Pending(flip, day, sale, shard, revision))
            .Add(Statement("INSERT INTO profit_metric_owners (flipper, day, shard, sale_id) VALUES (?, ?, ?, ?) USING TIMESTAMP ?",
                flip.Flipper, day, shard, sale, revision)).SetConsistencyLevel(ConsistencyLevel.Quorum));
        if (sale == Guid.Empty || reserved.Day != day || reserved.Owner != flip.Flipper)
        {
            // A correction which changes the identity's day/owner requires reconciliation.
            // Preserve the source write, and keep both days visibly incomplete.
            if (sale != Guid.Empty)
                await session.ExecuteAsync(Pending(flip, reserved.Day, sale, shard, revision));
            await source.ExecuteAsync();
            return;
        }
        var commit = new BatchStatement().SetBatchType(BatchType.Logged);
        commit.SetTimestamp(writtenAt);
        await session.ExecuteAsync(commit.Add(source)
            .Add(Statement("INSERT INTO profit_metric_days (day, shard, kind, sale_id, flipper, sell_time, uid, profit) VALUES (?, ?, 1, ?, ?, ?, ?, ?) USING TIMESTAMP ?",
                day, shard, sale, flip.Flipper, flip.SellTime, flip.Uid, MeasuredProfit(flip), revision))
            .Add(Statement("DELETE FROM profit_metric_days USING TIMESTAMP ? WHERE day = ? AND shard = ? AND kind = 0 AND sale_id = ?",
                revision + 1, day, shard, sale)).SetConsistencyLevel(ConsistencyLevel.Quorum));
    }

    /// <summary>Erase the private projection before acknowledging source erasure.</summary>
    public async Task Erase(Guid owner, Func<Task> eraseSource)
    {
        if (!Enabled) { await eraseSource(); return; }
        // Permanent suppression closes the race with a writer that already passed its local opt-out check.
        await Execute("INSERT INTO profit_metric_erased (flipper) VALUES (?)", owner);
        await eraseSource();
        var rows = await Execute("SELECT day, shard, sale_id FROM profit_metric_owners WHERE flipper = ?", owner);
        foreach (var row in rows)
        {
            var day = row.GetValue<DateTimeOffset>("day");
            var shard = row.GetValue<int>("shard");
            var sale = row.GetValue<Guid>("sale_id");
            await session.ExecuteAsync(new BatchStatement().SetBatchType(BatchType.Logged)
                .Add(Statement("DELETE FROM profit_metric_days WHERE day = ? AND shard = ? AND kind = 0 AND sale_id = ?", day, shard, sale))
                .Add(Statement("DELETE FROM profit_metric_days WHERE day = ? AND shard = ? AND kind = 1 AND sale_id = ?", day, shard, sale))
                .Add(Statement("DELETE FROM profit_metric_sales WHERE sale_id = ?", sale))
                .Add(Statement("DELETE FROM profit_metric_owners WHERE flipper = ? AND day = ? AND shard = ? AND sale_id = ?", owner, day, shard, sale))
                .SetConsistencyLevel(ConsistencyLevel.Quorum));
        }
    }

    public sealed record Snapshot(DateTime Day, bool Complete, long? Coins, string Reason);

    /// <summary>Read a bounded single UTC day; no historical source-table scan is performed.</summary>
    public async Task<Snapshot> Read(DateTime day, CancellationToken token = default)
    {
        day = DateTime.SpecifyKind(day.Date, DateTimeKind.Utc);
        if (!Enabled || day < coverage || day > DateTime.UtcNow.Date) return new(day, false, null, "outside_coverage");
        if (privacyLoadedAt < DateTime.UtcNow.AddHours(-2)) return new(day, false, null, "privacy_not_current");
        var erasedRows = await Execute("SELECT flipper FROM profit_metric_erased LIMIT 10001");
        var erased = erasedRows.Select(row => row.GetValue<Guid>("flipper")).ToHashSet();
        if (erased.Count > 10_000) return new(day, false, null, "privacy_limit");
        long total = 0;
        var count = 0;
        for (var shard = 0; shard < Shards; shard++)
        {
            token.ThrowIfCancellationRequested();
            var rows = await Execute("SELECT kind, flipper, profit FROM profit_metric_days WHERE day = ? AND shard = ? LIMIT ?", day, shard, maximumRows + 1);
            foreach (var row in rows)
            {
                if (++count > maximumRows) return new(day, false, null, "row_limit");
                var owner = row.GetValue<Guid>("flipper");
                if (erased.Contains(owner) || PlayerOptOut.IsOptedOut(owner)) continue;
                if (row.GetValue<int>("kind") == 0) return new(day, false, null, "pending_write");
                try { total = checked(total + row.GetValue<long>("profit")); }
                catch (OverflowException) { return new(day, false, null, "overflow"); }
            }
        }
        return Math.Abs((decimal)total) > 9_007_199_254_740_991m ? new(day, false, null, "precision_limit") : new(day, true, total, "ok");
    }
}
