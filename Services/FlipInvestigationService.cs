using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Api.Client.Api;
using Coflnet.Sky.Core;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Coflnet.Sky.SkyAuctionTracker.Models.Investigation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

/// <summary>
/// Joins found -> sent -> clicked -> bought for single auctions and for the purchases of a player.
/// Internal tooling, reached via port-forward only. Player hashes are lossy: recipients that never bought or sold
/// anything have no uuid mapping, see <see cref="PlayerIdLookup"/>.
/// </summary>
public class FlipInvestigationService
{
    private static readonly TimeSpan ExternalTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LinkedAccountTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DbTimeout = TimeSpan.FromSeconds(60);

    private readonly TrackerDbContext db;
    private readonly FlipStorageService storage;
    private readonly IAuctionsApi auctionsApi;
    private readonly McConnect.Api.IConnectApi connectApi;
    private readonly ILogger<FlipInvestigationService> logger;

    public FlipInvestigationService(TrackerDbContext db, FlipStorageService storage, IAuctionsApi auctionsApi,
        McConnect.Api.IConnectApi connectApi, ILogger<FlipInvestigationService> logger)
    {
        this.db = db;
        this.storage = storage;
        this.auctionsApi = auctionsApi;
        this.connectApi = connectApi;
        this.logger = logger;
        // the default 5 seconds are too short for aggregates over a month of events
        db?.Database.SetCommandTimeout(DbTimeout);
    }

    /// <summary>
    /// Runs an external call, returns default and a warning on failure or timeout
    /// </summary>
    private async Task<(T value, string warning)> Guarded<T>(Func<Task<T>> call, string what, TimeSpan timeout)
    {
        try
        {
            var task = Task.Run(call);
            if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
            {
                _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return (default, $"{what} timed out after {timeout.TotalSeconds}s");
            }
            return (await task, null);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Investigation call {What} failed", what);
            return (default, $"{what} failed: {e.GetType().Name} {e.Message}");
        }
    }

    private static void Add(List<string> warnings, string warning)
    {
        if (warning != null)
            warnings.Add(warning);
    }

    /// <summary>
    /// The player's own hash plus the hashes of linked accounts (opted out accounts are left out)
    /// </summary>
    public async Task<HashSet<long>> GetLinkedHashes(Guid player, List<string> warnings)
    {
        var hashes = new HashSet<long> { TrackerService.HashId(player.ToString("N")) };
        var (user, warning) = await Guarded(() => connectApi.ConnectMinecraftMcUuidGetAsync(player.ToString("N")), "linked accounts", LinkedAccountTimeout);
        if (warning != null && !warning.Contains("404"))
            warnings.Add(warning);
        foreach (var account in user?.Accounts ?? [])
        {
            if (string.IsNullOrEmpty(account.AccountUuid) || PlayerOptOut.IsOptedOut(account.AccountUuid))
                continue;
            hashes.Add(TrackerService.HashId(account.AccountUuid.Replace("-", "")));
        }
        return hashes;
    }

