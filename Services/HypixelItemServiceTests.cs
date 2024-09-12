using System.Net.Http;
using System.Threading.Tasks;
using Coflnet.Sky.Core.Services;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

public class HypixelItemServiceTests
{
    [Test]
    public async Task Basic()
    {
        var logger = new Logger<HypixelItemService>(LoggerFactory.Create(builder => builder.AddConsole()));
        var hypixelItemService = new HypixelItemService(new HttpClient(), logger);
        var costs = await hypixelItemService.GetSlotCost("DAEDALUS_AXE", new List<string>() { "COMBAT_0" }, new List<string>() { "COMBAT_0", "COMBAT_1" });

        Assert.That(costs.Count(), Is.EqualTo(5));
        Assert.That(costs.First().Coins, Is.EqualTo(100_000));
        Assert.That(costs.Last().Amount, Is.EqualTo(40));
        Assert.That(costs.Last().ItemId, Is.EqualTo("FINE_AMETHYST_GEM"));
    }
}
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member