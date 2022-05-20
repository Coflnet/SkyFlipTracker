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
        /// Returns the speed advantage of a list of players (coresponding to the same account)
        /// </summary>
        [HttpPost]
        [Route("/players/speed")]
        public async Task<SpeedCompResult> CheckMultiAccountSpeed([FromBody] SpeedCheckRequest request)
        {
            var longMacroMultiplier = 30;
            var maxAge = TimeSpan.FromMinutes(request.minutes);
            var maxTime = DateTime.UtcNow;
            if (request.when != default)
                maxTime = request.when;
            var minTime = maxTime.Subtract(maxAge * longMacroMultiplier);

            var numeric = request.PlayerIds.Select(playerId =>
            {
                if (!long.TryParse(playerId, out long val))
                    val = service.GetId(playerId);
                return val;
            });

            var relevantFlips = await db.FlipEvents.Where(flipEvent =>
                        flipEvent.Type == FlipEventType.AUCTION_SOLD
                        && numeric.Contains(flipEvent.PlayerId)
                        && flipEvent.Timestamp > minTime
                        && flipEvent.Timestamp <= maxTime)
                .ToListAsync();
            if (relevantFlips.Count == 0)
                return new SpeedCompResult() { Penalty = -1 };

            var ids = relevantFlips.Select(f => f.AuctionId).ToHashSet();
            Console.WriteLine("gettings clicks " + ids.Count());

            var receiveList = await db.FlipEvents.Where(f => ids.Contains(f.AuctionId) && numeric.Contains(f.PlayerId) && f.Type == FlipEventType.FLIP_RECEIVE)
                                .GroupBy(f => f.AuctionId).Select(f => f.OrderBy(f => f.Timestamp).First()).ToDictionaryAsync(f => f.AuctionId);
            var relevantTfm = await db.Flips.Where(f => ids.Contains(f.AuctionId) && f.FinderType == LowPricedAuction.FinderType.TFM)
                                .GroupBy(f => f.AuctionId).Select(f => f.OrderBy(f => f.Timestamp).First()).ToDictionaryAsync(f => f.AuctionId);

            var timeDif = relevantFlips.Where(f => receiveList.ContainsKey(f.AuctionId) && !relevantTfm.ContainsKey(f.AuctionId)).Select(f =>
            {
                var receive = receiveList[f.AuctionId];
                return ((receive.Timestamp - f.Timestamp).TotalSeconds, age: maxTime - receive.Timestamp);
            });
            double avg = 0;
            double penaltiy = GetPenalty(maxAge, timeDif.Where(t => t.age < maxAge), ref avg);
            penaltiy += GetSpeedPenalty(maxAge * longMacroMultiplier, timeDif.Where(t => t.TotalSeconds > 3.35), 0.5);

            return new SpeedCompResult()
            {
                // Clicks = clicks,
                Buys = relevantFlips.GroupBy(f => f.AuctionId).Select(f => f.First()).ToDictionary(f => f.AuctionId, f => f.Timestamp),
                Timings = timeDif.Select(d => d.TotalSeconds),
                AvgAdvantageSeconds = avg,
                Penalty = penaltiy,
                Times = timeDif.Select(t => new Timing() { age = t.age.ToString(), TotalSeconds = t.TotalSeconds }),
            };
        }

        /// <summary>
        /// Returns the speed advantage of a player
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("/player/{playerId}/speed")]
        public async Task<SpeedCompResult> CheckPlayerSpeedAdvantage(string playerId, DateTime when = default, int minutes = 20)
        {
            return await CheckMultiAccountSpeed(new SpeedCheckRequest()
            {
                minutes = minutes,
                PlayerIds = new string[] { playerId },
                when = when
            });
        }

        public static double GetPenalty(TimeSpan maxAge, IEnumerable<(double TotalSeconds, TimeSpan age)> timeDif, ref double avg)
        {
            var penaltiy = 0d;
            if (timeDif.Count() != 0)
            {
                var relevant = timeDif.Where(d => d.TotalSeconds < 8 && d.TotalSeconds > 1);
                if (relevant.Count() > 0)
                    avg = relevant.Average(d => (maxAge - d.age) / (maxAge) * (d.TotalSeconds - 3.06));
                var tooFast = timeDif.Where(d => d.TotalSeconds > 3.3);
                var speedPenalty = GetSpeedPenalty(maxAge, tooFast);
                Console.WriteLine(avg + " " + speedPenalty);
                penaltiy = avg + speedPenalty;
            }

            return penaltiy;
        }

        private static double GetSpeedPenalty(TimeSpan maxAge, IEnumerable<(double TotalSeconds, TimeSpan age)> tooFast, double v = 0.2)
        {
            var shrink = 1;
            return tooFast.Where(f => f.age * shrink < maxAge).Select(f => (maxAge - f.age * shrink) / (maxAge) * v).Where(d => d > 0).Sum();
        }

        public class SpeedCompResult
        {
            public Dictionary<long, List<DateTime>> Clicks { get; set; }
            public Dictionary<long, DateTime> Buys { get; set; }
            public double Penalty { get; set; }
            public double AvgAdvantageSeconds { get; set; }
            public IEnumerable<double> Timings { get; set; }
            public IEnumerable<Timing> Times { get; set; }
        }

        public class Timing
        {
            public double TotalSeconds { get; set; }
            public string age { get; set; }
            public bool Tfm { get; set; }
        }
    }
}
