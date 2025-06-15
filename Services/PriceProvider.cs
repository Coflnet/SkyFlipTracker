using Coflnet.Sky.SkyAuctionTracker.Models;
using System.Threading.Tasks;
using Coflnet.Sky.Crafts.Client.Api;
using Coflnet.Sky.Core;
using Coflnet.Sky.Api.Client.Api;
using Coflnet.Sky.Api.Client.Model;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

public class PriceProvider : IPriceProvider
{
    private readonly IPlayerApi playerApi;
    private readonly IPricesApi pricesApi;
    private readonly ICraftsApi craftsApi;
    private readonly Dictionary<string, double> priceCache;
    private readonly IAuctionsApi auctionsApi;
    private readonly Core.SaveAuction auction;

    public PriceProvider(IPlayerApi playerApi, IPricesApi pricesApi, Core.SaveAuction auction, IAuctionsApi auctionsApi, ICraftsApi craftsApi, Dictionary<string, double> prices)
    {
        this.playerApi = playerApi;
        this.pricesApi = pricesApi;
        this.auction = auction;
        this.auctionsApi = auctionsApi;
        this.craftsApi = craftsApi;
        this.priceCache = prices;
    }


    public async Task<PastFlip.ProfitChange> CostOf(string item, string title, long amount = 1, Dictionary<string, string> filters = null)
    {
        if (item == "MOVE_JERRY")
            return new PastFlip.ProfitChange()
            {
                Label = title,
                Amount = -1
            };
        if (item == "SKYBLOCK_COIN")
            return new PastFlip.ProfitChange()
            {
                Label = title,
                Amount = -amount
            };
        if (priceCache.TryGetValue(item, out var price))
        {
            return new PastFlip.ProfitChange()
            {
                Label = title,
                Amount = -(long)(price * amount)
            };
        }
        List<BidResult> playerBidsResult = await GetPlayerBids(item, filters);
        foreach (var bidSample in playerBidsResult)
        {
            if (bidSample.HighestBid != bidSample.HighestOwnBid)
                continue;
            if (amount == 1)
                return new PastFlip.ProfitChange()
                {
                    Label = title,
                    ContextItemId = AuctionService.Instance.GetId(bidSample.AuctionId),
                    Amount = -bidSample.HighestOwnBid
                };
            var auctionDetails = await auctionsApi.ApiAuctionAuctionUuidGetAsync(bidSample.AuctionId);
            if (auctionDetails == null)
                continue;
            if (auctionDetails.Count == amount)
                return new PastFlip.ProfitChange()
                {
                    Label = title,
                    ContextItemId = AuctionService.Instance.GetId(bidSample.AuctionId),
                    Amount = -auctionDetails.HighestBidAmount
                };
        }
        filters?.Remove("EndAfter");
        filters?.Remove("tag");
        var itemPrice = await pricesApi.ApiItemPriceItemTagGetAsync(item, filters)
                    ?? throw new Exception($"Failed to find price for {item}");
        var median = itemPrice.Median;
        if (itemPrice.Max == 0 && item.StartsWith("ENCHANTMENT"))
        {
            // get lvl 1 and scale up, sample ENCHANTMENT_ULTIMATE_CHIMERA_4
            var lvl1Price = await pricesApi.ApiItemPriceItemTagGetAsync(item.Substring(0, item.LastIndexOf('_') + 1) + "1");
            median = lvl1Price.Median * int.Parse(item.Substring(item.LastIndexOf('_') + 1));
        }
        else if (itemPrice.Median == 500_000_000)
        {
            var allCrafts = await craftsApi.GetAllAsync();
            median = (long)(allCrafts.Where(c => c.ItemId == item).FirstOrDefault()?.CraftCost ?? 500_000_000);
        }

        return new PastFlip.ProfitChange()
        {
            Label = title,
            Amount = -(long)median * amount
        };
    }

    private async Task<List<BidResult>> GetPlayerBids(string item, Dictionary<string, string> filters)
    {
        filters ??= new Dictionary<string, string>();
        filters["tag"] = item;
        filters["EndAfter"] = DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeSeconds().ToString();
        try
        {
            return await playerApi.ApiPlayerPlayerUuidBidsGetAsync(auction.AuctioneerId, 0, filters);
        }
        catch (System.Exception e)
        {
            Console.WriteLine($"getting bids from {auction.AuctioneerId} for {item}" + e);
            return new List<BidResult>();
        }
    }
}
