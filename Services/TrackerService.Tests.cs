using FluentAssertions;
using Newtonsoft.Json;
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
        Assert.That(name, Is.EqualTo(expected));
    }

    [Test]
    public void ParseTradeItem()
    {
        var json = """
        {"Id":null,"ItemName":"§dJaded Helmet of Divan §4✦","Tag":"DIVAN_HELMET","ExtraAttributes":{"rarity_upgrades":1,
        "gems":{
            "JADE_1":{"uuid":"fc33c0c2-4611-46ef-b026-40d70a362998","quality":"FLAWLESS"},
            "JADE_0":{"uuid":"32af71e6-bebb-4dfe-90a8-5d5d40bb3399","quality":"FLAWLESS"},
            "unlocked_slots":["AMBER_0","AMBER_1","JADE_0","JADE_1","TOPAZ_0"],
            "AMBER_0":{"uuid":"07a39430-816e-4f46-a571-4df9dfa6ed82","quality":"FLAWLESS"},
            "AMBER_1":{"uuid":"659f15fc-5b01-40f8-b903-7c9ca9fddd3f","quality":"FLAWLESS"},
            "TOPAZ_0":{"uuid":"74a569a4-fc86-4bfb-9f78-9c5e4c42bd33","quality":"FLAWLESS"}},
        "modifier":"jaded","skin":"GEMSTONE_DIVAN","favorite_gemstone":4,"uid":"a68fc0f858a8","uuid":"9a03e307-926b-44ee-8df4-a68fc0f858a8","timestamp":1706457319539,"tier":8},
        "Enchantments":null,"Color":null,
        "Description":"§7§8Gemstone Divan Helmet Skin\n\n§7Health: §a+100\n§7Defense: §a+130\n§7Mining Speed: §a+290 §9(+60) §d(+150)\n§7Mining Fortune: §a+130 §9(+30) §d(+70)\n§7Pristine: §a+1.6 §d(+1.6)\n §5[§6⸕§5] §5[§a☘§5] §5[§6⸕§5] §5[§a☘§5] §5[§e✧§5]\n\n§6Ability: Color Swapper  §e§lLEFT CLICK\n§7Swaps this helmet's skin through\n§7your favorite Gemstone colors!\n\n§7Selected: §6Amber Gemstone\n\n§7§4❣ §cRequires §5Heart of the Mountain Tier 6§c.\n§d§l§ka§r §d§l§d§lMYTHIC HELMET §d§l§ka","Count":1}
        """;
        var trade = JsonConvert.DeserializeObject<PlayerState.Client.Model.Item>(json);
        var service = new TrackerService(null, null, null,null,null,null,null,null,null,null,null,null,null);
        var auction = service.FromItemRepresent(trade);
        Assert.That(auction.ItemName, Is.EqualTo("§dJaded Helmet of Divan §4✦"));
        auction.FlatenedNBT.Should().Contain(new KeyValuePair<string, string>("AMBER_0", "FLAWLESS"));
    }
}