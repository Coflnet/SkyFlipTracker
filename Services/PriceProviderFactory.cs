using Coflnet.Sky.Crafts.Client.Api;
using Coflnet.Sky.Api.Client.Api;
using System.Threading.Tasks;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.SkyAuctionTracker.Services;
public interface IPriceProvider
{
    Task<PastFlip.ProfitChange> CostOf(string item, string title, long amount = 1, Dictionary<string, string> filters = null);

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
    private readonly Bazaar.Client.Api.IBazaarApi bazaarApi;
    private DateTime lastRefresh = DateTime.MinValue;
    private Dictionary<string, double> prices = new();
    private Dictionary<string, Queue<double>> priceHistory = new();
    private readonly ILogger<PriceProviderFactory> logger;


    public PriceProviderFactory(IPlayerApi playerApi,
                                IPricesApi pricesApi,
                                ICraftsApi craftsApi,
                                IAuctionsApi auctionsApi,
                                Bazaar.Client.Api.IBazaarApi bazaarApi,
                                ILogger<PriceProviderFactory> logger)
    {
        this.playerApi = playerApi;
        this.pricesApi = pricesApi;
        this.craftsApi = craftsApi;
        this.auctionsApi = auctionsApi;
        this.bazaarApi = bazaarApi;
        this.logger = logger;
    }

    public IPriceProvider Create(Core.SaveAuction auction)
    {
        DoRefresh();
        return new PriceProvider(playerApi, pricesApi, auction, auctionsApi, craftsApi, prices);
    }

    private void DoRefresh()
    {
        Task.Run(async () =>
        {
            try
            {
                if (DateTime.Now.AddMinutes(-10) > lastRefresh)
                {
                    lastRefresh = DateTime.Now;
                    var priceList = await bazaarApi.GetAllPricesAsync();
                    foreach (var item in priceList)
                    {
                        if (!priceHistory.ContainsKey(item.ProductId))
                        {
                            priceHistory[item.ProductId] = new Queue<double>();
                        }
                        var history = priceHistory[item.ProductId];
                        history.Enqueue(item.BuyPrice);
                        if (history.Count > 30)
                            history.Dequeue();
                        prices[item.ProductId] = history.OrderBy(x => x).ElementAt(history.Count / 2);
                    }
                }
            }
            catch (System.Exception e)
            {
                logger.LogError(e, "Failed to refresh prices");
            }
        });
    }
}
