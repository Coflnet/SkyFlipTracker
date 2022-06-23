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

        private static HashSet<string> BadPlayers = new() { "dffa84d869684e81894ea2a355c40118" };
        private static HashSet<string> CoolMacroers = new() { "0a86231badba4dbdbe12a3e4a8838f80" };

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
        /// Returns how many user recently received a flip
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("/player/{playerId}/alternative")]
        public async Task<AltResult> GetAlt(string toCheck)
        {
            if (!long.TryParse(toCheck, out long numericId))
                numericId = service.GetId(toCheck);
            var minTime = DateTime.UtcNow - TimeSpan.FromDays(1);
            var relevantBuys = await db.FlipEvents.Where(flipEvent =>
                        flipEvent.Type == FlipEventType.AUCTION_SOLD
                        && numericId == flipEvent.PlayerId
                        && flipEvent.Timestamp > minTime
                        && flipEvent.Timestamp <= DateTime.Now)
                .ToListAsync();
            if (relevantBuys.Count == 0)
                return new AltResult();

            var ids = relevantBuys.Select(f => f.AuctionId).ToHashSet();

            var interestingList = await db.FlipEvents.Where(f => ids.Contains(f.AuctionId) && f.Type == FlipEventType.FLIP_RECEIVE)
                                .ToListAsync();

            var receiveMost = interestingList.GroupBy(f => f.PlayerId).OrderByDescending(f => f.Count()).FirstOrDefault();
            if (receiveMost == null)
                return new AltResult();

            return new AltResult()
            {
                PlayerId = receiveMost.Key,
                TargetReceived = receiveMost.Count(),
                BoughtCount = relevantBuys.Count(),
                SentOut = receiveMost,
                TargetBought = relevantBuys,

            };
        }


        /// <summary>
        /// Returns the speed advantage of a list of players (coresponding to the same account)
        /// </summary>
        [HttpPost]
        [Route("/players/speed")]
        public async Task<SpeedCompResult> CheckMultiAccountSpeed([FromBody] SpeedCheckRequest request)
        {
            var longMacroMultiplier = 30;
            // extended time that supposed macroers will be delayed over.
            var shortMacroMultiplier = 6;
            var maxAge = TimeSpan.FromMinutes(request.minutes == 0 ? 20 : request.minutes);
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

            int escrowedUserCount = await GetEscrowedUserCount(maxAge, maxTime, numeric, relevantFlips);

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
            var antiMacro = GetSpeedPenalty(maxAge * shortMacroMultiplier, timeDif.Where(t => t.TotalSeconds > 3.37 && t.TotalSeconds < 4 && t.age < maxAge * shortMacroMultiplier), 0.2);
            penaltiy = Math.Max(penaltiy, 0) + antiMacro;
            if (antiMacro > 0)
                penaltiy += 0.01 * escrowedUserCount;
            penaltiy += 0.01 * escrowedUserCount;

            var badIds = request.PlayerIds.Where(p => BadPlayers.Contains(p));
            penaltiy += (8 * badIds.Count());
            penaltiy += (request.PlayerIds.Where(p => CoolMacroers.Contains(p)).Any() ? 0.312345 : 0);

            return new SpeedCompResult()
            {
                // Clicks = clicks,
                BadIds = badIds,
                Buys = relevantFlips.GroupBy(f => f.AuctionId).Select(f => f.First()).ToDictionary(f => f.AuctionId, f => f.Timestamp),
                Timings = timeDif.Select(d => d.TotalSeconds),
                AvgAdvantageSeconds = avg,
                Penalty = penaltiy,
                Times = timeDif.Select(t => new Timing() { age = t.age.ToString(), TotalSeconds = t.TotalSeconds }),
                OutspeedUserCount = escrowedUserCount
            };
        }

        private async Task<int> GetEscrowedUserCount(TimeSpan maxAge, DateTime maxTime, IEnumerable<long> numeric, List<FlipEvent> relevantFlips)
        {
            var escrowRelevantTimeMin = maxTime - (maxAge);
            var recentIds = relevantFlips.Where(f => f.Timestamp > escrowRelevantTimeMin && f.Timestamp < maxTime).Select(f => f.AuctionId).ToHashSet();
            var escrowState = await db.FlipEvents.Where(f => recentIds.Contains(f.AuctionId)).ToListAsync();
            var escrowedUserCount = escrowState.Where(s => !numeric.Contains(s.PlayerId) && s.Type == FlipEventType.FLIP_CLICK && s.Timestamp < relevantFlips.Where(f => f.AuctionId == s.AuctionId).Select(f => f.Timestamp).FirstOrDefault() + TimeSpan.FromSeconds(4)).Count();
            return escrowedUserCount;
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
                    avg = relevant.Average(d => (maxAge - d.age) / (maxAge) * (d.TotalSeconds - 3.03));
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
            public IEnumerable<string> BadIds { get; set; }
            public int OutspeedUserCount { get; internal set; }
        }

        public class Timing
        {
            public double TotalSeconds { get; set; }
            public string age { get; set; }
            public bool Tfm { get; set; }
        }
    }
}
