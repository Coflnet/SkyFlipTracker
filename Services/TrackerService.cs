using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Api.Client.Api;
using Coflnet.Sky.Core;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using AutoMapper;
using Newtonsoft.Json;
using Prometheus;
using System.Globalization;
using Coflnet.Leaderboard.Client.Api;
using Coflnet.Sky.Settings.Client.Api;
using Coflnet.Sky.PlayerState.Client.Model;
using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Sky.SkyAuctionTracker.Services
{
    public class TrackerService
    {
        private readonly TrackerDbContext db;
        private readonly ILogger<TrackerService> logger;
        private readonly IAuctionsApi auctionsApi;
        private readonly FlipSumaryEventProducer flipSumaryEventProducer;
        private readonly IServiceScopeFactory scopeFactory;
        private readonly ProfitChangeService profitChangeService;
        private readonly FlipStorageService flipStorageService;
        private readonly IScoresApi scoresApi;
        private readonly ISettingsApi settingsApi;
        private readonly IPlayerApi playerApi;
        private readonly ActivitySource activitySource;
        private readonly PlayerState.Client.Api.IItemsApi itemsApi;
        private readonly PlayerState.Client.Api.ITransactionApi transactionApi;
        private readonly RepresentationConverter representationConverter;
        private const short Version = 1;
        private const int COIN_ID = 1_000_001;
        readonly Counter flipFoundCounter = Metrics.CreateCounter("sky_fliptracker_saved_finding", "How many found flips were saved");
        readonly Counter userFlipCounter = Metrics.CreateCounter("sky_fliptracker_user_flip", "How many flips were done by a user");

        private ConcurrentQueue<long> flipIds = new();

        public TrackerService(
            TrackerDbContext db,
            ILogger<TrackerService> logger,
            IAuctionsApi api,
            FlipSumaryEventProducer flipSumaryEventProducer,
            IServiceScopeFactory scopeFactory,
            ProfitChangeService profitChangeService,
            FlipStorageService flipStorageService,
            ActivitySource activitySource,
            IPlayerApi playerApi,
            IScoresApi scoresApi,
            ISettingsApi settingsApi,
            PlayerState.Client.Api.IItemsApi itemsApi,
            PlayerState.Client.Api.ITransactionApi transactionApi,
            RepresentationConverter representationConverter)
        {
            this.db = db;
            this.logger = logger;
            this.auctionsApi = api;
            this.flipSumaryEventProducer = flipSumaryEventProducer;
            this.scopeFactory = scopeFactory;
            this.profitChangeService = profitChangeService;
            this.flipStorageService = flipStorageService;
            this.activitySource = activitySource;
            this.playerApi = playerApi;
            this.scoresApi = scoresApi;
            this.settingsApi = settingsApi;
            this.itemsApi = itemsApi;
            this.transactionApi = transactionApi;
            this.representationConverter = representationConverter;
        }

        /// <summary>
        /// Store a flip
        /// </summary>
        /// <param name="flip">flip to store</param>
        /// <returns></returns>
        public async Task<Flip> AddFlip(Flip flip)
        {
            if (flip.Timestamp < new DateTime(2020, 1, 1))
            {
                flip.Timestamp = DateTime.Now;
            }
            var flipAlreadyExists = await db.Flips.Where(f => f.AuctionId == flip.AuctionId && f.FinderType == flip.FinderType).AnyAsync();
            if (flipAlreadyExists)
            {
                return flip;
            }
            if (flip.FinderType == LowPricedAuction.FinderType.TFM)
            {
                logger.LogInformation($"TFM flip: {flip.AuctionId} {flip.Timestamp.Second}.{flip.Timestamp.Millisecond} \t{DateTime.Now}.{DateTime.Now.Millisecond}");
            }
            db.Flips.Add(flip);
            await db.SaveChangesAsync();
            return flip;
        }

        /// <summary>
        /// Adds multiple flips to the database.
        /// </summary>
        /// <param name="flipsToSave"></param>
        /// <returns></returns>
        public async Task AddFlips(IEnumerable<Flip> flipsToSave)
        {
            DateTime minTime = new DateTime(2020, 1, 1);
            await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    var flips = flipsToSave.ToList();
                    var lookup = flips.Select(f => f.AuctionId).ToHashSet();
                    var existing = await db.Flips.Where(f => lookup.Contains(f.AuctionId)).ToListAsync();
                    var newFlips = flips.Where(f => !existing.Where(ex => f.AuctionId == f.AuctionId && ex.FinderType == f.FinderType).Any()).ToList();
                    foreach (var item in newFlips)
                    {
                        if (item.Timestamp < minTime)
                            item.Timestamp = DateTime.UtcNow;
                    }
                    db.Flips.AddRange(newFlips);
                    var count = await db.SaveChangesAsync();
                    await db.Database.CommitTransactionAsync();
                    flipFoundCounter.Inc(count);
                    break;
                }
                catch (Exception e)
                {
                    logger.LogError(e, "saving flips");
                    await Task.Delay(500);
                }
            }
        }

        public async Task<FlipEvent> AddEvent(FlipEvent flipEvent)
        {
            return (await AddEvents(new FlipEvent[] { flipEvent })).First();
        }

        public async Task<List<FlipEvent>> AddEvents(IEnumerable<FlipEvent> flipEvents)
        {
            foreach (var flipEvent in flipEvents)
            {
                if (flipEvent.Timestamp == default)
                {
                    flipEvent.Timestamp = DateTime.UtcNow;
                }
            }
            var affectedAuctions = flipEvents.Select(f => f.AuctionId).ToHashSet();
            var affectedPlayers = flipEvents.Select(f => f.PlayerId).ToHashSet();
            var existing = await db.FlipEvents.Where(f => affectedAuctions.Contains(f.AuctionId) && affectedPlayers.Contains(f.PlayerId))
                .ToListAsync();
            var result = new List<FlipEvent>();
            foreach (var flipEvent in flipEvents)
            {
                var eventAlreadyExists = existing.Where(f => f.AuctionId == flipEvent.AuctionId && f.Type == flipEvent.Type && f.PlayerId == flipEvent.PlayerId).FirstOrDefault();
                if (eventAlreadyExists != null)
                {
                    result.Add(eventAlreadyExists);
                    continue;
                }
                db.FlipEvents.Add(flipEvent);

            }
            await db.SaveChangesAsync();
            return result;
        }

        internal async Task AddSells(IEnumerable<SaveAuction> sells)
        {
            var lookup = sells.Select(s => s.UId).ToHashSet();
            var existing = await db.FlipEvents.Where(e => lookup.Contains(e.AuctionId) && e.Type == FlipEventType.AUCTION_SOLD).Select(e => e.AuctionId).ToListAsync();
            var found = await db.Flips.Where(e => lookup.Contains(e.AuctionId)).Select(e => e.AuctionId).ToListAsync();
            foreach (var item in sells)
            {
                if (!item.Bin || item.Bids.Count == 0)
                    continue;
                if (!found.Contains(item.UId))
                    continue;
                if (!existing.Contains(item.UId))
                    db.FlipEvents.Add(new FlipEvent()
                    {
                        AuctionId = item.UId,
                        PlayerId = GetId(item.Bids.MaxBy(b => b.Amount).Bidder),
                        Timestamp = item.Bids.MaxBy(b => b.Amount).Timestamp,
                        Type = FlipEventType.AUCTION_SOLD
                    });
            }
            logger.LogInformation($"saving sells {sells.Count()}");
            var count = await db.SaveChangesAsync();
            if (count > 0)
                Console.WriteLine($"Saved sells {count}");
            await flipStorageService.DeleteActiveBasedOnStartTime(sells.Where(s => s.Bin && s.Bids.Count > 0).Select(s => s.Start));
        }

        public async Task PutBuySpeedOnBoard(IEnumerable<SaveAuction> sells)
        {
            var relevantSells = sells.Where(s => s.Bin && s.Bids.Count > 0).ToList();
            var relevantLookup = relevantSells.Select(s => s.UId).ToHashSet();
            var creationTimes = await db.FlipEvents
                                .Where(e => relevantLookup.Contains(e.AuctionId) && e.Type == FlipEventType.START)
                                .GroupBy(e => e.AuctionId).Select(g => g.First())
                                .ToDictionaryAsync(e => e.AuctionId, e => e.Timestamp);
            foreach (var item in relevantSells)
            {
                if (!creationTimes.TryGetValue(item.UId, out var creationTime))
                    continue;
                var timeToBuy = item.End - creationTime - TimeSpan.FromSeconds(16);
                if (timeToBuy > TimeSpan.FromSeconds(10))
                    continue;
                var playerUuid = item.Bids.First().Bidder;
                var disabled = await settingsApi.SettingsGetSettingAsync(playerUuid, "disable-buy-speed-board");
                if (!string.IsNullOrEmpty(disabled))
                {
                    logger.LogInformation($"user {playerUuid} disabled buy speed board: {disabled}");
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var leaderboardSlug = $"sky-buyspeed-{DateTime.UtcNow.RoundDown(TimeSpan.FromDays(7)):yyyy-MM-dd}";
                        await scoresApi.ScoresLeaderboardSlugPostAsync(leaderboardSlug, new Leaderboard.Client.Model.ScoreCreate()
                        {
                            UserId = playerUuid,
                            Score = (long)(timeToBuy.TotalSeconds * -1000),
                            HighScore = true,
                            DaysToKeep = 20
                        });
                        if (timeToBuy < TimeSpan.FromSeconds(5))
                        {
                            logger.LogInformation($"user {playerUuid} bought {item.Tag} in {timeToBuy.TotalSeconds} seconds posted toboard {leaderboardSlug}");
                        }
                    }
                    catch (System.Exception e)
                    {
                        logger.LogError(e, $"Could not post buy speed {playerUuid} {timeToBuy.TotalSeconds}");
                    }
                });
            }
        }

        /// <summary>
        /// Refreshes flips for given auctions
        /// </summary>
        /// <param name="playerId"></param>
        /// <param name="auctionIds"></param>
        /// <param name="version"></param>
        /// <returns></returns>
        public async Task RefreshFlips(Guid playerId, IEnumerable<Guid> auctionIds, int version)
        {
            var existing = await flipStorageService.GetFlipVersions(playerId, new DateTime(2020, 1, 1), DateTime.Now, auctionIds);
            if (version == 0)
            {
                version = Version;
            }
            var toRefresh = auctionIds.Except(existing.Where(e => e.Item2 < version).Select(e => e.Item1)).ToList();
            var mapper = new AutoMapper.Mapper(new MapperConfiguration(cfg =>
            {
                cfg.CreateMap<Api.Client.Model.ColorSaveAuction, SaveAuction>().ForMember(dest => dest.FlatenedNBT, opt => opt.MapFrom(src => src.FlatNbt));
                cfg.CreateMap<Api.Client.Model.ColorEnchant, Enchantment>();
                cfg.CreateMap<Api.Client.Model.SaveBids, SaveBids>();
                cfg.AddGlobalIgnore("NbtData");
            }));

            var auctions = await Task.WhenAll(toRefresh.Select(async a =>
            {
                var originalResp = await auctionsApi.ApiAuctionAuctionUuidGetWithHttpInfoAsync(a.ToString("N"));
                return JsonConvert.DeserializeObject<ApiSaveAuction>(originalResp.RawContent);
                /*if (original == null)
                    throw new Exception($"auction {a.ToString("N")} could not be loaded");

                var mapped = mapper.Map<SaveAuction>(original);
                if (mapped == null)
                    throw new Exception($"auction {JsonConvert.SerializeObject(original)} could not be mapped");
                return mapped;*/
            }));
            logger.LogInformation("Refreshing flips {auctions}", JsonConvert.SerializeObject(auctions));
            await IndexCassandra(auctions, true);
        }

        public virtual async Task IndexCassandra(IEnumerable<SaveAuction> sells, bool extraLog = false)
        {
            var count = sells.Count();
            if (count == 0)
                return;
            using var activity = activitySource.StartActivity("IndexCassandra", ActivityKind.Server);
            try
            {
                await CalculateAndIndex(sells.ToList(), extraLog);
                return;
            }
            catch (System.Exception error)
            {
                if (error.Message.Contains("with the same key has already been added."))
                {
                    foreach (var item in sells.ToList())
                    {
                        await CalculateAndIndex([item], extraLog);
                    }
                    logger.LogInformation($"saved sells {count} one by one because dupplicate");
                    return;
                }
                if (count < 4)
                    dev.Logger.Instance.Error(error, $"cassandra index failed batch size {count}");

                await Task.Delay(200);
                if (count > 1)
                {
                    await IndexCassandra(sells.Take(count / 2));
                    await IndexCassandra(sells.Skip(count / 2));
                }
                else
                    throw new CoflnetException("load error", "This sell caused error: " + JsonConvert.SerializeObject(sells.FirstOrDefault()));
            }
        }

        private async Task CalculateAndIndex(List<SaveAuction> sells, bool extraLog = false)
        {
            var sellLookup = sells.Where(s => s.FlatenedNBT.Where(n => n.Key == "uid").Any() && s.HighestBidAmount > 0)
                                .GroupBy(s => new { uid = s.FlatenedNBT.Where(n => n.Key == "uid").First(), s.End }).Select(g => g.First())
                                .ToDictionary(s => s.FlatenedNBT.Where(n => n.Key == "uid").Select(n => n.Value).FirstOrDefault());
            var tradeLookupTask = transactionApi.TransactionUuidItemIdPostAsync(GetItemUuids(sells));
            var buyLookup = await auctionsApi.ApiAuctionsUidsSoldPostWithHttpInfoAsync(new Api.Client.Model.InventoryBatchLookup() { Uuids = sellLookup.Keys.ToList() });
            var tradeUuidLookup = await tradeLookupTask;
            if (buyLookup.StatusCode != System.Net.HttpStatusCode.OK)
                throw new Exception("Could not reach api to load purchases " + buyLookup.StatusCode);
            var exists = buyLookup.Data;
            if (tradeUuidLookup.Count > 0)
                logger.LogInformation($"Found {tradeUuidLookup.Count} trade items");
            if (exists.Count == 0)
            {
                logger.LogInformation($"no purchases found {sells.Count()}");
                return;
            }
            if (extraLog)
            {
                logger.LogInformation($"Buy lookup {JsonConvert.SerializeObject(exists)}");
                logger.LogInformation($"Sell lookup {JsonConvert.SerializeObject(sellLookup)}");
            }
            var soldAuctions = exists.Select(item => new
            {
                sell = sellLookup.GetValueOrDefault(item.Key),
                buy = item.Value.Where(v => v.Uuid != sellLookup.GetValueOrDefault(item.Key)?.Uuid
                                        && (v.Timestamp < sellLookup.GetValueOrDefault(item.Key)?.End
                                        || v.Timestamp > DateTime.UtcNow))
                                    .OrderByDescending(u => u.Timestamp).FirstOrDefault()
            }).Where(item => item.buy != null).ToList();
            if (extraLog)
                logger.LogInformation($"Found {soldAuctions.Count} sold auctions {JsonConvert.SerializeObject(soldAuctions)}");
            var purchaseUid = soldAuctions.Select(u => GetId(u.buy.Uuid)).ToHashSet();
            foreach (var tradeSource in tradeUuidLookup)
            {
                var uid = tradeSource.Key.Split("-").Last();
                if (exists.TryGetValue(uid, out var existing))
                    continue; // know buy properties
                var sell = sellLookup.GetValueOrDefault(uid) ?? throw new Exception($"Could not find sell for trade item {uid} {tradeSource.Key}");
                soldAuctions.Add(new
                {
                    sell = sell,
                    buy = new Api.Client.Model.ItemSell() { Buyer = null, Uuid = tradeSource.Value.OrderByDescending(t => t).First().ToString() }
                });
            }

            List<Flip> finders = new();
            if (scopeFactory != null)
            {
                using (var scope = scopeFactory.CreateScope())
                using (var dbScoped = scope.ServiceProvider.GetRequiredService<TrackerDbContext>())
                {
                    finders = await dbScoped.Flips.Where(f => purchaseUid.Contains(f.AuctionId)).ToListAsync();
                }
            }
            var parallelOptions = new ParallelOptions()
            {
                MaxDegreeOfParallelism = 3,
                CancellationToken = new CancellationTokenSource(20000).Token
            };
            var noUidTask = CheckNoIdAuctions(sells, parallelOptions);
            await Parallel.ForEachAsync(soldAuctions, parallelOptions, async (item, token) =>
            {
                if (flipIds.Contains(item.sell.UId))
                {
                    logger.LogInformation($"Already stored {item.sell.Uuid}");
                    return;
                }
                var buy = await GetAuction(item.buy.Uuid, item.sell, token).ConfigureAwait(false);
                try
                {
                    var sell = item.sell;
                    var first = sells.FirstOrDefault();
                    if (first == null)
                    {
                        logger.LogWarning("No sell found in {item}", JsonConvert.SerializeObject(item));
                        return;
                    }
                    if (!sells.All(s => s.AuctioneerId == first.AuctioneerId) && (await flipStorageService.GetFlips(Guid.Parse(sell.AuctioneerId), sell.End, sell.End)).Any())
                        return; // no refresh request and already stored, skip calculation
                    if (buy.AuctioneerId == null)
                        logger.LogInformation($"trade check {item.buy.ItemTag}");
                    (FlipFlags flags, var change) = await CheckTrade(buy, sell);
                    var purchaseId = GetId(buy.Uuid);
                    var flipFound = finders.Where(f => f != null && f.AuctionId == purchaseId).OrderBy(f => f.Timestamp).FirstOrDefault();
                    flipSumaryEventProducer?.Produce(new FlipSumaryEvent()
                    {
                        Flipper = item.sell.AuctioneerId,
                        Buy = buy,
                        Sell = item.sell,
                        Finder = flipFound,
                        Profit = (int)(item.sell.HighestBidAmount - buy?.HighestBidAmount ?? 0),
                    });
                    if (buy.AuctioneerId == null)
                        logger.LogInformation($"trade produced  {item.buy.ItemTag}");
                    List<PastFlip.ProfitChange> changes = new();
                    try
                    {
                        changes = await profitChangeService.GetChanges(buy, sell).ConfigureAwait(false);
                        if (buy.AuctioneerId == null)
                            logger.LogInformation($"trade changes retrieved  {item.buy.ItemTag}");
                        if (buy.End > sell.End - TimeSpan.FromDays(14))
                            await AddListingAttempts(sell, changes);
                        if (change != null)
                            changes.Insert(0, change);
                    }
                    catch (System.Exception e)
                    {
                        logger.LogError(e, "Could not load profit changes");
                        throw;
                    }
                    var profit = (long)(item.sell.HighestBidAmount - buy?.HighestBidAmount ?? 0) + changes.Sum(c => c.Amount);
                    if (sell.End - buy.End > TimeSpan.FromDays(14))
                        profit = 0; // no flip if it took more than 2 weeks
                    var name = GetDisplayName(buy, sell);
                    if (buy.AuctioneerId == null)
                        logger.LogInformation($"trade name determined {item.buy.ItemTag}");
                    if (item.sell.UId == 0)
                        item.sell.UId = AuctionService.Instance.GetId(item.sell.Uuid);
                    var flip = new PastFlip()
                    {
                        Flipper = Guid.Parse(sell.AuctioneerId),
                        ItemName = name,
                        ItemTag = sell.Tag,
                        ItemTier = sell.Tier,
                        Profit = profit,
                        SellPrice = item.sell.HighestBidAmount,
                        SellTime = item.sell.End,
                        PurchaseCost = buy.HighestBidAmount,
                        PurchaseTime = buy.End,
                        Uid = item.sell.UId,
                        PurchaseAuctionId = Guid.Parse(buy.Uuid),
                        SellAuctionId = Guid.Parse(sell.Uuid),
                        Version = 1,
                        TargetPrice = flipFound?.TargetPrice ?? 0,
                        FinderType = flipFound?.FinderType ?? LowPricedAuction.FinderType.UNKOWN,
                        ProfitChanges = changes,
                        Flags = flags
                    };
                    await flipStorageService.SaveFlip(flip);
                    userFlipCounter.Inc();
                    if (extraLog)
                        logger.LogInformation($"Saved flip {flip.Uid} {JsonConvert.SerializeObject(flip)}");

                    if (flipFound == default && changes.Count <= 1 && profit > 1_500_000 && buy.End > DateTime.UtcNow - TimeSpan.FromDays(1))
                    {
                        logger.LogInformation($"Flip {flip.PurchaseAuctionId:n} not found for {flip.Profit}");
                        await MissedFlip(flip, "Flip not found at all", buy);
                    }
                    if (flipFound != default && changes.Count <= 1 && profit > 600_000 && buy.End > DateTime.UtcNow - TimeSpan.FromDays(1))
                    {
                        await LogFoundFlips(buy, flip);
                    }
                    flipIds.Enqueue(sell.UId);
                    if (flipIds.Count > 100)
                        flipIds.TryDequeue(out _);
                }
                catch (Exception e)
                {
                    logger.LogError(e, $"Failed to save flip {item.buy.Uuid} -> {item.sell.Uuid} {JsonConvert.SerializeObject(item.sell)}\n{JsonConvert.SerializeObject(buy)}");
                    throw;
                }
            });
            await noUidTask;
        }

        private async Task MissedFlip(PastFlip flip, string v, SaveAuction buy)
        {
            using var scope = scopeFactory.CreateScope();
            var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var webhook = configuration["UNFOUND_FLIP_WEBHOOK"];
            if (webhook == null)
                return;
            var client = new System.Net.Http.HttpClient();
            var profitPercent = flip.Profit * 100 / (flip.PurchaseCost == 0 ? int.MaxValue : flip.PurchaseCost);
            var text = $"Flipped for {flip.Profit:N0} coins (`{profitPercent}%`) within {flip.SellTime - flip.PurchaseTime}";
            if (flip.SellTime - flip.PurchaseTime < TimeSpan.FromMinutes(15) && (flip.Profit > 100_000_000 || profitPercent > 10_000) && buy.FlatenedNBT.Any(n => Constants.AttributeKeys.Contains(n.Key)))
                return;
            if (buy.StartingBid == 0)
                text += $"\nPageflipped";
            if (!buy.Bin)
                text += $"\n**Auction**";

            var body = JsonConvert.SerializeObject(new
            {
                embeds = new[] { new {
                    description = text,
                    url = $"https://sky.coflnet.com/auction/{flip.PurchaseAuctionId:n}",
                    title = v,
                    footer = new { text = "SkyCofl", icon_url = "https://sky.coflnet.com/logo192.png" },
                    thumbnail = new { url = $"https://sky.coflnet.com/static/icon/{flip.ItemTag}" },
                    avatar_url = "https://sky.coflnet.com/logo192.png",
                    } }
            });
            await client.PostAsync(webhook, new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            await flipStorageService.SaveUnknownFlip(flip);
        }

        private async Task LogFoundFlips(ApiSaveAuction buy, PastFlip flip)
        {
            using var scope = scopeFactory.CreateScope();
            using var dbScoped = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            var mcService = scope.ServiceProvider.GetRequiredService<McConnect.Api.IConnectApi>();
            var buyerUuid = buy.Bids.OrderByDescending(b => b.Amount).First().Bidder;
            var playerId = GetId(buyerUuid);
            var purchaseUid = GetId(buy.Uuid);
            var userTask = mcService.ConnectMinecraftMcUuidGetAsync(buyerUuid); ;
            var sendEvents = await dbScoped.FlipEvents.Where(f => purchaseUid == f.AuctionId).ToListAsync();
            if (sendEvents.Count <= 1)
            {
                logger.LogInformation($"Flip {flip.PurchaseAuctionId:n} ({buy.UId}) found for {flip.Profit} not sent to anybody");
                await MissedFlip(flip, "Flip not sent to anybody (blocked)", buy);
                return;
            }
            var user = await userTask;
            var userIds = user?.Accounts?.Select(a => GetId(a.AccountUuid)).ToHashSet() ?? [];
            var sentToPurchaser = sendEvents.Where(e => e.Type == FlipEventType.FLIP_RECEIVE && userIds.Contains(e.PlayerId)).Any();
            var boughtAt = sendEvents.Where(e => e.Type == FlipEventType.AUCTION_SOLD).FirstOrDefault();
            var firstSend = sendEvents.Where(e => e.Type == FlipEventType.FLIP_RECEIVE).OrderBy(e => e.Timestamp).FirstOrDefault();
            var diff = boughtAt?.Timestamp - firstSend?.Timestamp;
            logger.LogInformation($"Flip {flip.PurchaseAuctionId:n} found for {flip.Profit} by us {sentToPurchaser} bought {boughtAt?.Timestamp} {sendEvents.Count} diff {diff}");
            if (!sentToPurchaser && diff > TimeSpan.FromSeconds(-4) && (diff < TimeSpan.FromSeconds(-3.2) || flip.Profit > 50_000_000 && diff < TimeSpan.FromSeconds(-0.1)))
            {
                // todo store
                var all = await flipStorageService.GetFinderContexts(flip.PurchaseAuctionId);
                var medianSniperFinder = all.Where(f => f.Finder == LowPricedAuction.FinderType.SNIPER_MEDIAN).FirstOrDefault();
                if (medianSniperFinder == null)
                {
                    logger.LogInformation($"Not found context {flip.PurchaseAuctionId:n} ({buy.UId}) found for {flip.Profit} not sent to us");
                    return;
                }
                var key = medianSniperFinder.Context.GetValueOrDefault("key");
                if (key == null)
                {
                    logger.LogInformation($"Not found key {flip.PurchaseAuctionId:n} ({buy.UId}) found for {flip.Profit} not sent to us");
                    return;
                }
                await flipStorageService.SaveOutspedFlip(buy.Tag, key, flip.PurchaseAuctionId);
                logger.LogInformation($"Flip {flip.PurchaseAuctionId:n} ({buy.UId}) found for {flip.Profit} noew excempt");
                await MissedFlip(flip, "Excempted now", buy);
            }


        }

        private static List<Guid> GetItemUuids(IEnumerable<SaveAuction> sells)
        {
            return sells.Select(s => s.FlatenedNBT.Where(n => n.Key == "uuid").FirstOrDefault().Value)
                .Where(v => v != default).Select(Guid.Parse).ToHashSet().ToList();
        }

        private async Task AddListingAttempts(SaveAuction sell, List<PastFlip.ProfitChange> changes)
        {
            try
            {
                var uidKv = sell.FlatenedNBT.FirstOrDefault(n => n.Key == "uid");
                var uid = uidKv.Value;
                if (string.IsNullOrEmpty(uid))
                {
                    // nothing we can query
                    return;
                }
                if (playerApi == null)
                {
                    logger.LogWarning("playerApi is not available, skipping listing attempts lookup");
                    return;
                }

                object listings;
                try
                {
                    // We don't have a stable compile-time type for the playerApi return value here
                    // (it comes from an external client package). Load it as object and use
                    // reflection below to read the properties we need. This avoids compile
                    // errors when the model type differs between packages/versions.
                    listings = await playerApi.ApiPlayerPlayerUuidAuctionsGetAsync(sell.AuctioneerId, 0, new Dictionary<string, string>() { { "UId", uid }, { "HighestBid", "0" } });
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Could not load listings for {auctioneer} uid {uid}", sell.AuctioneerId, uid);
                    return;
                }
                if (listings == null)
                    return;

                // listingObj is some enumerable (likely List<T>). Try to enumerate it safely.
                if (listings is System.Collections.IEnumerable enumListings)
                {
                    foreach (var listingObj in enumListings)
                    {
                        if (listingObj == null)
                            continue;

                        try
                        {
                            var t = listingObj.GetType();

                            // Auction id can be named AuctionId or Uuid depending on model
                            var auctionIdProp = t.GetProperty("AuctionId") ?? t.GetProperty("Uuid") ?? t.GetProperty("Uuid");
                            var auctionIdVal = auctionIdProp?.GetValue(listingObj)?.ToString();
                            if (auctionIdVal == null)
                                continue;
                            if (auctionIdVal == sell.Uuid)
                                continue;

                            // Highest bid amount: try common property names
                            var highestProp = t.GetProperty("HighestBidAmount") ?? t.GetProperty("HighestBid") ?? t.GetProperty("HighestBidAmount");
                            var startingProp = t.GetProperty("StartingBid") ?? t.GetProperty("StartingBid");

                            long highest = 0;
                            long starting = 0;

                            if (highestProp != null && highestProp.GetValue(listingObj) != null)
                                highest = Convert.ToInt64(highestProp.GetValue(listingObj));
                            if (startingProp != null && startingProp.GetValue(listingObj) != null)
                                starting = Convert.ToInt64(startingProp.GetValue(listingObj));

                            var change = profitChangeService.GetAhTax(highest, starting);
                            change.ContextItemId = AuctionService.Instance.GetId(auctionIdVal);
                            change.Label = $"Listing attempt {starting}";
                            changes.Add(change);

                            // Try to read HighestBid for logging as well
                            var highestBidProp = t.GetProperty("HighestBid");
                            var highestBidVal = highestBidProp != null ? highestBidProp.GetValue(listingObj)?.ToString() : null;
                            Console.WriteLine($"Found listing attempt {starting} {highestBidVal ?? highest.ToString()} {auctionIdVal} for {sell.Uuid}");
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Could not parse listing object for {sell}", sell?.Uuid);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error while checking listing attempts for {sell}", sell?.Uuid);
            }
        }

        private async Task<(FlipFlags, PastFlip.ProfitChange)> CheckTrade(ApiSaveAuction buy, SaveAuction sell)
        {
            var flags = FlipFlags.None;
            var itemUuid = sell.FlatenedNBT.Where(n => n.Key == "uuid").FirstOrDefault().Value;
            if (buy.Bids == null)
            {
                return (FlipFlags.ViaTrade, new PastFlip.ProfitChange($"Item was bought via trade for {buy.HighestBidAmount} coins", -1));
            }
            if (buy.Bids.OrderByDescending(b => b.Amount).First().Bidder == sell.AuctioneerId || itemUuid == default)
            {
                return (flags, null);
            }
            if (sell.Uuid == Guid.Empty.ToString("N"))
                flags |= FlipFlags.ViaTrade;
            else
                flags |= FlipFlags.DifferentBuyer;

            // check trade
            var items = await itemsApi.ApiItemsFindUuidPostAsync(new(){
                            new (){
                                Tag = sell.Tag,
                                Uuid = Guid.Parse(sell.FlatenedNBT.Where(n => n.Key == "uuid").FirstOrDefault().Value ?? Guid.Empty.ToString("N"))
                            }
                        });
            if (items.Count <= 0)
            {
                return (flags, null);
            }
            // TODO: maybe make sure to use best match to sell modifiers
            var itemInfo = items.Where(f => !f.ItemName.StartsWith("§f§f")).Take(2); // probably auction listings create new wrong item ids
            var itemTrade = new List<Transaction>();
            PlayerState.Client.Model.Item itemStateAtTrade = null;
            foreach (var trade in itemInfo)
            {
                itemTrade = await transactionApi.TransactionItemItemIdGetAsync(trade.Id ?? throw new Exception("no item id"), 0);
                if (itemTrade.Count > 0)
                {
                    itemStateAtTrade = trade;
                    break;
                }
            }
            if (itemTrade.Count > 0)
            {
                (int itemCount, long tradeEstimate, _) = await GetTradeValue(itemTrade);
                flags |= FlipFlags.ViaTrade;
                buy.HighestBidAmount = tradeEstimate;
                // adjust buy state to match traded attributes
                representationConverter.TryUpdatingBuyState(buy, itemStateAtTrade, itemTrade);
                if (itemCount > 1)
                {
                    flags |= FlipFlags.MultiItemTrade;
                    return (flags, new PastFlip.ProfitChange($"Item was traded with other items for about {tradeEstimate} coins", -1));
                }
                return (flags, new PastFlip.ProfitChange($"Item was bought by trade for {tradeEstimate} coins", -1));
            }

            return (flags, null);
        }



        private async Task<(int itemCount, long tradeEstimate, List<Transaction> items)> GetTradeValue(List<Transaction> itemTrade)
        {
            var time = itemTrade.First().TimeStamp;
            var tradeitems = await transactionApi.TransactionPlayerPlayerUuidGetAsync(itemTrade.First().PlayerUuid, 1, time);
            var coins = tradeitems.Where(t => t.ItemId == COIN_ID).Sum(t => t.Amount);
            var itemCount = tradeitems.Where(t => t.ItemId != COIN_ID).Count();
            // overwrite buy cost
            var tradeEstimate = coins / 10 / itemCount;
            foreach (var tradePosition in tradeitems)
            {
                Console.WriteLine($"trade: {tradePosition.PlayerUuid} {tradePosition.TimeStamp} {tradePosition.ItemId} {tradePosition.Amount}");
            }
            return (itemCount, tradeEstimate, tradeitems);
        }

        private async Task CheckNoIdAuctions(IEnumerable<SaveAuction> sells, ParallelOptions parallelOptions)
        {

            var noUidCheck = sells.Where(s => !s.FlatenedNBT.Where(n => n.Key == "uid").Any() && s.HighestBidAmount > 0)
                                .Where(s => s.End > DateTime.UtcNow - TimeSpan.FromDays(1)) // old not important
                                .GroupBy(s => new { s.AuctioneerId, s.Tag });
            await Parallel.ForEachAsync(noUidCheck, async (item, token) =>
            {
                var logMore = item.First().Tag == "PINK_DONUT_PERSONALITY";
                var query = new Dictionary<string, string>() {
                    { "tag", item.Key.Tag },
                    { "EndAfter", (item.First().End - TimeSpan.FromDays(14)).ToUnix().ToString() } };
                var purchases = await playerApi.ApiPlayerPlayerUuidBidsGetAsync(item.Key.AuctioneerId, 0, query);
                if (logMore)
                    logger.LogInformation($"Found {purchases.Count} purchases for {item.Key.AuctioneerId} {item.Key.Tag}");
                Api.Client.Model.BidResult previousAuction = null;
                foreach (var purchase in purchases.OrderByDescending(p => p.End).Where(p => p.End < item.First().End))
                {
                    var buyResp = await GetAuction(purchase.AuctionId, null, token).ConfigureAwait(false);
                    var match = buyResp.FlatenedNBT.FirstOrDefault(n => item.Any(i => i.FlatenedNBT.Any(f => f.Key == n.Key && f.Value == n.Value)));
                    if (logMore)
                        logger.LogInformation($"Found match {match.Key} {match.Value}");
                    // some items don't have any nbt data
                    if (match.Key == null && buyResp.FlatenedNBT.Count > 0)
                        continue;
                    var sell = item.Where(i => i.FlatenedNBT.Any(f => f.Key == match.Key && f.Value == match.Value)).FirstOrDefault();
                    if (sell == null && buyResp.FlatenedNBT.Count > 0)
                        continue;
                    sell = item.First();
                    var buyPrice = buyResp.HighestBidAmount;
                    var tax = profitChangeService.GetAhTax(sell.HighestBidAmount, sell.StartingBid);
                    var changes = new List<PastFlip.ProfitChange>() { tax };
                    if (sell.Count < buyResp.Count)
                    {
                        buyPrice = buyPrice * sell.Count / buyResp.Count;
                        var reduction = buyResp.HighestBidAmount - buyPrice;
                        changes.Add(new PastFlip.ProfitChange($"Stacked item sold partially", -reduction));
                    }
                    var profit = sell.HighestBidAmount - buyPrice;
                    profit += tax.Amount;
                    if (previousAuction != null)
                    {
                        profit -= previousAuction.HighestBid;
                        var purchaseCost = new PastFlip.ProfitChange($"Item purchase (combined stack)", -previousAuction.HighestBid)
                        {
                            ContextItemId = AuctionService.Instance.GetId(previousAuction.AuctionId)
                        };
                        changes.Add(purchaseCost);
                    }
                    else if (sell.Count > buyResp.Count)
                    {
                        // has sold multiple purchases in one auction
                        previousAuction = purchase;
                        continue;
                    }
                    if (await IsRemovedDrillPart(purchase, sell))
                        return;

                    if (sell.End - buyResp.End > TimeSpan.FromDays(14))
                        profit = 0; // no flip if it took more than 2 weeks
                    var flip = new PastFlip()
                    {
                        Flipper = Guid.Parse(sell.AuctioneerId),
                        ItemName = sell.ItemName,
                        ItemTag = sell.Tag,
                        ItemTier = sell.Tier,
                        Profit = profit,
                        SellPrice = sell.HighestBidAmount,
                        SellTime = sell.End,
                        PurchaseCost = buyResp.HighestBidAmount,
                        PurchaseTime = buyResp.End,
                        Uid = sell.UId,
                        PurchaseAuctionId = Guid.Parse(buyResp.Uuid),
                        SellAuctionId = Guid.Parse(sell.Uuid),
                        Version = 1,
                        TargetPrice = 0,
                        FinderType = LowPricedAuction.FinderType.UNKOWN,
                        ProfitChanges = changes
                    };
                    if (logMore)
                        logger.LogInformation($"Found flip {flip.Profit} {flip.ItemName} {flip.SellTime} {flip.PurchaseTime} {flip.SellAuctionId} {flip.PurchaseAuctionId}");
                    await flipStorageService.SaveFlip(flip);
                    Console.WriteLine($"Found flip https://sky.coflnet.com/a/{buyResp.Uuid} -> https://sky.coflnet.com/a/{sell.Uuid}");
                    return;
                }
            });
        }


        HashSet<string> partIds = [
            "goblin_omelette",
            "goblin_omelette_sunny_side",
            "goblin_omelette_pesto",
            "goblin_omelette_spicy",
            "starfall_seasoning",
            "goblin_omelette_blue_cheese",
            "tungsten_keychain",
            "mithril_drill_engine",
            "ruby_polished_drill_engine",
            "amber_polished_drill_engine",
            "titanium_drill_engine",
            "sapphire_polished_drill_engine",
            "mithril_fuel_tank",
            "perfectly_cut_fuel_tank",
            "gemstone_fuel_tank",
            "titanium_fuel_tank"
        ];
        private async Task<bool> IsRemovedDrillPart(Api.Client.Model.BidResult item, SaveAuction sell)
        {
            if (!partIds.Contains(sell.Tag.ToLower()))
                return false;
            logger.LogInformation($"Checking if {sell.Tag} is removed for {sell.Uuid}");
            if (sell.Tag.EndsWith("ENGINE"))
            {
                return await CheckPurchase(item, sell, "DrillPartEngine");
            }
            if (sell.Tag.EndsWith("FUEL_TANK"))
            {
                return await CheckPurchase(item, sell, "DrillPartFuelTank");
            }
            return await CheckPurchase(item, sell, "DrillPartUpgradeModule");

            async Task<bool> CheckPurchase(Api.Client.Model.BidResult item, SaveAuction sell, string filterName)
            {
                var purchases = await playerApi.ApiPlayerPlayerUuidBidsGetAsync(sell.AuctioneerId, 0, new Dictionary<string, string>() {
                    { "EndAfter", (item.End - TimeSpan.FromDays(14)).ToUnix().ToString() },
                    {filterName, sell.Tag.ToLower()} });
                return purchases.Any();
            }
        }

        private async Task<ApiSaveAuction> GetAuction(string uuid, SaveAuction sell, CancellationToken token)
        {
            if (uuid.Length >= 32 && Guid.TryParse(uuid, out _) || sell == null)
            {
                try
                {

                    var buyResp = await auctionsApi.ApiAuctionAuctionUuidGetWithHttpInfoAsync(uuid, 0, token).ConfigureAwait(false);
                    var buy = JsonConvert.DeserializeObject<ApiSaveAuction>(buyResp.RawContent);
                    if (buy == null)
                        throw new Exception($"could not load buy {uuid} {buyResp.StatusCode} Content: {buyResp.RawContent}");
                    return buy;
                }
                catch (Exception)
                {
                    logger.LogError($"Could not load auction {uuid} {sell?.Tag} {sell?.UId} {sell?.Uuid}");
                    throw;
                }
            }
            logger.LogInformation($"Loading trade item for {uuid}");
            // this is a trade mock
            var itemTrade = await transactionApi.TransactionItemItemIdGetAsync(long.Parse(uuid), 0);
            if (itemTrade.Count <= 0)
                throw new Exception($"could not load trade {uuid}");
            (int itemCount, long tradeEstimate, var items) = await GetTradeValue(itemTrade);
            var potentialItems = items.Where(i => i.ItemId > COIN_ID + 100).ToList();
            if (potentialItems.Count == 0)
                throw new Exception($"No item in trade for {uuid}");
            var itemInfo = await itemsApi.ApiItemsIdGetAsync(long.Parse(uuid), 0);
            var auction = representationConverter.FromItemRepresent(itemInfo);
            auction.HighestBidAmount = tradeEstimate;
            auction.End = itemTrade.First().TimeStamp;
            auction.Uuid = Guid.Empty.ToString("N");
            logger.LogInformation("Created virtual trade item for {playerId} {auction} from {item}",
                itemTrade.First().PlayerUuid,
                JsonConvert.SerializeObject(auction),
                JsonConvert.SerializeObject(itemInfo));
            return auction;

        }


        public static string GetDisplayName(ApiSaveAuction buy, SaveAuction sell)
        {
            string name = sell.ItemName;
            if (name.Length < 10 || buy.ItemName.Length < 10)
            {
                return name;
            }
            if (sell.Tag.StartsWith("PET_") && sell.FlatenedNBT.Any(f => f.Key == "exp") && buy.FlatenedNBT.Any(f => f.Key == "exp") && sell.ItemName != buy.ItemName
                                    && ParseFloat(sell.FlatenedNBT.First(f => f.Key == "exp").Value) - ParseFloat(buy.FlatenedNBT.First(f => f.Key == "exp").Value) > 100_000)
            {
                // level changed 
                // get original level from string [Lvl 63] Bat
                var start = buy.ItemName.IndexOf(' ');
                var endIndex = buy.ItemName.IndexOf(']') - start;
                if (endIndex < 0)
                {
                    Console.Write($"Could not find level in {buy.ItemName}");
                    return name;
                }
                var level = ParseFloat(buy.ItemName.Substring(start, endIndex));
                var insertAt = name.IndexOf(' ') + 1;
                name = name.Insert(insertAt, $"{level}->");
            }

            return name;
        }

        private static float ParseFloat(string value)
        {
            return float.Parse(value, NumberStyles.Float | NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture);
        }

        internal long GetId(string uuid)
        {
            if (uuid == null)
                return -1;
            if (uuid.Length < 16)
                return long.Parse(uuid);
            if (uuid.Length > 17)
                uuid = uuid.Substring(0, 17);
            var builder = new System.Text.StringBuilder(uuid);
            builder.Remove(12, 1);
            builder.Remove(16, uuid.Length - 17);
            var id = Convert.ToInt64(builder.ToString(), 16);
            if (id == 0)
                id = 1; // allow uId == 0 to be false if not calculated
            return id;
        }

        internal async Task AddTrades(IEnumerable<TradeModel> trades)
        {
            foreach (var trade in trades)
            {
                var auctions = await representationConverter.ConvertToDummyAuctions(trade);
                await IndexCassandra(auctions);
            }
        }


    }

    public class FlipSumaryEvent
    {
        public string SellUuid { get; set; }
        public string Flipper { get; set; }
        public int Profit { get; set; }
        public SaveAuction Sell { get; internal set; }
        public SaveAuction Buy { get; internal set; }
        public Flip Finder { get; internal set; }
    }
}