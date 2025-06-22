using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Microsoft.EntityFrameworkCore;
using Coflnet.Sky.SkyAuctionTracker.Services;

namespace Coflnet.Sky.SkyAuctionTracker.Controllers
{
    /// <summary>
    /// Main Controller handling tracking
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class TrackerController : ControllerBase
    {
        private readonly TrackerDbContext db;
        private readonly TrackerService service;
        private readonly ILogger<TrackerController> logger;
        private readonly FlipStorageService flipStorageService;

        /// <summary>
        /// Creates a new instance of <see cref="TrackerController"/>
        /// </summary>
        /// <param name="context"></param>
        public TrackerController(TrackerDbContext context, TrackerService service, ILogger<TrackerController> logger, FlipStorageService flipStorageService)
        {
            db = context;
            this.service = service;
            this.logger = logger;
            this.flipStorageService = flipStorageService;
        }

        /// <summary>
        /// Tracks a flip
        /// </summary>
        /// <param name="flip"></param>
        /// <param name="AuctionId"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("flip/{AuctionId}")]
        public async Task<Flip> TrackFlip([FromBody] Flip flip, string AuctionId)
        {
            flip.AuctionId = GetId(AuctionId);
            await service.AddFlip(flip);
            return flip;
        }

        [HttpGet]
        [Route("flip/{AuctionId}")]
        public async Task<IEnumerable<FinderContext>> GetFlip(string AuctionId)
        {
            return await flipStorageService.GetFinderContexts(Guid.Parse(AuctionId));
        }

        /// <summary>
        /// Tracks a flip event for an auction
        /// </summary>
        /// <param name="flipEvent"></param>
        /// <param name="AuctionId"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("event/{AuctionId}")]
        public async Task<FlipEvent> TrackFlipEvent(FlipEvent flipEvent, string AuctionId)
        {

            flipEvent.AuctionId = GetId(AuctionId);

            await service.AddEvent(flipEvent);
            return flipEvent;
        }

        /// <summary>
        /// Returns the time when the last flip was found
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("flip/time")]
        public async Task<DateTime> GetLastFlipTime()
        {

            var flipEventAlreadyExists = await db.FlipEvents.OrderByDescending(f => f.Id).FirstOrDefaultAsync();
            if (flipEventAlreadyExists == null)
            {
                return DateTime.UnixEpoch;
            }
            return flipEventAlreadyExists.Timestamp;
        }

        /// <summary>
        /// Returns the average time to receive flips
        /// of the last X flips
        /// </summary>
        /// <param name="number">How many flips to analyse</param>
        /// <returns></returns>
        [HttpGet]
        [Route("flip/receive/times")]
        public async Task<double> GetFlipRecieveTimes(int number = 100)
        {

            var flips = await db.Flips.OrderByDescending(f => f.Id).Take(number).ToListAsync();

            double sum = 0;
            await Task.WhenAll(flips.Select(async flip =>
           {
               sum += await db.FlipEvents.Where(f => f.AuctionId == flip.AuctionId && f.Type == FlipEventType.FLIP_RECEIVE)
                                   .AverageAsync(f => (f.Timestamp - flip.Timestamp).TotalSeconds);
           }));
            return sum / flips.Count();
        }

        /// <summary>
        /// Calculates the average buying time for a player
        /// </summary>
        /// <param name="PlayerId"></param>
        /// <param name="number"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("flipBuyingTimeForPlayer")]
        public async Task<double> GetFlipBuyingTimeForPlayer(long PlayerId, int number = 50)
        {
            var purchaseConfirmEvents = await db.FlipEvents.Where(flipEvent => flipEvent.PlayerId == PlayerId && flipEvent.Type == FlipEventType.PURCHASE_CONFIRM)
                                                            .OrderByDescending(f => f.Id).Take(number).ToListAsync();

            double sum = 0;
            await Task.WhenAll(purchaseConfirmEvents.Select(async purchaseEvent =>
            {
                var recieveEvent = await db.FlipEvents.Where(f => f.AuctionId == purchaseEvent.AuctionId
                                                            && f.PlayerId == purchaseEvent.PlayerId
                                                            && f.Type == FlipEventType.FLIP_RECEIVE)
                                                            .FirstOrDefaultAsync();
                sum += (purchaseEvent.Timestamp - recieveEvent.Timestamp).TotalSeconds;
            }));
            return sum / purchaseConfirmEvents.Count();
        }

        /// <summary>
        /// Returns flips that were bought before a finder found them
        /// </summary>
        /// <param name="PlayerId"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("flipsBoughtBeforeFound")]
        public async Task<List<Flip>> GetFlipsBoughtBeforeFound(long PlayerId)
        {
            var purchaseConfirmEvents = await db.FlipEvents.Where(flipEvent => flipEvent.PlayerId == PlayerId && flipEvent.Type == FlipEventType.AUCTION_SOLD).ToListAsync();

            var flips = new List<Flip>();
            await Task.WhenAll(purchaseConfirmEvents.Select(async purchaseEvent =>
            {
                var flip = await db.Flips.FirstOrDefaultAsync(f => f.AuctionId == purchaseEvent.AuctionId);
                if (flip != null && flip.Timestamp > purchaseEvent.Timestamp)
                {
                    flips.Add(flip);
                }
            }));
            return flips;
        }

