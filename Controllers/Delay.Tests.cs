using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Controllers
{
    public class DelayTests
    {
        [Test]
        public void DecreasesOverTime()
        {
            double avg = 0;
            double penaltiy = AnalyseController.CalculatePenalty(
                new Models.SpeedCheckRequest() { PlayerIds = new List<string>() },
                TimeSpan.FromMinutes(20),
                new List<(double TotalSeconds, TimeSpan age)>()
                {
                    (1.422106, TimeSpan.FromMinutes(112))
                }, 0, ref avg, 0, new List<string>());
            Assert.AreEqual(0, penaltiy);
        }
    }
}
