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

        private static readonly HashSet<string> BadPlayers = new() {
            "dffa84d869684e81894ea2a355c40118", // macroed for days
            "700ccbf05bc947bc9bfe4152b08a7e17", // skipped delay -- aka space
            "799dae2ecbb94c6ebf642727947b64d2", // ^ alt
            "d1acafbfd04644cdaabcffe508829c47", // skipped delay -- aka aestic
            "c051226ddfc643a6b49b5074ecd3a658", // ^ alt
            "fd490a7d6bbe4201b55b5239544a3dbe", // credit card fraud
            "961fc0c390b64687830b6e3ca6478433", // delay bypass
            "f3db2077e2434ef4867048ad60b6afed", // delay bapass to 3b9c200b2ba24036b3e900f31788c3f1
            "9b7f74f538f549baa1617ebd136c8305", "ee05104863684a0a85ec79c40e80f7f5", // delay bypass
            "50e3c3fbd4d746f7868652ed37e0a153", "e7b5ad30f6f94b3993db1d2fb37661b5", "326263423660400393310fcc9d0d826f", // more bypass :/
            "9b6cbecab4654b08b70d9817b885be24", "059056b17ea1467880f59350ad635589",
            "e709f7b92af9447ebc2c8ebc172a1b4f", "40d45b6f6bf9412580383269416e0cf2", "855fa79c6f9441b19fb507d088bb2dd9",  // connected
            "678712ab612b4a099ab261418e0da389", "c414a81db5104e1b9dc9e4b4fe5a7768", 
            "c57f8d117f414a4099cc3fccef5a34e7", "0f448f2df8a4401d9b2042d7435bbf30", "3a914807472944ce912ad1752866642f",
            "d61b636f545243a4814e66f595524e96", "be375613d04a4360b91b6751ee4d6ca7", 
            "d472ab290c0f4cbbaccefdce90176d32" // See https://discord.com/channels/267680588666896385/1006897388641853470/1011757951087820911
        };
        private static readonly HashSet<string> CoolMacroers = new() { };

        private static readonly TimeSpan shadowTiming = TimeSpan.FromDays(2);
        private static readonly int longMacroMultiplier = 80;
        // extended time that supposed macroers will be delayed over.
        private static readonly int shortMacroMultiplier = 6;

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
        [HttpPost]
        [Route("/flips/sent")]
        public async Task<IEnumerable<FlipWorthInfo>> GetSentFlipsWithin([FromBody] List<FlipTimeSelection> selections)
        {
            if (selections.Count == 0)
                return new List<FlipWorthInfo>();
            var minTime = selections.Min(s => s.Start);
            var maxTime = selections.Max(s => s.End);
            var playerIds = selections.Select(s => ParsePlayerId(s.PlayerId)).Distinct();
            var combinedQuery = db.FlipEvents.Where(flipEvent => flipEvent.Type == FlipEventType.FLIP_RECEIVE && flipEvent.Timestamp > minTime && playerIds.Contains(flipEvent.PlayerId) && flipEvent.Timestamp < maxTime);
            var allReceives = await combinedQuery.ToListAsync();
            logger.LogInformation("Found {0} flips sent from {1}", allReceives.Count, string.Join(',', playerIds.Select(p => p.ToString())));
            var result = new List<FlipWorthInfo>();
            var flipIds = allReceives.Select(r => r.AuctionId).Distinct();
            var allSentFlips = await db.Flips.Where(f => flipIds.Contains(f.AuctionId)).ToListAsync();
            logger.LogInformation("Found {0} flips sent", allSentFlips.Count);
            foreach (var selection in selections)
            {
                var numeric = ParsePlayerId(selection.PlayerId);
                var relevantReceives = allReceives.Where(r => r.PlayerId == numeric && r.Timestamp > selection.Start && r.Timestamp < selection.End);
                foreach (var item in relevantReceives)
                {
                    var flip = allSentFlips.FirstOrDefault(f => f.AuctionId == item.AuctionId);
                    if (flip == null)
                        continue;
                    result.Add(new FlipWorthInfo(selection.PlayerId, flip.TargetPrice, item.Timestamp, item.AuctionId.ToString()));
                }
            }
            logger.LogInformation("Found {0} flips worth {1} coins", result.Count, result.Sum(r => r.Worth));
            return result;
        }


        /// <summary>
        /// Requests the loading of a flip
        /// </summary>
        /// <param name="playerId"></param>
        /// <param name="uuids"></param>
        /// <param name="version"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("/flips/{playerId}/load")]
        public async Task CalculateFlips(Guid playerId, [FromBody] List<Guid> uuids, int version = 0)
        {
            await service.RefreshFlips(playerId, uuids, version);
        }

        /// <summary>
        /// Returns how many user recently received a flip
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("/player/{playerId}/alternative")]
        public async Task<AltResult> GetAlt(string playerId, double days = 1, DateTime when = default)
        {
            long numericId = ParsePlayerId(playerId);
            if (when == default)
                when = DateTime.UtcNow;
            var minTime = when - TimeSpan.FromDays(days);
            var relevantBuys = await db.FlipEvents.Where(flipEvent =>
                        flipEvent.Type == FlipEventType.AUCTION_SOLD
                        && numericId == flipEvent.PlayerId
                        && flipEvent.Timestamp > minTime
                        && flipEvent.Timestamp < when
                        && flipEvent.Timestamp <= DateTime.Now)
                .ToListAsync();
            if (relevantBuys.Count == 0)
                return new AltResult();

            var ids = relevantBuys.Select(f => f.AuctionId).ToHashSet();
            var buyTimes = relevantBuys.GroupBy(f => f.AuctionId).Select(f => f.First()).ToDictionary(f => f.AuctionId, f => f.Timestamp);

            var interestingList = await db.FlipEvents.Where(f => ids.Contains(f.AuctionId) && f.Type == FlipEventType.FLIP_RECEIVE)
                                .ToListAsync();

            var receiveMost = interestingList.Where(f => f.Timestamp - TimeSpan.FromSeconds(3.8) < buyTimes[f.AuctionId] && f.Timestamp + TimeSpan.FromSeconds(4) > buyTimes[f.AuctionId])
                                .GroupBy(f => f.PlayerId).OrderByDescending(f => f.Count()).FirstOrDefault();
            if (receiveMost == null)
                return new AltResult();

            var targetBought = await db.FlipEvents.Where(f => ids.Contains(f.AuctionId) && f.Type == FlipEventType.AUCTION_SOLD && f.PlayerId == receiveMost.Key)
                                .CountAsync();

            return new AltResult()
            {
                PlayerId = receiveMost.Key.ToString(),
                TargetReceived = receiveMost.Count(),
                BoughtCount = relevantBuys.Count(),
                SentOut = receiveMost,
                TargetBought = relevantBuys,
                SelfBought = targetBought
            };
        }

        private long ParsePlayerId(string playerId)
        {
            if (!long.TryParse(playerId, out long numericId))
                numericId = service.GetId(playerId);
            return numericId;
        }


        /// <summary>
        /// Returns the speed advantage of a list of players (coresponding to the same account)
        /// </summary>
        [HttpPost]
        [Route("/players/speed")]
        public async Task<SpeedCompResult> CheckMultiAccountSpeed([FromBody] SpeedCheckRequest request)
        {
            if (request.minutes > 300)
                throw new Exception("to long time span");
            var maxAge = TimeSpan.FromMinutes(request.minutes <= 0 ? 15 : request.minutes);
            var badIds = request.PlayerIds.Where(p => BadPlayers.Contains(p));
            var maxTime = DateTime.UtcNow;
            if (request.when != default)
                maxTime = request.when;
            var minTime = maxTime.Subtract(maxAge * longMacroMultiplier);

            var numeric = request.PlayerIds.Where(p => p != null).Select(playerId =>
            {
                if (string.IsNullOrEmpty(playerId))
                    throw new CoflnetException("invalid_player_id", "One of the player ids is invalid " + string.Join(',', request.PlayerIds));
                if (!long.TryParse(playerId, out long val))
                    val = service.GetId(playerId);
                return val;
            });

            var relevantFlips = await GetRelevantFlips(maxTime, minTime, numeric);
            if (relevantFlips.Count == 0)
            {
                var receivedCount = await db.FlipEvents.Where(flipEvent =>
                                    flipEvent.Type == FlipEventType.FLIP_RECEIVE
                                    && numeric.Contains(flipEvent.PlayerId)
                                    && flipEvent.Timestamp > minTime
                                    && flipEvent.Timestamp <= maxTime)
                            .CountAsync();
                return new SpeedCompResult() { Penalty = badIds.Any() ? 8 : -1, BadIds = badIds, ReceivedCount = receivedCount };
            }
            IEnumerable<(double TotalSeconds, TimeSpan age)> timeDif = (await GetTimings(maxTime, numeric, relevantFlips)).Where(t => t.TotalSeconds < 4);

            int escrowedUserCount = await GetEscrowedUserCount(maxAge, maxTime, numeric, relevantFlips);
            double avg = 0;
            var macroedFlips = timeDif.Where(t => t.TotalSeconds > 3.57 && t.TotalSeconds < 4).ToList();
            var macroedTimeDif = await GetMacroedFlipsLongTerm(shadowTiming, maxTime, numeric, macroedFlips);
            double antiMacro = GetShortTermAntiMacroDelay(maxAge, timeDif, macroedFlips.Where(t => t.age < maxAge * shortMacroMultiplier).ToList());

            double penaltiy = CalculatePenalty(request, maxAge, timeDif, escrowedUserCount, ref avg, antiMacro, badIds);
            int flipworth = await GetBoughtFlipsWorth(maxAge, maxTime, relevantFlips);

            return new SpeedCompResult()
            {
                // Clicks = clicks,
                BadIds = badIds,
                Buys = relevantFlips.GroupBy(f => f.AuctionId).Select(f => f.First()).ToDictionary(f => f.AuctionId, f => f.Timestamp),
                Timings = timeDif.Select(d => d.TotalSeconds),
                AvgAdvantageSeconds = avg,
                Penalty = penaltiy,
                Times = timeDif.Select(t => new Timing() { age = t.age.ToString(), TotalSeconds = t.TotalSeconds }),
                OutspeedUserCount = escrowedUserCount,
                MacroedFlips = macroedTimeDif,
                BoughtWorth = flipworth,
                TopBuySpeed = timeDif.Any() ? timeDif.Max(d => d.TotalSeconds) : 0
            };
        }

        private async Task<int> GetBoughtFlipsWorth(TimeSpan maxAge, DateTime maxTime, List<FlipEvent> relevantFlips)
        {
            try
            {
                return await db.Flips.Where(f => Auctionids(maxAge, maxTime, relevantFlips).Contains(f.AuctionId)).SumAsync(f => f.TargetPrice);
            }
            catch (System.Exception)
            {
                return 0;
            }
        }

        public static double CalculatePenalty(SpeedCheckRequest request, TimeSpan baseMaxAge, IEnumerable<(double TotalSeconds, TimeSpan age)> timeDif, int escrowedUserCount, ref double avg, double antiMacro, IEnumerable<string> badIds)
        {
            var penaltiy = 0d;
            var maxAge = baseMaxAge * 16;
            var relevantTimings = timeDif.Where(t => t.age < maxAge).ToList();
            if (relevantTimings.Count() != 0)
            {
                var relevant = relevantTimings.Where(d => d.TotalSeconds < 3.9 && d.TotalSeconds > 1);
                if (relevant.Count() > 0)
                    avg = GetAverageAdvantage(baseMaxAge, maxAge, relevant);
                var recentTooFast = relevantTimings.Where(t => t.age < baseMaxAge).Where(d => d.TotalSeconds > 3.3);
                var speedPenalty = GetSpeedPenalty(maxAge, recentTooFast);
                penaltiy = avg + speedPenalty;
            }

            penaltiy = Math.Max(penaltiy, 0) + antiMacro;
            if (antiMacro > 0)
                penaltiy += 0.02 * escrowedUserCount;
            penaltiy += 0.02 * escrowedUserCount;

            penaltiy += (8 * badIds.Count());
            penaltiy += (request.PlayerIds.Where(p => CoolMacroers.Contains(p)).Any() ? 0.312345 : 0);
            return penaltiy;
        }
        /// <summary>
        /// Calculates the average speed advantage, first drops of sharply within baseMaxAge but includes all flips until maxAge at 50%
        /// </summary>
        /// <param name="baseMaxAge"></param>
        /// <param name="maxAge"></param>
        /// <param name="relevant"></param>
        /// <returns></returns>
        private static double GetAverageAdvantage(TimeSpan baseMaxAge, TimeSpan maxAge, IEnumerable<(double TotalSeconds, TimeSpan age)> relevant)
        {
            var targetSpeed = 3.02;
            return (relevant.Average(d => (maxAge - d.age) / (maxAge) * (d.TotalSeconds - targetSpeed)) / 2
                + relevant.Where(t => t.age < baseMaxAge).Select(d => (baseMaxAge - d.age) / (baseMaxAge) * (d.TotalSeconds - targetSpeed)).DefaultIfEmpty(0).Average() / 2);
        }

        private static double GetShortTermAntiMacroDelay(TimeSpan maxAge, IEnumerable<(double TotalSeconds, TimeSpan age)> timeDif, List<(double TotalSeconds, TimeSpan age)> macroedFlips)
        {
            var antiMacro = GetSpeedPenalty(maxAge * shortMacroMultiplier, timeDif.Where(t => t.TotalSeconds > 3.37 && t.TotalSeconds < 4 && t.age < maxAge * shortMacroMultiplier), 0.2);
            antiMacro += GetSpeedPenalty(maxAge * shortMacroMultiplier, macroedFlips, 0.2);
            return antiMacro;
        }

        private async Task<IEnumerable<MacroedFlip>> GetMacroedFlipsLongTerm(TimeSpan shadowTiming, DateTime maxTime, IEnumerable<long> numeric, List<(double TotalSeconds, TimeSpan age)> macroedFlips)
        {
            IEnumerable<MacroedFlip> macroedTimeDif = new List<MacroedFlip>();
            if (macroedFlips.Count > 0)
            {
                var longTermFlips = await GetRelevantFlips(maxTime, maxTime - shadowTiming, numeric);
                var longTermTimeDif = await GetTimings(maxTime, numeric, longTermFlips);
                macroedTimeDif = longTermTimeDif.Where(t => t.TotalSeconds > 3.51 && t.TotalSeconds < 4).Select(f => new MacroedFlip() { TotalSeconds = f.TotalSeconds, BuyTime = DateTime.Now - f.age });
            }

            return macroedTimeDif;
        }

        private async Task<IEnumerable<(double TotalSeconds, TimeSpan age)>> GetTimings(DateTime maxTime, IEnumerable<long> numeric, List<FlipEvent> relevantFlips)
        {
            var ids = relevantFlips.Select(f => f.AuctionId).ToHashSet();


            var receiveList = await db.FlipEvents.Where(f => ids.Contains(f.AuctionId) && numeric.Contains(f.PlayerId) && f.Type == FlipEventType.FLIP_RECEIVE)
                                .GroupBy(f => f.AuctionId).Select(f => f.OrderBy(f => f.Timestamp).First()).ToDictionaryAsync(f => f.AuctionId);
            var relevantTfm = await db.Flips.Where(f => ids.Contains(f.AuctionId) && f.FinderType == LowPricedAuction.FinderType.TFM)
                                .GroupBy(f => f.AuctionId).Select(f => f.OrderBy(f => f.Timestamp).First()).ToDictionaryAsync(f => f.AuctionId);

            var timeDif = relevantFlips.Where(f => receiveList.ContainsKey(f.AuctionId) && !relevantTfm.ContainsKey(f.AuctionId)).Select(f =>
            {
                var receive = receiveList[f.AuctionId];
                return ((receive.Timestamp - f.Timestamp).TotalSeconds, age: maxTime - receive.Timestamp);
            });
            return timeDif;
        }

        private async Task<List<FlipEvent>> GetRelevantFlips(DateTime maxTime, DateTime minTime, IEnumerable<long> numeric)
        {
            var fullList = await db.FlipEvents.Where(flipEvent =>
                                    flipEvent.Type == FlipEventType.AUCTION_SOLD
                                    && numeric.Contains(flipEvent.PlayerId)
                                    && flipEvent.Timestamp > minTime
                                    && flipEvent.Timestamp <= maxTime)
                            .ToListAsync();
            return fullList.GroupBy(f => f.AuctionId).Select(f => f.First()).ToList();
        }

        private async Task<int> GetEscrowedUserCount(TimeSpan maxAge, DateTime maxTime, IEnumerable<long> numeric, List<FlipEvent> relevantFlips)
        {
            var recentIds = Auctionids(maxAge, maxTime, relevantFlips);
            var escrowState = await db.FlipEvents.Where(f => recentIds.Contains(f.AuctionId)).ToListAsync();
            var escrowedUserCount = escrowState.Where(s =>
            {
                var sellTimestamp = relevantFlips.Where(f => f.AuctionId == s.AuctionId).Select(f => f.Timestamp).FirstOrDefault();
                return !numeric.Contains(s.PlayerId) && s.Type == FlipEventType.FLIP_CLICK
                        && s.Timestamp < sellTimestamp + TimeSpan.FromSeconds(4)
                        && s.Timestamp > sellTimestamp + TimeSpan.FromSeconds(2.5);
            }).Count();
            return escrowedUserCount;
        }

        private static HashSet<long> Auctionids(TimeSpan maxAge, DateTime maxTime, List<FlipEvent> relevantFlips)
        {
            var escrowRelevantTimeMin = maxTime - (maxAge);
            var recentIds = relevantFlips.Where(f => f.Timestamp > escrowRelevantTimeMin && f.Timestamp < maxTime).Select(f => f.AuctionId).ToHashSet();
            return recentIds;
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
                    avg = relevant.Average(d => (maxAge - d.age) / (maxAge) * (d.TotalSeconds - 3.0));
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
            public int ReceivedCount { get; set; }

            public Dictionary<long, List<DateTime>> Clicks { get; set; }
            public Dictionary<long, DateTime> Buys { get; set; }
            public double Penalty { get; set; }
            public double AvgAdvantageSeconds { get; set; }
            public IEnumerable<double> Timings { get; set; }
            public IEnumerable<Timing> Times { get; set; }
            public IEnumerable<string> BadIds { get; set; }
            public int OutspeedUserCount { get; set; }
            public IEnumerable<MacroedFlip> MacroedFlips { get; set; }
            public long BoughtWorth { get; set; }
            public double TopBuySpeed { get; set; }
        }

        public class Timing
        {
            public double TotalSeconds { get; set; }
            public string age { get; set; }
            public bool Tfm { get; set; }
        }

        public class MacroedFlip
        {
            public double TotalSeconds { get; set; }
            public DateTime BuyTime { get; set; }
        }
    }
}
