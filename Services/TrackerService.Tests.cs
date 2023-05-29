using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

public class TrackerServiceTests
{
    [Test]
    [TestCase("§7[Lvl 1] §6Bat", "[Lvl 60] Bat", "[Lvl 1->60] Bat")]
    [TestCase("[Lvl 30] Bat", "§7[Lvl 100] §6Bat", "§7[Lvl 30->100] §6Bat")]
    public void CheckLevelDisplay(string startName, string sellName, string expected)
    {
        var buy = new ApiSaveAuction()
        {
            ItemName = startName,
            FlatenedNBT = new() { { "exp", "500000" } },
            Tag = "PET_BAT"
        };
        var sell = new ApiSaveAuction()
        {
            ItemName = sellName,
            FlatenedNBT = new() { { "exp", "1000000.1" } },
            Tag = "PET_BAT"
        };
        var name = TrackerService.GetDisplayName(buy, sell);
        Assert.AreEqual(expected, name);
    }
}