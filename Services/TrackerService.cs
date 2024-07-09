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
        private const short Version = 1;
        private const int COIN_ID = 1_000_001;
        readonly Counter flipFoundCounter = Metrics.CreateCounter("sky_fliptracker_saved_finding", "How many found flips were saved");
        readonly Counter userFlipCounter = Metrics.CreateCounter("sky_fliptracker_user_flip", "How many flips were done by a user");


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
            PlayerState.Client.Api.ITransactionApi transactionApi)
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
        }

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
                var disabled = await settingsApi.SettingsUserIdSettingKeyGetAsync(playerUuid, "disable-buy-speed-board");
                if (disabled != null)
                {
                    logger.LogInformation($"user {playerUuid} disabled buy speed board");
                    continue;
                }
                var leaderboardSlug = "sky-buyspeed-" + DateTime.UtcNow.ToString("yyyy-MM-dd");
                await scoresApi.ScoresLeaderboardSlugPostAsync(leaderboardSlug, new Leaderboard.Client.Model.ScoreCreate()
                {
                    UserId = playerUuid,
                    Score = (long)(timeToBuy.TotalSeconds * -1000),
                    HighScore = true
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
            await IndexCassandra(auctions);
        }

        public async Task IndexCassandra(IEnumerable<SaveAuction> sells)
        {
            using var activity = activitySource.StartActivity("IndexCassandra", ActivityKind.Server);
            try
            {
                await CalculateAndIndex(sells);
                return;
            }
            catch (System.Exception error)
            {
                if (error.Message.Contains("with the same key has already been added."))
                {
                    foreach (var item in sells)
                    {
                        await CalculateAndIndex(new SaveAuction[] { item });
                    }
                    logger.LogInformation($"saved sells {sells.Count()} one by one because dupplicate");
                    return;
                }
                if (sells.Count() < 4)
                    dev.Logger.Instance.Error(error, $"cassandra index failed batch size {sells.Count()}");
                await Task.Delay(200);
                if (sells.Count() > 1)
                {
                    await IndexCassandra(sells.Take(sells.Count() / 2));
                    await IndexCassandra(sells.Skip(sells.Count() / 2));
                }
                else
                    throw new CoflnetException("load error", "This sell caused error: " + JsonConvert.SerializeObject(sells.First()));
            }
        }

        private async Task CalculateAndIndex(IEnumerable<SaveAuction> sells)
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
            var soldAuctions = exists.Select(item => new
            {
                sell = sellLookup.GetValueOrDefault(item.Key),
                buy = item.Value.Where(v => v.Uuid != sellLookup.GetValueOrDefault(item.Key)?.Uuid
                                        && (v.Timestamp < sellLookup.GetValueOrDefault(item.Key)?.End
                                        || v.Timestamp > DateTime.UtcNow))
                                    .OrderByDescending(u => u.Timestamp).FirstOrDefault()
            }).Where(item => item.buy != null).ToList();
            var purchaseUid = soldAuctions.Select(u => GetId(u.buy.Uuid)).ToHashSet();
            foreach (var tradeSource in tradeUuidLookup)
            {
                var uid = tradeSource.Key.Split("-").Last();
                var sell = sellLookup.GetValueOrDefault(uid) ?? throw new Exception($"Could not find sell for trade item {uid} {tradeSource.Key}");
                soldAuctions.Add(new
                {
                    sell = sell,
                    buy = new Api.Client.Model.ItemSell() { Buyer = null, Uuid = tradeSource.Value.OrderByDescending(t => t).First().ToString() }
                });
            }

            List<Flip> finders = new();
            using (var scope = scopeFactory.CreateScope())
            using (var dbScoped = scope.ServiceProvider.GetRequiredService<TrackerDbContext>())
            {
                finders = await dbScoped.Flips.Where(f => purchaseUid.Contains(f.AuctionId)).ToListAsync();
            }
            var parallelOptions = new ParallelOptions()
            {
                MaxDegreeOfParallelism = 2,
                CancellationToken = new CancellationTokenSource(20000).Token
            };
            var noUidTask = CheckNoIdAuctions(sells, parallelOptions);
            // Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(soldAuctions, Newtonsoft.Json.Formatting.Indented));
            await Parallel.ForEachAsync(soldAuctions, parallelOptions, async (item, token) =>
            {
                var buy = await GetAuction(item.buy.Uuid, item.sell, token).ConfigureAwait(false);
                try
                {
                    var sell = item.sell;
                    (FlipFlags flags, var change) = await CheckTrade(buy, sell); 
                    var purchaseId = GetId(buy.Uuid);
                    var flipFound = finders.Where(f => f != null && f.AuctionId == purchaseId).OrderByDescending(f => f.Timestamp).FirstOrDefault();
                    flipSumaryEventProducer.Produce(new FlipSumaryEvent()
                    {
                        Flipper = item.sell.AuctioneerId,
                        Buy = buy,
                        Sell = item.sell,
                        Finder = flipFound,
                        Profit = (int)(item.sell.HighestBidAmount - buy?.HighestBidAmount ?? 0),
                    });
                    List<PastFlip.ProfitChange> changes = new();
                    try
                    {
                        changes = await profitChangeService.GetChanges(buy, sell).ToListAsync().ConfigureAwait(false);
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
                }
                catch (System.Exception e)
                {
                    logger.LogError(e, $"Failed to save flip {item.buy.Uuid} -> {item.sell.Uuid} {Newtonsoft.Json.JsonConvert.SerializeObject(item.sell)}\n{Newtonsoft.Json.JsonConvert.SerializeObject(buy)}");
                    throw;
                }
            });
            await noUidTask;
        }

        private static List<Guid> GetItemUuids(IEnumerable<SaveAuction> sells)
        {
            return sells.Select(s => s.FlatenedNBT.Where(n => n.Key == "uuid").FirstOrDefault().Value)
                .Where(v => v != default).Select(Guid.Parse).ToHashSet().ToList();
        }

        private async Task AddListingAttempts(SaveAuction sell, List<PastFlip.ProfitChange> changes)
        {
            var uid = sell.FlatenedNBT.Where(n => n.Key == "uid").FirstOrDefault().Value;
            var listings = await playerApi.ApiPlayerPlayerUuidAuctionsGetAsync(sell.AuctioneerId, 0, new Dictionary<string, string>() { { "UId", uid }, { "HighestBid", "0" } });
            foreach (var listing in listings.Where(l => l.AuctionId != sell.Uuid))
            {
                var change = profitChangeService.GetAhTax(listing.HighestBid, listing.StartingBid);
                change.ContextItemId = AuctionService.Instance.GetId(listing.AuctionId);
                change.Label = $"Listing attempt {listing.StartingBid}";
                changes.Add(change);
                Console.WriteLine($"Found listing attempt {listing.StartingBid} {listing.HighestBid} {listing.AuctionId} for {sell.Uuid}");
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
            flags |= FlipFlags.DifferentBuyer;

            // check trade
            var items = await itemsApi.ApiItemsFindUuidPostAsync(new(){
                            new (){
                                Tag = sell.Tag,
                                Uuid = Guid.Parse(sell.FlatenedNBT.Where(n => n.Key == "uuid").First().Value)
                            }
                        });
            if (items.Count <= 0)
            {
                return (flags, null);
            }
            // TODO: maybe make sure to use best match to sell modifiers
            var itemInfo = items.First();
            var itemTrade = await transactionApi.TransactionItemItemIdGetAsync(itemInfo.Id ?? throw new Exception("no item id"), 0);
            if (itemTrade.Count > 0)
            {
                (int itemCount, long tradeEstimate, _) = await GetTradeValue(itemTrade);
                flags |= FlipFlags.ViaTrade;
                buy.HighestBidAmount = tradeEstimate;
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
                                .GroupBy(s => new { s.AuctioneerId, s.Tag });
            await Parallel.ForEachAsync(noUidCheck, async (item, token) =>
            {
                var logMore = item.First().Tag == "PINK_DONUT_PERSONALITY";
                var query = new Dictionary<string, string>() { { "tag", item.Key.Tag } };
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
                    var profit = sell.HighestBidAmount - buyResp.HighestBidAmount;
                    var tax = profitChangeService.GetAhTax(sell.HighestBidAmount, sell.StartingBid);
                    profit += tax.Amount;
                    var changes = new List<PastFlip.ProfitChange>() { tax };
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

        private async Task<ApiSaveAuction> GetAuction(string uuid, SaveAuction sell, CancellationToken token)
        {
            if (uuid.Length >= 32 && (Guid.TryParse(uuid, out _) || sell == null))
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
            var auction = FromitemRepresent(itemInfo);
            auction.HighestBidAmount = tradeEstimate;
            auction.End = itemTrade.First().TimeStamp;
            auction.Uuid = Guid.Empty.ToString("N");
            logger.LogInformation("Created virtual trade item {auction} from {item}", JsonConvert.SerializeObject(auction), JsonConvert.SerializeObject(itemInfo));
            return auction;

        }

        public ApiSaveAuction FromitemRepresent(Coflnet.Sky.PlayerState.Client.Model.Item i)
        {
            var auction = new SaveAuction()
            {
                Count = i.Count ?? 0,
                Tag = i.Tag,
                ItemName = i.ItemName,
            };
            auction.Enchantments = i.Enchantments?.Select(e => new Enchantment()
            {
                Type = Enum.TryParse<Enchantment.EnchantmentType>(e.Key, out var type) ? type : Enchantment.EnchantmentType.unknown,
                Level = (byte)(e.Value ?? 0)
            }).ToList() ?? new();
            auction.Tier = Enum.TryParse<Tier>(i.ExtraAttributes.FirstOrDefault(a => a.Key == "tier").Value?.ToString() ?? "", out var tier) ? tier : Tier.UNKNOWN;
            auction.Reforge = Enum.TryParse<ItemReferences.Reforge>(i.ExtraAttributes.FirstOrDefault(a => a.Key == "modifier").Value?.ToString() ?? "", out var reforge) ? reforge : ItemReferences.Reforge.Unknown;
            auction.SetFlattenedNbt(NBT.FlattenNbtData(i.ExtraAttributes));
            return JsonConvert.DeserializeObject<ApiSaveAuction>(JsonConvert.SerializeObject(auction));
        }

        public static string GetDisplayName(ApiSaveAuction buy, SaveAuction sell)
        {
            string name = sell.ItemName;
            if (name.Length < 10 || buy.ItemName.Length < 10)
            {
                return name;
            }
            if (sell.Tag.StartsWith("PET_") && sell.FlatenedNBT.Any(f => f.Key == "exp") && sell.ItemName != buy.ItemName
                                    && ParseFloat(sell.FlatenedNBT.First(f => f.Key == "exp").Value) - ParseFloat(buy.FlatenedNBT.First(f => f.Key == "exp").Value) > 100_000)
            {
                // level changed 
                // get original level from string [Lvl 63] Bat
                var start = buy.ItemName.IndexOf(' ');
                var endIndex = buy.ItemName.IndexOf(']') - start;
                if (endIndex < 0)
                {
                    Console.Write($"Could not find level in {buy.ItemName}");
                    Task.Delay(1000).Wait();
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