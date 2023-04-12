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

namespace Coflnet.Sky.SkyAuctionTracker.Services
{
    public class TrackerService
    {
        private TrackerDbContext db;
        private ILogger<TrackerService> logger;
        private IAuctionsApi auctionsApi;
        private FlipSumaryEventProducer flipSumaryEventProducer;
        private IServiceScopeFactory scopeFactory;
        private ProfitChangeService profitChangeService;
        private FlipStorageService flipStorageService;
        private ActivitySource activitySource;
        private const short Version = 1;
        Counter flipSavedCounter = Metrics.CreateCounter("flip_saved", "How many flips were saved");

        public TrackerService(
            TrackerDbContext db,
            ILogger<TrackerService> logger,
            IAuctionsApi api,
            FlipSumaryEventProducer flipSumaryEventProducer,
            IServiceScopeFactory scopeFactory,
            ProfitChangeService profitChangeService,
            FlipStorageService flipStorageService,
            ActivitySource activitySource)
        {
            this.db = db;
            this.logger = logger;
            this.auctionsApi = api;
            this.flipSumaryEventProducer = flipSumaryEventProducer;
            this.scopeFactory = scopeFactory;
            this.profitChangeService = profitChangeService;
            this.flipStorageService = flipStorageService;
            this.activitySource = activitySource;
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
            await db.Database.BeginTransactionAsync();
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
                    flipSavedCounter.Inc(count);
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
            var toTrack = sells.ToList();

            await IndexCassandra(toTrack);
            Console.WriteLine($"Saved sells {count}");
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
                var original = JsonConvert.DeserializeObject<Api.Client.Model.ColorSaveAuction>(originalResp.RawContent);
                if (original == null)
                    throw new Exception($"auction {a.ToString("N")} could not be loaded");

                var mapped = mapper.Map<SaveAuction>(original);
                if (mapped == null)
                    throw new Exception($"auction {JsonConvert.SerializeObject(original)} could not be mapped");
                return mapped;
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
            var buyLookup = await auctionsApi.ApiAuctionsUidsSoldPostWithHttpInfoAsync(new Api.Client.Model.InventoryBatchLookup() { Uuids = sellLookup.Keys.ToList() });
            if (buyLookup.StatusCode != System.Net.HttpStatusCode.OK)
                throw new Exception("Could not reach api to load purchases " + buyLookup.StatusCode);
            var exists = buyLookup.Data;
            if (exists.Count == 0)
                return;
            var soldAuctions = exists.Select(item => new
            {
                sell = sellLookup.GetValueOrDefault(item.Key),
                buy = item.Value.Where(v => v.Uuid != sellLookup.GetValueOrDefault(item.Key)?.Uuid && v.Timestamp < sellLookup.GetValueOrDefault(item.Key)?.End)
                                    .OrderByDescending(u => u.Timestamp).FirstOrDefault()
            }).Where(item => item.buy != null).ToList();
            var purchaseUid = soldAuctions.Select(u => GetId(u.buy.Uuid)).ToHashSet();
            var flipsSoldFromTfm = purchaseUid.Select(f => new Flip() { AuctionId = f, FinderType = LowPricedAuction.FinderType.TFM }).ToList();

            List<Flip> finders = new();
            using (var scope = scopeFactory.CreateScope())
            using (var dbScoped = scope.ServiceProvider.GetRequiredService<TrackerDbContext>())
            {
                finders = await dbScoped.Flips.Where(f => purchaseUid.Contains(f.AuctionId)).ToListAsync();
            }


            // Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(soldAuctions, Newtonsoft.Json.Formatting.Indented));
            await Parallel.ForEachAsync(soldAuctions, new ParallelOptions()
            {
                MaxDegreeOfParallelism = 2,
                CancellationToken = new CancellationTokenSource(20000).Token
            }, async (item, token) =>
            {
                var buy = (await auctionsApi.ApiAuctionAuctionUuidGetAsync(item.buy.Uuid, 0, token)) 
                            ?? throw new Exception("could not load buy from sky api " + item.buy.Uuid);
                flipSumaryEventProducer.Produce(new FlipSumaryEvent()
                {
                    Flipper = item.sell.AuctioneerId,
                    Buy = buy,
                    Sell = item.sell,
                    Finder = finders.Where(f => f.AuctionId == GetId(item.buy.Uuid)).FirstOrDefault(),
                    Profit = (int)(item.sell.HighestBidAmount - buy?.HighestBidAmount ?? 0),
                });
                try
                {
                    var sell = item.sell;
                    var purchaseId = GetId(buy.Uuid);
                    var flipFound = finders.Where(f => f != null && f.AuctionId == purchaseId).OrderByDescending(f => f.Timestamp).FirstOrDefault();
                    List<PastFlip.ProfitChange> changes = new();
                    try
                    {
                        changes = await profitChangeService.GetChanges(buy, sell).ToListAsync();
                    }
                    catch (System.Exception e)
                    {
                        logger.LogError(e, "Could not load profit changes");
                        throw;
                    }
                    var profit = (long)(item.sell.HighestBidAmount - buy?.HighestBidAmount ?? 0) + changes.Sum(c => c.Amount);
                    if (sell.End - buy.End > TimeSpan.FromDays(14))
                        profit = 0; // no flip if it took more than 2 weeks
                    var flip = new PastFlip()
                    {
                        Flipper = Guid.Parse(sell.AuctioneerId),
                        ItemName = item.sell.ItemName,
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
                        ProfitChanges = changes
                    };
                    await flipStorageService.SaveFlip(flip);
                }
                catch (System.Exception e)
                {
                    logger.LogError(e, $"Failed to save flip {item.buy.Uuid} -> {item.sell.Uuid} {Newtonsoft.Json.JsonConvert.SerializeObject(item.sell)}\n{Newtonsoft.Json.JsonConvert.SerializeObject(buy)}");
                    throw;
                }
            });
        }

        internal long GetId(string uuid)
        {
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
        public Api.Client.Model.ColorSaveAuction Buy { get; internal set; }
        public Flip Finder { get; internal set; }
    }
}