    /// <summary>
    /// The full funnel of one auction
    /// </summary>
    public async Task<FlipFunnel> GetFunnel(Guid auction, bool includeOutsped)
    {
        var uuid = auction.ToString("N");
        var uid = TrackerService.HashId(uuid);
        var funnel = new FlipFunnel { AuctionUuid = uuid, AuctionUid = uid };
        var warnings = funnel.Warnings;
        // external calls first, they run while the db is queried (a DbContext can only run one query at a time)
        var auctionTask = Guarded(() => auctionsApi.ApiAuctionAuctionUuidGetAsync(uuid), "auction api", ExternalTimeout);
        var contextTask = Guarded(async () => (await storage.GetFinderContexts(auction)).ToList(), "finder contexts", ExternalTimeout);

        var flips = await db.Flips.AsNoTracking().Where(f => f.AuctionId == uid).ToListAsync();
        var events = await InvestigationQueries.EventsOfAuction(db, uid).ToListAsync();
        if (events.Count > InvestigationQueries.MaxEventsPerAuction)
        {
            events = events.Take(InvestigationQueries.MaxEventsPerAuction).ToList();
            funnel.Timeline.Truncated = true;
            warnings.Add($"more than {InvestigationQueries.MaxEventsPerAuction} events, only the earliest were read");
        }
        var (apiAuction, auctionWarning) = await auctionTask;
        Add(warnings, auctionWarning);
        var (contexts, contextWarning) = await contextTask;
        Add(warnings, contextWarning);

        var topBid = apiAuction?.Bids?.OrderByDescending(b => b.Amount).FirstOrDefault();
        var soldEvent = events.Where(e => e.Type == FlipEventType.AUCTION_SOLD).OrderBy(e => e.Timestamp).FirstOrDefault();
        long? buyerHash = soldEvent?.PlayerId;
        string buyerUuid = topBid?.Bidder;
        var source = "unknown";
        if (buyerUuid != null)
        {
            buyerHash ??= TrackerService.HashId(buyerUuid);
            source = "auction";
        }
        else if (buyerHash != null)
        {
            var (found, lookupWarning) = await Guarded(() => storage.LookupPlayerIds([buyerHash.Value]), "player id lookup", ExternalTimeout);
            Add(warnings, lookupWarning);
            if (found != null && found.TryGetValue(buyerHash.Value, out var guid))
            {
                buyerUuid = guid.ToString("N");
                source = "lookup";
            }
        }
        var buyerHashes = new HashSet<long>();
        if (buyerHash != null)
            buyerHashes.Add(buyerHash.Value);
        if (buyerUuid != null && PlayerOptOut.IsOptedOut(buyerUuid))
        {
            buyerUuid = null;
            source = "suppressed";
        }
        else if (buyerUuid != null)
        {
            foreach (var hash in await GetLinkedHashes(Guid.Parse(buyerUuid), warnings))
                buyerHashes.Add(hash);
        }
        funnel.BuyerLinkedAccounts = buyerHashes.ToList();

        bool? sold = apiAuction != null ? apiAuction.Bids?.Count > 0 : soldEvent != null ? true : null;
        var input = new FunnelInput
        {
            AuctionStart = apiAuction?.Start,
            AuctionSold = sold,
            SoldAtOverride = sold == true && apiAuction != null ? topBid?.Timestamp : null,
            Flips = flips,
            Contexts = contexts ?? [],
            Profit = EstimateProfit(flips, apiAuction)
        };
        var stats = EventStats.FromEvents(events, buyerHashes);
        var result = FunnelClassifier.Classify(input, stats);

        funnel.Tag = apiAuction?.Tag;
        funnel.StartingBid = apiAuction?.StartingBid;
        funnel.HighestBid = apiAuction?.HighestBidAmount;
        funnel.Findings = result.Findings;
        funnel.Timings = result.Timings;
        funnel.Verdict = result.Verdict;
        funnel.Explanation = result.Explanation;
        funnel.QualifiesForExempt = result.QualifiesForExempt;
        funnel.BuyerReceived = result.BuyerReceived;
        funnel.BuyerClicked = result.BuyerClicked;
        var timeline = funnel.Timeline;
        timeline.AuctionStart = apiAuction?.Start;
        timeline.AuctionEnd = apiAuction?.End;
        timeline.SoldAt = result.SoldAt;
        timeline.SoldToHash = buyerHash;
        timeline.SoldToUuid = buyerUuid;
        timeline.SoldToSource = source;
        timeline.FirstFound = result.FirstFound;
        timeline.FirstReceive = stats.FirstReceive;
        timeline.LastReceive = stats.LastReceive;
        timeline.ReceiveCount = stats.ReceiveCount;
        timeline.ClickCount = stats.ClickCount;
        timeline.PurchaseStartCount = stats.PurchaseStartCount;
        timeline.PurchaseConfirmCount = stats.PurchaseConfirmCount;
        timeline.ReceiversNearSold = stats.ReceiversNearSold;
        timeline.FirstClick = stats.FirstClick;
        timeline.FirstPurchaseStart = stats.FirstPurchaseStart;
        timeline.FirstPurchaseConfirm = stats.FirstPurchaseConfirm;
        timeline.ReceiverCount = result.ReceiverCount;
        timeline.Receivers = result.Receivers;

        if (includeOutsped)
        {
            var (outsped, outspedWarning) = await Guarded(async () => (await storage.GetOutspedFlips()).Where(o => o.TriggeredBy == auction).ToList(), "outsped flips", TimeSpan.FromSeconds(20));
            Add(warnings, outspedWarning);
            funnel.Outsped = outsped ?? [];
        }
        if (result.Verdict is FunnelVerdict.NotSoldYet or FunnelVerdict.Unknown)
        {
            var (unsold, unsoldWarning) = await Guarded(async () => ToInfo(await storage.GetUnsoldFlip(uid)), "unsold flip", ExternalTimeout);
            Add(warnings, unsoldWarning);
            funnel.UnsoldFlip = unsold;
        }
        return funnel;
    }

    private static UnsoldFlipInfo ToInfo(UnsoldFlip unsold)
    {
        if (unsold == null)
            return null;
        var flip = unsold.Flip;
        return new UnsoldFlipInfo
        {
            Finder = flip.Finder,
            TargetPrice = flip.TargetPrice,
            StartingBid = flip.Auction?.StartingBid ?? 0,
            DailyVolume = (int)flip.DailyVolume,
            AuctionStart = unsold.AuctionStart
        };
    }

