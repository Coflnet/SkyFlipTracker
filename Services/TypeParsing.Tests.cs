using Coflnet.Sky.Api.Client.Model;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

public class TypeParsingTest
{
    [Test]
    public void Test()
    {
        var original = new Core.SaveAuction() { Reforge = Core.ItemReferences.Reforge.aote_stone };
        var parsed = JsonConvert.DeserializeObject<ColorSaveAuction>(JsonConvert.SerializeObject(original));
        Assert.AreEqual(Reforge.AoteStone, parsed.Reforge);
    }
}