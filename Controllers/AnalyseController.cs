using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.SkyAuctionTracker.Models;
using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using Coflnet.Sky.Core;
using Coflnet.Sky.SkyAuctionTracker.Services;

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
        private readonly TrackerService service;

        /// <summary>
        /// Creates a new instance of <see cref="TrackerController"/>
        /// </summary>
        /// <param name="context"></param>
        /// <param name="logger"></param>
        /// <param name="service"></param>
        public AnalyseController(TrackerDbContext context, ILogger<AnalyseController> logger, TrackerService service)
        {
            db = context;
            this.logger = logger;
            this.service = service;
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
        [Route("player/{playerId}/speed")]
        public async Task<SpeedCompResult> CheckPlayerSpeedAdvantage(string playerId)
        {
            var minTime = DateTime.Now.Subtract(TimeSpan.FromMinutes(120));
            if(!long.TryParse(playerId, out long numeric))
                numeric = service.GetId(playerId);
            Console.WriteLine("checking flip timing for " + numeric);
            var relevantFlips = await db.FlipEvents.Where(flipEvent => 
                        flipEvent.Type == FlipEventType.AUCTION_SOLD 
                        && flipEvent.PlayerId == numeric 
                        && flipEvent.Timestamp > minTime)
                .ToListAsync();
            if (relevantFlips.Count == 0)
                return new SpeedCompResult();

            var ids = relevantFlips.Select(f => f.AuctionId).ToHashSet();
            Console.WriteLine("gettings clicks " + ids.Count());

            var receiveList = await db.FlipEvents.Where(f => ids.Contains(f.AuctionId) && f.PlayerId == numeric && f.Type == FlipEventType.FLIP_CLICK).ToDictionaryAsync(f=>f.AuctionId);
            
            var timeDif = relevantFlips.Where(f=>receiveList.ContainsKey(f.AuctionId)).Select(f =>
            {
                var receive = receiveList[f.AuctionId];
                return (receive.Timestamp - f.Timestamp).TotalSeconds;
            });
            var avg = timeDif.Where(d=>d<8).Average();

            return new SpeedCompResult()
            {
               // Clicks = clicks,
                Buys = relevantFlips.ToDictionary(f => f.AuctionId, f => f.Timestamp),
                Timings = timeDif,
                AvgAdvantageSeconds = avg,
                AvgAdvantage = TimeSpan.FromSeconds(avg),
                Penalty = TimeSpan.FromSeconds(avg) - TimeSpan.FromSeconds(2.9),
            };
        }

        public class SpeedCompResult
        {
            public Dictionary<long, List<DateTime>> Clicks { get; set; }
            public Dictionary<long, DateTime> Buys { get; set; }
            public TimeSpan AvgAdvantage { get; set; }
            public TimeSpan Penalty { get; set; }
            public double AvgAdvantageSeconds { get; set; }
            public IEnumerable<double> Timings { get; set; }
        }
    }
}
