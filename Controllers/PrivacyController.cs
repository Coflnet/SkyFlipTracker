using System.Threading.Tasks;
using Coflnet.Sky.SkyAuctionTracker.Services;
using Microsoft.AspNetCore.Mvc;

namespace Coflnet.Sky.SkyAuctionTracker.Controllers;

/// <summary>
/// Internal GDPR export and erasure of a player's flips
/// </summary>
[ApiController]
[Route("privacy/player")]
public class PrivacyController : ControllerBase
{
    private readonly PrivacyService service;

    public PrivacyController(PrivacyService service)
    {
        this.service = service;
    }

    [HttpGet("{uuid}")]
    public async Task<ActionResult<PrivacyService.PlayerExport>> Export(string uuid)
    {
        if (!Guid.TryParse(uuid, out var player))
            return BadRequest("malformed uuid");
        return await service.Export(player);
    }

    [HttpPost("{uuid}/erase")]
    public async Task<ActionResult> Erase(string uuid, [FromBody] PrivacyService.PlayerExport export)
    {
        if (!Guid.TryParse(uuid, out var player))
            return BadRequest("malformed uuid");
        var result = await service.Erase(player, export);
        return result.Check switch
        {
            PrivacyService.EraseCheck.Mismatch => BadRequest("export does not belong to this player"),
            PrivacyService.EraseCheck.NotOptedOut => Conflict("player is not opted out, opt out first"),
            PrivacyService.EraseCheck.ScopeChanged => Conflict("data changed since the export, create a fresh export"),
            _ => Ok(new { deletedFlips = result.DeletedFlips })
        };
    }
}