    private static long EstimateProfit(IEnumerable<Flip> flips, Api.Client.Model.ColorSaveAuction auction)
    {
        var target = flips.Select(f => (long)f.TargetPrice).DefaultIfEmpty(0).Max();
        var cost = auction?.HighestBidAmount > 0 ? auction.HighestBidAmount : auction?.StartingBid ?? 0;
        return target == 0 ? 0 : target - cost;
    }

    /// <summary>
    /// Purchases of a player joined with the funnel result of each
    /// </summary>
    /// <param name="source">both, pastflips or events</param>
    public async Task<PlayerPurchaseReport> GetPurchases(Guid player, int days, int limit, string source)
    {
        var warnings = new List<string>();
        var now = DateTime.UtcNow;
        var since = now.AddDays(-days);
        var hashes = await GetLinkedHashes(player, warnings);
        var hashList = hashes.ToList();
        var entries = new Dictionary<long, PurchaseFunnelEntry>();

        if (source != "events")
        {
            var (flips, flipWarning) = await Guarded(async () => (await storage.GetFlips(player, since, now)).ToList(), "past flips", TimeSpan.FromSeconds(30));
            Add(warnings, flipWarning);
            // re-calculated flips don't have milliseconds in the timestamp, the newer flip has the lower timestamp
            foreach (var f in (flips ?? []).GroupBy(f => f.SellAuctionId == Guid.Empty ? f.PurchaseAuctionId : f.SellAuctionId).Select(g => g.OrderBy(f => f.SellTime).First()))
            {
                if (f.PurchaseAuctionId == Guid.Empty)
                    continue;
                var uid = TrackerService.HashId(f.PurchaseAuctionId.ToString("N"));
                entries[uid] = new PurchaseFunnelEntry
                {
                    AuctionUuid = f.PurchaseAuctionId.ToString("N"),
                    AuctionUid = uid,
                    ItemTag = f.ItemTag,
                    PurchaseTime = f.PurchaseTime,
                    Cost = f.PurchaseCost,
                    Profit = f.Profit,
                    Source = "pastflip",
                    Flags = FlagNames(f.Flags),
                    StoredFinder = f.FinderType.ToString(),
                    ViaTrade = f.Flags.HasFlag(FlipFlags.ViaTrade) || f.Flags.HasFlag(FlipFlags.MultiItemTrade)
                };
            }
        }
        if (source != "pastflips")
        {
            var sold = await InvestigationQueries.SoldEventsOfPlayers(db, hashList, since, 1000).ToListAsync();
            foreach (var e in sold)
            {
                if (entries.TryGetValue(e.AuctionId, out var existing))
                    existing.Source = "both";
                else
                    entries[e.AuctionId] = new PurchaseFunnelEntry { AuctionUid = e.AuctionId, PurchaseTime = e.Timestamp, Source = "event" };
            }
        }
        var selected = entries.Values.OrderByDescending(e => e.PurchaseTime).Take(limit).ToList();
        if (entries.Count > selected.Count)
            warnings.Add($"{entries.Count} purchases found, only the {limit} most recent are analysed");

        // trades were not bought on the auction house, they have no funnel
        var uids = selected.Where(e => !e.ViaTrade).Select(e => e.AuctionUid).ToList();
        var aggregates = new List<EventAggregate>();
        var ownEvents = new List<FlipEvent>();
        var soldRows = new List<FlipEvent>();
        var flipRows = new List<Flip>();
        var receiverChunks = new List<List<KeyValuePair<long, int>>>();
        foreach (var chunk in uids.Chunk(InvestigationQueries.ChunkSize))
        {
            var ids = chunk.ToList();
            aggregates.AddRange(await InvestigationQueries.EventAggregates(db, ids).ToListAsync());
            var sold = await InvestigationQueries.SoldEventsOfAuctions(db, ids).ToListAsync();
            soldRows.AddRange(sold);
            // events of the player's accounts plus the accounts that actually bought
            var buyerHashes = hashList.Concat(sold.Select(e => e.PlayerId)).Distinct().ToList();
            ownEvents.AddRange(await InvestigationQueries.PlayerEventsOnAuctions(db, buyerHashes, ids).ToListAsync());
            flipRows.AddRange(await InvestigationQueries.FlipsOfAuctions(db, ids).ToListAsync());
            receiverChunks.Add((await InvestigationQueries.ReceiverCounts(db, ids).ToListAsync()).Select(r => new KeyValuePair<long, int>(r.PlayerId, r.Count)).ToList());
        }
        var contexts = await LoadContexts(selected.Where(e => !e.ViaTrade).ToList(), warnings);

        var aggByAuction = aggregates.ToLookup(a => a.AuctionId);
        var ownByAuction = ownEvents.ToLookup(e => e.AuctionId);
        var flipsByAuction = flipRows.ToLookup(f => f.AuctionId);
        var soldByAuction = soldRows.GroupBy(e => e.AuctionId).ToDictionary(g => g.Key, g => g.OrderBy(e => e.Timestamp).First().PlayerId);
        foreach (var entry in selected)
        {
            long? soldTo = soldByAuction.TryGetValue(entry.AuctionUid, out var soldHash) ? soldHash : null;
            PurchaseReportBuilder.ClassifyEntry(entry, aggByAuction[entry.AuctionUid], ownByAuction[entry.AuctionUid], soldTo, hashes,
                flipsByAuction[entry.AuctionUid].ToList(), contexts.GetValueOrDefault(entry.AuctionUid) ?? []);
        }

        var report = PurchaseReportBuilder.Build(selected);
        report.PlayerUuid = player.ToString("N");
        report.Days = days;
        report.Hashes = hashList;
        var withReceive = aggByAuction.Count(g => g.Any(a => a.Type == FlipEventType.FLIP_RECEIVE));
        report.TopReceivers = PurchaseReportBuilder.MergeTopReceivers(receiverChunks, hashes, withReceive);
        await ResolveReceivers(report.TopReceivers, warnings);
        report.Warnings = warnings;
        return report;
    }

