using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.SkyAuctionTracker.Models;
using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;

namespace Coflnet.Sky.SkyAuctionTracker.Controllers
{
    /// <summary>
    /// Main Controller handling tracking
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class AnalyseController : ControllerBase
    {
        private readonly TrackerDbContext db;
        private readonly ILogger<AnalyseController> logger;

        /// <summary>
        /// Creates a new instance of <see cref="TrackerController"/>
        /// </summary>
        /// <param name="context"></param>
        public AnalyseController(TrackerDbContext context, ILogger<AnalyseController> logger)
        {
            db = context;
            this.logger = logger;
        }

        /// <summary>
        /// Gets all flips between two timestamps
        /// </summary>
        /// <param name="finderType">The finder type to look at</param>
        /// <param name="start">The timestamp to start searching at (inclusive)</param>
        /// <param name="end">The timestamp to end searching at (exclusive)</param>
        /// <returns></returns>
        [HttpGet]
        [Route("finder/{finderType}")]
        public async Task<List<Flip>> GetForFinder(LowPricedAuction.FinderType finderType, DateTime start, DateTime end)
        {
            return await db.Flips.Where(f => f.FinderType == finderType && f.Timestamp >= start && f.Timestamp < end).ToListAsync();
        }

        /// <summary>
        /// Gets all flips between two timestamps
        /// </summary>
        /// <param name="finderType">The finder type to look at</param>
        /// <param name="start">The timestamp to start searching at (inclusive)</param>
        /// <param name="end">The timestamp to end searching at (exclusive)</param>
        /// <returns></returns>
        [HttpGet]
        [Route("finder/{finderType}/ids")]
        public async Task<List<long>> GetUidsForFinder(LowPricedAuction.FinderType finderType, DateTime start, DateTime end)
        {
            return await db.Flips.Where(f => f.FinderType == finderType && f.Timestamp >= start && f.Timestamp < end).Select(s => s.AuctionId).ToListAsync();
        }


        /// <summary>
        /// Returns how many user recently received a flip
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("/users/active/count")]
        public async Task<int> GetNumberOfActiveFlipperUsers()
        {
            var minTime = DateTime.Now.Subtract(TimeSpan.FromMinutes(3));
            return await db.FlipEvents.Where(flipEvent => flipEvent.Id > db.FlipEvents.Max(f => f.Id) - 5000 && flipEvent.Type == FlipEventType.FLIP_CLICK && flipEvent.Timestamp > minTime)
                .GroupBy(flipEvent => flipEvent.PlayerId).CountAsync();
        }

        /// <summary>
        /// Returns the speed advantage of a player
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("player/{id}/speed")]
        public async Task<SpeedCompResult> CheckPlayerSpeedAdvantage(long playerId)
        {
            var minTime = DateTime.Now.Subtract(TimeSpan.FromMinutes(30));
            var relevantFlips = await db.FlipEvents.Where(flipEvent => flipEvent.Id > db.FlipEvents.Max(f => f.Id) - 5000 && flipEvent.Type == FlipEventType.PURCHASE_CONFIRM && flipEvent.Timestamp > minTime)
                .ToListAsync();

            var ids = relevantFlips.Select(f => f.AuctionId).ToHashSet();

            var clicks = await db.FlipEvents.Where(f => ids.Contains(f.AuctionId) && f.Type == FlipEventType.FLIP_CLICK).GroupBy(f => f.AuctionId)
                        .ToDictionaryAsync(f => f.Key, f => f.Select(f => f.Timestamp).ToList());

            var avg = relevantFlips.Average(f =>
            {
                var refClicks = clicks[f.AuctionId];
                var time = new DateTime((long)refClicks.Average(c=>c.Ticks));
                return (time - f.Timestamp).TotalSeconds;
            });

            return new SpeedCompResult()
            {
                Clicks = clicks,
                Buys = relevantFlips.ToDictionary(f=>f.AuctionId,f=>f.Timestamp),
                AvgAdvantageSeconds = avg,
                AvgAdvantage = TimeSpan.FromSeconds(avg)
            };
        }

        public class SpeedCompResult
        {
            public Dictionary<long, List<DateTime>> Clicks;
            public Dictionary<long, DateTime> Buys;
            public TimeSpan AvgAdvantage;
            public double AvgAdvantageSeconds;
        }
    }
}
