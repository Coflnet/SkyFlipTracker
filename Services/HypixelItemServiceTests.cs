using System.Net.Http;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

public class HypixelItemServiceTests
{
    [Test]
    public void Basic()
    {
        var logger = new Logger<HypixelItemService>(LoggerFactory.Create(builder => builder.AddConsole()));
        var hypixelItemService = new HypixelItemService(new HttpClient(), logger);
        var costs = hypixelItemService.GetSlotCost("DAEDALUS_AXE", new List<string>() { "COMBAT_0" }, new List<string>() { "COMBAT_0", "COMBAT_1" }).Result;

        Assert.AreEqual(5, costs.Count());
        Assert.AreEqual(100_000, costs.First().Coins);
        Assert.AreEqual(40, costs.Last().Amount);
        Assert.AreEqual("FINE_AMETHYST_GEM", costs.Last().ItemId);
    }
}
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member