    private static List<string> FlagNames(FlipFlags flags)
        => Enum.GetValues<FlipFlags>().Where(f => f != FlipFlags.None && flags.HasFlag(f)).Select(f => f.ToString()).ToList();

    private async Task<Dictionary<long, List<FinderContext>>> LoadContexts(List<PurchaseFunnelEntry> entries, List<string> warnings)
    {
        var result = new Dictionary<long, List<FinderContext>>();
        var gate = new SemaphoreSlim(8);
        var failed = 0;
        await Task.WhenAll(entries.Where(e => e.AuctionUuid != null).Select(async entry =>
        {
            await gate.WaitAsync();
            try
            {
                var (list, warning) = await Guarded(async () => (await storage.GetFinderContexts(Guid.Parse(entry.AuctionUuid))).ToList(), "finder contexts", ExternalTimeout);
                if (warning != null)
                    Interlocked.Increment(ref failed);
                else
                    lock (result)
                        result[entry.AuctionUid] = list;
            }
            finally
            {
                gate.Release();
            }
        }));
        if (failed > 0)
            warnings.Add($"finder contexts could not be loaded for {failed} auctions, they may show as NotFound");
        return result;
    }

    private async Task ResolveReceivers(List<TopReceiver> receivers, List<string> warnings)
    {
        if (receivers.Count == 0)
            return;
        var (found, warning) = await Guarded(() => storage.LookupPlayerIds(receivers.Select(r => r.Hash)), "player id lookup", ExternalTimeout);
        Add(warnings, warning);
        foreach (var receiver in receivers)
        {
            if (found != null && found.TryGetValue(receiver.Hash, out var uuid) && !PlayerOptOut.IsOptedOut(uuid))
                receiver.Uuid = uuid;
        }
    }

    /// <summary>
    /// Event counts of a player's accounts per day and type
    /// </summary>
    public async Task<PlayerActivity> GetActivity(Guid player, int days)
    {
        var warnings = new List<string>();
        var since = DateTime.UtcNow.AddDays(-days);
        var hashes = (await GetLinkedHashes(player, warnings)).ToList();
        var rows = await InvestigationQueries.ActivityByDay(db, hashes, since).ToListAsync();
        var activity = new PlayerActivity { PlayerUuid = player.ToString("N"), Days = days, Hashes = hashes, Warnings = warnings };
        foreach (var group in rows.GroupBy(r => r.Type))
            activity.LastEventAt[group.Key.ToString()] = group.Max(r => r.Last);
        activity.Daily = rows.GroupBy(r => r.Day).OrderBy(g => g.Key)
            .Select(g => new DailyActivity { Day = g.Key, Counts = g.ToDictionary(r => r.Type.ToString(), r => r.Count) }).ToList();
        var recent = DateTime.UtcNow.AddDays(-3);
        activity.LooksActive = rows.Any(r => r.Last >= recent && r.Type is FlipEventType.FLIP_RECEIVE or FlipEventType.FLIP_CLICK
            or FlipEventType.PURCHASE_START or FlipEventType.PURCHASE_CONFIRM or FlipEventType.AUCTION_SOLD);
        return activity;
    }
}
