using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Coflnet.Sky.Sniper.Client.Api;
using MessagePack;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RestSharp;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

public interface ISniperClient
{
    Task<List<Sniper.Client.Model.PriceEstimate>> GetPrices(IEnumerable<SaveAuction> auctionRepresent);
}
public class SniperClient : ISniperClient
{
    private RestSharp.RestClient sniperClient;
    private readonly ILogger<SniperClient> logger;

    public SniperClient(ISniperApi sniperApi, ILogger<SniperClient> logger)
    {
        sniperClient = new(sniperApi.GetBasePath());
        this.logger = logger;
    }
    public async Task<List<Sniper.Client.Model.PriceEstimate>> GetPrices(IEnumerable<SaveAuction> auctionRepresent)
    {
        var request = new RestRequest("/api/sniper/prices", RestSharp.Method.Post);
        var options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block);
        request.AddJsonBody(JsonConvert.SerializeObject(Convert.ToBase64String(MessagePackSerializer.Serialize(auctionRepresent, options))));

        var respone = await sniperClient.ExecuteAsync(request).ConfigureAwait(false);
        if (respone.StatusCode == 0)
        {
            logger.LogError("sniper service could not be reached");
            return auctionRepresent.Select(a => new Sniper.Client.Model.PriceEstimate()).ToList();
        }
        try
        {
            return JsonConvert.DeserializeObject<List<Sniper.Client.Model.PriceEstimate>>(respone.Content);
        }
        catch (System.Exception)
        {
            logger.LogError("responded with " + respone.StatusCode + respone.Content);
            throw;
        }
    }
}