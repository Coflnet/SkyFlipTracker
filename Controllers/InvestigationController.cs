using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Coflnet.Sky.SkyAuctionTracker.Models.Investigation;
using Coflnet.Sky.SkyAuctionTracker.Services;
using Microsoft.AspNetCore.Mvc;

namespace Coflnet.Sky.SkyAuctionTracker.Controllers;

/// <summary>
/// Internal investigation endpoints (cluster-internal, reached via port-forward, no auth like the other controllers)
/// </summary>
[ApiController]
[Route("investigate")]
public class InvestigationController : ControllerBase
{
    private readonly FlipInvestigationService service;
    private readonly FlipStorageService storage;

    public InvestigationController(FlipInvestigationService service, FlipStorageService storage)
    {
        this.service = service;
        this.storage = storage;
    }

    /// <summary>
    /// Found, sent, clicked, bought for one auction
    /// </summary>
    [HttpGet("flip/{auctionUuid}")]
    public async Task<ActionResult<FlipFunnel>> GetFlip(string auctionUuid, [FromQuery] bool includeOutsped = false)
    {
        if (!Guid.TryParse(auctionUuid, out var auction))
            return BadRequest("auctionUuid must be a uuid");
        return await service.GetFunnel(auction, includeOutsped);
    }

    /// <summary>
    /// Purchases of a player with the funnel result of each
    /// </summary>
    [HttpGet("player/{playerUuid}/purchases")]
    public async Task<ActionResult<PlayerPurchaseReport>> GetPurchases(string playerUuid, [FromQuery] int days = 7, [FromQuery] int limit = 100, [FromQuery] string source = "both")
    {
        var error = ValidatePurchases(playerUuid, days, limit, source, out var player);
        if (error != null)
            return BadRequest(error);
        if (PlayerOptOut.IsOptedOut(player))
            return StatusCode(403, "player opted out");
        return await service.GetPurchases(player, days, limit, source);
    }

    /// <summary>
    /// Recent event activity of a player
    /// </summary>
    [HttpGet("player/{playerUuid}/activity")]
    public async Task<ActionResult<PlayerActivity>> GetActivity(string playerUuid, [FromQuery] int days = 7)
    {
        var error = ValidateActivity(playerUuid, days, out var player);
        if (error != null)
            return BadRequest(error);
        if (PlayerOptOut.IsOptedOut(player))
            return StatusCode(403, "player opted out");
        return await service.GetActivity(player, days);
    }

    /// <summary>
    /// uuid to hash used in events
    /// </summary>
    [HttpGet("id/{uuid}")]
    public ActionResult GetHash(string uuid)
    {
        if (!Guid.TryParse(uuid, out var player))
            return BadRequest("uuid must be a uuid");
        if (PlayerOptOut.IsOptedOut(player))
            return StatusCode(403, "player opted out");
        return Ok(new { uuid = player.ToString("N"), hash = TrackerService.HashId(player.ToString("N")) });
    }

    /// <summary>
    /// hash to uuid, only known for players that bought or sold something recently
    /// </summary>
    [HttpGet("hash/{hash}")]
    public async Task<ActionResult> GetUuid(long hash)
    {
        var found = await storage.LookupPlayerIds([hash]);
        if (!found.TryGetValue(hash, out var uuid) || PlayerOptOut.IsOptedOut(uuid))
            return NotFound();
        return Ok(new { hash, uuid = uuid.ToString("N") });
    }

    internal static string ValidatePurchases(string playerUuid, int days, int limit, string source, out Guid player)
    {
        if (!Guid.TryParse(playerUuid, out player))
            return "playerUuid must be a uuid";
        if (days < 1 || days > 30)
            return "days must be between 1 and 30";
        if (limit < 1 || limit > 200)
            return "limit must be between 1 and 200";
        if (source != "both" && source != "pastflips" && source != "events")
            return "source must be both, pastflips or events";
        return null;
    }

    internal static string ValidateActivity(string playerUuid, int days, out Guid player)
    {
        if (!Guid.TryParse(playerUuid, out player))
            return "playerUuid must be a uuid";
        if (days < 1 || days > 30)
            return "days must be between 1 and 30";
        return null;
    }
}
