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
        public long PlayerId { get; set; }
        /// <summary>
        /// How many flips where sent to the supposed alt
        /// </summary>
        public int TargetReceived { get; internal set; }
        /// <summary>
        /// Amount of flips bought
        /// </summary>
        public int BoughtCount { get; internal set; }
        /// <summary>
        /// Which flips were bought
        /// </summary>
        public IGrouping<long, FlipEvent> SentOut { get; set; }
        /// <summary>
        /// 
        /// </summary>
        public IEnumerable<FlipEvent> TargetBought { get; set; }
    }
}