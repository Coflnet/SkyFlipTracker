using System.Threading.Tasks;
using System.Reflection;
using Coflnet.Sky.Core;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Coflnet.Sky.SkyAuctionTracker.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Controllers;

public class InvestigationTests
{
    private const string OptedOut = "f3c19fb53ea940f3921e90faab8e2b30";

    [SetUp]
    public void SetUp() => PlayerOptOut.ResetMemory();
    [TearDown]
    public void TearDown() => PlayerOptOut.ResetMemory();

    [TestCase("GetFlip", "flip/{auctionUuid}")]
    [TestCase("GetPurchases", "player/{playerUuid}/purchases")]
    [TestCase("GetActivity", "player/{playerUuid}/activity")]
    [TestCase("GetHash", "id/{uuid}")]
    [TestCase("GetUuid", "hash/{hash}")]
    public void ExposesReadOnlyRoutes(string method, string template)
    {
        var action = typeof(InvestigationController).GetMethod(method);
        action.Should().NotBeNull();
        action.GetCustomAttributes<HttpGetAttribute>().Single().Template.Should().Be(template);
        typeof(InvestigationController).GetCustomAttribute<RouteAttribute>().Template.Should().Be("investigate");
    }

    [Test]
    public void ValidatesPurchasesQuery()
    {
        var uuid = Guid.NewGuid().ToString("N");
        InvestigationController.ValidatePurchases(uuid, 7, 100, "both", out _).Should().BeNull();
        InvestigationController.ValidatePurchases("nope", 7, 100, "both", out _).Should().NotBeNull();
        InvestigationController.ValidatePurchases(uuid, 0, 100, "both", out _).Should().NotBeNull();
        InvestigationController.ValidatePurchases(uuid, 31, 100, "both", out _).Should().NotBeNull();
        InvestigationController.ValidatePurchases(uuid, 7, 0, "both", out _).Should().NotBeNull();
        InvestigationController.ValidatePurchases(uuid, 7, 201, "both", out _).Should().NotBeNull();
        InvestigationController.ValidatePurchases(uuid, 7, 200, "events", out _).Should().BeNull();
        InvestigationController.ValidatePurchases(uuid, 7, 100, "x", out _).Should().NotBeNull();
    }

    [Test]
    public void ValidatesActivityQuery()
    {
        var uuid = Guid.NewGuid().ToString("N");
        InvestigationController.ValidateActivity(uuid, 30, out _).Should().BeNull();
        InvestigationController.ValidateActivity(uuid, 31, out _).Should().NotBeNull();
        InvestigationController.ValidateActivity("x", 7, out _).Should().NotBeNull();
    }

    [Test]
    public async Task OptedOutPlayersGet403()
    {
        // services are null: reaching them would throw
        var controller = new InvestigationController(null, null);
        (await controller.GetPurchases(OptedOut, 7, 100, "both")).Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        (await controller.GetActivity(OptedOut, 7)).Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        controller.GetHash(OptedOut).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    [Test]
    public async Task BadAuctionUuidIs400()
        => (await new InvestigationController(null, null).GetFlip("x")).Result.Should().BeOfType<BadRequestObjectResult>();

    [Test]
    public async Task UnknownHashIs404AndOptedOutHashIs404()
    {
        var storage = new Mock<FlipStorageService>(null, null, null);
        storage.Setup(s => s.LookupPlayerIds(It.IsAny<IEnumerable<long>>()))
            .ReturnsAsync(new Dictionary<long, Guid> { [2] = Guid.Parse(OptedOut), [3] = Guid.Parse("11111111111111111111111111111111") });
        var controller = new InvestigationController(null, storage.Object);
        (await controller.GetUuid(1)).Should().BeOfType<NotFoundResult>();
        (await controller.GetUuid(2)).Should().BeOfType<NotFoundResult>();
        (await controller.GetUuid(3)).Should().BeOfType<OkObjectResult>();
    }

    [Test]
    public void FunnelVerdictSerializesAsString()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new Models.Investigation.FlipFunnel { Verdict = Models.Investigation.FunnelVerdict.FoundNotSent },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        json.Should().Contain("\"verdict\":\"FoundNotSent\"");
    }

    /// <summary>
    /// EF throws from ToQueryString when a LINQ shape can't be translated; no connection is opened because the server version is explicit
    /// </summary>
    [Test]
    public void QueryShapesTranslateToSql()
    {
        var options = new DbContextOptionsBuilder<TrackerDbContext>()
            .UseMySql("Server=localhost;Database=none;Uid=x;Pwd=x", new MariaDbServerVersion(new Version(10, 11, 0)))
            .Options;
        using var db = new TrackerDbContext(options);
        var ids = new List<long> { 1, 2, 3 };
        var since = DateTime.UtcNow.AddDays(-7);
        var queries = new Dictionary<string, IQueryable>
        {
            ["events"] = InvestigationQueries.EventsOfAuction(db, 5),
            ["flips"] = InvestigationQueries.FlipsOfAuctions(db, ids),
            ["aggregates"] = InvestigationQueries.EventAggregates(db, ids),
            ["playerEvents"] = InvestigationQueries.PlayerEventsOnAuctions(db, ids, ids),
            ["soldOfAuctions"] = InvestigationQueries.SoldEventsOfAuctions(db, ids),
            ["soldEvents"] = InvestigationQueries.SoldEventsOfPlayers(db, ids, since, 1000),
            ["receiverCounts"] = InvestigationQueries.ReceiverCounts(db, ids),
            ["activity"] = InvestigationQueries.ActivityByDay(db, ids, since),
        };
        foreach (var (name, query) in queries)
        {
            var sql = query.ToQueryString();
            sql.Should().NotBeNullOrWhiteSpace(name);
        }
        queries["aggregates"].ToQueryString().Should().Contain("GROUP BY");
        queries["receiverCounts"].ToQueryString().Should().Contain("DISTINCT").And.Contain("GROUP BY");
    }
}
