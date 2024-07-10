using Coflnet.Sky.Crafts.Client.Api;
using Coflnet.Sky.Api.Client.Api;
using System.Threading.Tasks;
using Coflnet.Sky.SkyAuctionTracker.Models;

namespace Coflnet.Sky.SkyAuctionTracker.Services;
public interface IPriceProvider
{
    Task<PastFlip.ProfitChange> CostOf(string item, string title, long amount = 1);

}

public interface IPriceProviderFactory
{
    IPriceProvider Create(Core.SaveAuction auction);
}
public class PriceProviderFactory : IPriceProviderFactory
{
    private readonly IPlayerApi playerApi;
    private readonly IPricesApi pricesApi;
    private readonly ICraftsApi craftsApi;
    private readonly IAuctionsApi auctionsApi;


    public PriceProviderFactory(IPlayerApi playerApi, IPricesApi pricesApi, ICraftsApi craftsApi, IAuctionsApi auctionsApi)
    {
        this.playerApi = playerApi;
        this.pricesApi = pricesApi;
        this.craftsApi = craftsApi;
        this.auctionsApi = auctionsApi;
    }

    public IPriceProvider Create(Core.SaveAuction auction)
    {
        return new PriceProvider(playerApi, pricesApi, auction, auctionsApi, craftsApi);
    }
}
