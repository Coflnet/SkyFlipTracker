using System;
using System.Collections.Generic;

namespace Coflnet.Sky.SkyAuctionTracker.Models
{
    public class SpeedCheckRequest
    {
        public IEnumerable<string> PlayerIds { get; set; }
        public DateTime when { get; set; } = default;
        public int minutes { get; set; } = 20;
    }
}