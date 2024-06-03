using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.SkyAuctionTracker.Models
{
    /// <summary>
    /// Result for finding an alt
    /// </summary>
    public class AltResult
    {
        /// <summary>
        /// The most likely alt
        /// </summary>
        public string PlayerId { get; set; }
        /// <summary>
        /// How many flips where sent to the supposed alt
        /// </summary>
        public int TargetReceived { get; internal set; }
        /// <summary>
        /// Amount of flips bought
        /// </summary>
        public int BoughtCount { get; internal set; }
        /// <summary>
        /// How many flips were bought by the checked player
        /// </summary>
        public int SelfBought { get; internal set; }
        /// <summary>
        /// Which flips were bought
        /// </summary>
        public IGrouping<long, FlipEvent> SentOut { get; set; }
        /// <summary>
        /// 
        /// </summary>
        public IEnumerable<FlipEvent> TargetBought { get; set; }
        public List<TimeDiff> TimeDiffs { get; internal set; }
    }

    public class TimeDiff
    {
        public string AuctionId { get; set; }
        public float TimeDiffrence { get; set; }
        public bool WasBed { get; set; }
    }
}