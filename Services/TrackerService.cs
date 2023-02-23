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
                    logger.LogInformation($"saved {count} flips");
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

        public async Task RefreshFlip(string auctionId)
        {
            var auction = await auctionsApi.ApiAuctionAuctionUuidGetAsync(auctionId);
            var mapper = new AutoMapper.Mapper(new MapperConfiguration(cfg =>
            {
                cfg.CreateMap<Api.Client.Model.ColorSaveAuction, SaveAuction>().ForMember(dest => dest.FlatenedNBT, opt => opt.MapFrom(src => src.FlatNbt));
                cfg.CreateMap<Api.Client.Model.ColorEnchant, Enchantment>();
                cfg.CreateMap<Api.Client.Model.SaveBids, SaveBids>();
                cfg.AddGlobalIgnore("NbtData");
            }));
            var mapped = mapper.Map<SaveAuction>(auction);
            Console.WriteLine(JsonConvert.SerializeObject(mapped, Formatting.Indented));
            await IndexCassandra(new SaveAuction[] { mapped });
        }

        private async Task IndexCassandra(IEnumerable<SaveAuction> sells)
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
                if (sells.Count() < 10)
                    dev.Logger.Instance.Error(error, $"cassandra index failed batch size {sells.Count()}");
                await Task.Delay(200);
                if (sells.Count() > 1)
                {
                    await IndexCassandra(sells.Take(sells.Count() / 2));
                    await IndexCassandra(sells.Skip(sells.Count() / 2));
                }
                else
                    throw error;
            }
        }

        private async Task CalculateAndIndex(IEnumerable<SaveAuction> sells)
        {
            var sellLookup = sells.Where(s => s.FlatenedNBT.Where(n => n.Key == "uid").Any())
                                .GroupBy(s => new { uid = s.FlatenedNBT.Where(n => n.Key == "uid").First(), s.End }).Select(g => g.First())
                                .ToDictionary(s => s.FlatenedNBT.Where(n => n.Key == "uid").Select(n => n.Value).FirstOrDefault());
            var exists = await auctionsApi.ApiAuctionsUidsSoldPostAsync(new Api.Client.Model.InventoryBatchLookup() { Uuids = sellLookup.Keys.ToList() });
            if (exists == null)
                throw new Exception("Could not reach api to load purchases");
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
                var buy = await auctionsApi.ApiAuctionAuctionUuidGetAsync(item.buy.Uuid, 0, token);
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
                    var flipFound = finders.Where(f => f.AuctionId == GetId(buy.Uuid)).OrderByDescending(f => f.Timestamp).FirstOrDefault();
                    var changes = await profitChangeService.GetChanges(buy, sell).ToListAsync();
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
                    if (flip.ProfitChanges.Count() > 2 && flip.Profit != 0 && !flip.ProfitChanges.Any(c => c.Label.StartsWith("crafting material")) || flip.ProfitChanges.Any(c => c.Label.Contains("drill_part")))
                    {
                        logger.LogInformation($"saving flip {Newtonsoft.Json.JsonConvert.SerializeObject(flip, Newtonsoft.Json.Formatting.Indented)}");
                    }
                }
                catch (System.Exception)
                {
                    Console.WriteLine($"Failed to save flip {item.buy.Uuid} -> {item.sell.Uuid} {Newtonsoft.Json.JsonConvert.SerializeObject(item.sell, Newtonsoft.Json.Formatting.Indented)}");
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