        /// <summary>
        /// Gets the flips for a given auction
        /// </summary>
        /// <param name="auctionId"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("flips/{auctionId}")]
        public async Task<List<Flip>> GetFlipsOfAuction(long auctionId)
        {
            return await db.Flips.Where(flip => flip.AuctionId == auctionId).ToListAsync();
        }
        [HttpPost]
        [Route("flips/estimates/batch")]
        public async Task<List<Flip>> GetFlipsOfAuctionBatch(List<long> auctionIds)
        {
            return await db.Flips.Where(flip => auctionIds.Contains(flip.AuctionId)).ToListAsync();
        }



        /// <summary>
        /// Gets the flips for a given auctions
        /// </summary>
        /// <param name="auctionIds">collection of auction ids</param>
        /// <returns></returns>
        [HttpPost]
        [Route("batch/flips")]
        public async Task<List<Flip>> GetFlipsOfAuctions(List<long> auctionIds)
        {
            return await db.Flips.Where(flip => auctionIds.Contains(flip.AuctionId)).ToListAsync();
        }


        /// <summary>
        /// Returns the player and the amount of second he bought an auction faster than the given player
        /// </summary>
        /// <param name="auctionId"></param>
        /// <param name="PlayerId"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("/flip/outspeed/{auctionId}/{PlayerId}")]
        public async Task<ValueTuple<long, double>> GetOutspeedTime(long auctionId, long PlayerId)
        {
            var flipClickEventTask = db.FlipEvents.Where(flip => flip.AuctionId == auctionId && flip.Type == FlipEventType.FLIP_CLICK && flip.PlayerId == PlayerId).FirstOrDefaultAsync();
            var flipSoldEvent = await db.FlipEvents.Where(flip => flip.AuctionId == auctionId && flip.Type == FlipEventType.AUCTION_SOLD).FirstOrDefaultAsync();
            var flipClickEvent = await flipClickEventTask;
            if (flipClickEvent == null || flipSoldEvent == null)
                return new ValueTuple<long, double>(0, 0);

            return new ValueTuple<long, double>(flipSoldEvent.PlayerId, (flipSoldEvent.Timestamp - flipClickEvent.Timestamp).TotalSeconds);
        }


        [HttpGet]
        [Route("/flips/{PlayerId}")]
        [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any, VaryByQueryKeys = new[] { "from", "to" })]
        public async Task<IEnumerable<PastFlip>> GetFlipsOfPlayer(Guid PlayerId, DateTime from, DateTime to, bool getAll = false)
        {
            if (from == DateTime.MinValue)
                from = DateTime.Now.AddYears(-1);
            if (to == DateTime.MinValue)
                to = DateTime.Now;
            var all = await flipStorageService.GetFlips(PlayerId, from, to);
            if (getAll)
                return all;
            // re-calculated flips don't have milliseconds in the timestamp, thus the newer flip has lower timestamp
            return all.GroupBy(f => f.SellAuctionId == Guid.Empty ? f.PurchaseAuctionId : f.SellAuctionId).Select(f => f.OrderBy(f => f.SellTime).First());
        }

        [HttpGet]
        [Route("/flips/{PlayerId}/{uid}")]
        [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any, VaryByQueryKeys = new[] { "from", "to" })]
        public async Task<PastFlip> GetFlipsOfPlayerByUid(Guid PlayerId, long uid)
        {
            return await flipStorageService.GetFlip(PlayerId, uid);
        }

        [HttpGet]
        [Route("/flips/exempt")]
        [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
        public async Task<IEnumerable<OutspedFlip>> GetExemptFlips()
        {
            return await flipStorageService.GetOutspedFlips();
        }

        [HttpPost]
        [Route("/flips/complicated")]
        public async Task SaveComplicatedFlip(ComplicatedFlip flip)
        {
            await flipStorageService.StoreComplicated(flip);
        }

        [HttpGet]
        [Route("/flips/complicated/{tag}")]
        public async Task<IEnumerable<ComplicatedFlip>> GetComplicatedFlips(string tag)
        {
            return await flipStorageService.GetComplicatedFlips(tag);
        }

        [HttpGet]
        [Route("/flips/unknown")]
        public async Task<IEnumerable<PastFlip>> GetUnknownFlips(DateTime start, DateTime end)
        {
            return await flipStorageService.GetUnknownFlips(start, end);
        }

        private long GetId(string uuid)
        {
            return service.GetId(uuid);
        }
    }
}
