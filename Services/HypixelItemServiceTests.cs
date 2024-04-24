using System.Net.Http;
using Coflnet.Sky.Core.Services;
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

        Assert.That(costs.Count(), Is.EqualTo(5));
        Assert.That(costs.First().Coins, Is.EqualTo(100_000));
        Assert.That(costs.Last().Amount, Is.EqualTo(40));
        Assert.That(costs.Last().ItemId, Is.EqualTo("FINE_AMETHYST_GEM"));
    }
}
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member