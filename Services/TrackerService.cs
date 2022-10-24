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
using RestSharp;

namespace Coflnet.Sky.SkyAuctionTracker.Services
{
    public class TrackerService
    {
        private TrackerDbContext db;
        private ILogger<TrackerService> logger;
        private IAuctionsApi auctionsApi;
        private FlipSumaryEventProducer flipSumaryEventProducer;
        private IServiceScopeFactory scopeFactory;

        public TrackerService(TrackerDbContext db, ILogger<TrackerService> logger, IAuctionsApi api, FlipSumaryEventProducer flipSumaryEventProducer, IServiceScopeFactory scopeFactory)
        {
            this.db = db;
            this.logger = logger;
            this.auctionsApi = api;
            this.flipSumaryEventProducer = flipSumaryEventProducer;
            this.scopeFactory = scopeFactory;
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
            var count = await db.SaveChangesAsync();
            _ = Task.Run(async () =>
            {
                try
                {
                    await TfmSellCallback(sells);
                }
                catch (System.Exception error)
                {
                    dev.Logger.Instance.Error(error, "TFM sell callback failed");
                }
            });

            Console.WriteLine($"Saved sells {count}");
        }

        private async Task TfmSellCallback(IEnumerable<SaveAuction> sells)
        {
            var uids = sells.Where(s => s.FlatenedNBT.Where(n => n.Key == "uid").Any()).ToDictionary(s => s.FlatenedNBT.Where(n => n.Key == "uid").Select(n => n.Value).FirstOrDefault());
            var exists = await auctionsApi.ApiAuctionsUidsSoldPostAsync(new Api.Client.Model.InventoryBatchLookup() { Uuids = uids.Keys.ToList() });
            var soldAuctions = exists.Select(item => new
            {
                sell = uids.GetValueOrDefault(item.Key),
                buy = item.Value.Where(v => v.Uuid != uids.GetValueOrDefault(item.Key)?.Uuid && v.Timestamp < uids.GetValueOrDefault(item.Key)?.End)
                                    .OrderByDescending(u => u.Timestamp).FirstOrDefault()
            }).Where(item => item.buy != null).ToList();
            var soldUids = soldAuctions.Select(u => GetId(u.buy.Uuid)).ToHashSet();
            var flipsSoldFromTfm = soldUids.Select(f => new Flip() { AuctionId = f, FinderType = LowPricedAuction.FinderType.TFM }).ToList();

            List<Flip> finders = new();
            using (var scope = scopeFactory.CreateScope())
            using (var dbScoped = scope.ServiceProvider.GetRequiredService<TrackerDbContext>())
            {
                finders = await dbScoped.Flips.Where(f => soldUids.Contains(f.AuctionId)).ToListAsync();
            }

            var result = flipsSoldFromTfm.Select(f =>
            {
                var match = soldAuctions.Where(s => GetId(s.buy.Uuid) == f.AuctionId).FirstOrDefault();
                return new
                {
                    foundTime = f.Timestamp,
                    sell = new { uuid = match?.sell?.Uuid, sellPrice = match?.sell?.HighestBidAmount },
                    originAuction = match?.buy?.Uuid,
                };
            }).ToList();

            if (result.Count == 0)
                return; // nothing to do

            var client = new RestClient("https://tfm.thom.club");
            var request = new RestRequest("/flip_sold", Method.Post);
            request.AddJsonBody(result);
            var token = new CancellationTokenSource(10000).Token;
            var response = await client.ExecuteAsync(request, token);

            Console.WriteLine($"TFM sell response ({response.StatusCode}) sent {result.Count}: {response.Content}");
            // Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(soldAuctions, Newtonsoft.Json.Formatting.Indented));
            foreach (var item in soldAuctions)
            {
                token = new CancellationTokenSource(10000).Token;
                var buy = await auctionsApi.ApiAuctionAuctionUuidGetAsync(item.buy.Uuid, 0, token);
                flipSumaryEventProducer.Produce(new FlipSumaryEvent()
                {
                    Flipper = item.sell.AuctioneerId,
                    Buy = buy,
                    Sell = item.sell,
                    Finder = finders.Where(f => f.AuctionId == item.sell.UId).FirstOrDefault(),
                    Profit = (int)(item.sell.HighestBidAmount - buy?.HighestBidAmount ?? 0),
                });
            }
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