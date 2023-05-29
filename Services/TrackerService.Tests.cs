using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

public class TrackerServiceTests
{
    [Test]
    public void CheckLevelDisplay()
    {
        var buy = new ApiSaveAuction()
        {
            ItemName = "[Lvl 30] Bat",
            FlatenedNBT = new() { { "exp", "500000" } },
            Tag = "PET_BAT"
        };
        var sell = new ApiSaveAuction()
        {
            ItemName = "[Lvl 60] Bat",
            FlatenedNBT = new() { { "exp", "1000000.1" } },
            Tag = "PET_BAT"
        };
        var name = TrackerService.GetDisplayName(buy, sell);
        Assert.AreEqual("[Lvl 30->60] Bat", name);
    }
}