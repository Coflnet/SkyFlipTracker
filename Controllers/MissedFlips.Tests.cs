using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Cassandra;
using Cassandra.Data.Linq;
using Cassandra.Mapping;
using Coflnet.Sky.Core;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Coflnet.Sky.SkyAuctionTracker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Controllers;

class MissedFlipsTests
{
    [Test]
    public void ExposesReadOnlyMissedFlipsRoute()
    {
        var action = typeof(TrackerController).GetMethod("GetMissedFlips");

        Assert.That(action, Is.Not.Null);
        Assert.That(action.GetCustomAttributes(typeof(HttpGetAttribute), false), Has.Length.EqualTo(1));
        Assert.That(action.GetCustomAttributes(typeof(RouteAttribute), false)
            .Cast<RouteAttribute>().Single().Template, Is.EqualTo("/flips/missed"));
    }

    [Test]
    public void CohortsUseExactZeroOrDefinedNonzeroPartitions()
    {
        var getPartitions = typeof(FlipStorageService).GetMethod(
            "GetMissedFlipFinderTypes", BindingFlags.Static | BindingFlags.NonPublic);

        var unknown = ((IEnumerable)getPartitions.Invoke(null, ["finder_unknown"]))
            .Cast<object>().Select(Convert.ToInt32).ToList();
        var blockedOrOutsped = ((IEnumerable)getPartitions.Invoke(null, ["blocked_or_outsped"]))
            .Cast<object>().Select(Convert.ToInt32).ToList();
        var expectedNonzero = Enum.GetValues<LowPricedAuction.FinderType>()
            .Select(finderType => Convert.ToInt32(finderType)).Where(value => value != 0).Distinct().Order().ToList();

        Assert.That(unknown, Is.EqualTo(new[] { 0 }));
        Assert.That(blockedOrOutsped, Is.EqualTo(expectedNonzero));
        Assert.That(blockedOrOutsped, Has.None.EqualTo(0));
    }

    [Test]
    public void MergesNewestFirstWithDeterministicTieBreakingAndGlobalLimit()
    {
        var merge = typeof(FlipStorageService).GetMethod(
            "MergeMissedFlips", BindingFlags.Static | BindingFlags.NonPublic);
        var newest = new DateTime(2026, 8, 31, 9, 0, 0, DateTimeKind.Utc);
        IEnumerable<IEnumerable<PastFlip>> partitions =
        [
            new[]
            {
                Flip(newest.AddMinutes(-1), 1, 10, "00000000-0000-0000-0000-000000000004"),
                Flip(newest.AddMinutes(-3), 1, 11, "00000000-0000-0000-0000-000000000005")
            },
            new[]
            {
                Flip(newest, 2, 2, "00000000-0000-0000-0000-000000000003"),
                Flip(newest, 1, 99, "00000000-0000-0000-0000-000000000002"),
                Flip(newest, 1, 1, "00000000-0000-0000-0000-000000000001")
            }
        ];

        var result = ((IEnumerable<PastFlip>)merge.Invoke(null, [partitions, 3])).ToList();

        Assert.That(result.Select(flip => flip.PurchaseAuctionId), Is.EqualTo(new[]
        {
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            Guid.Parse("00000000-0000-0000-0000-000000000003")
        }));
    }

    [TestCase(0)]
    [TestCase(51)]
    public void RejectsLimitsOutsideTheContract(int limit)
    {
        Assert.That(Validate(Utc(0), Utc(1), "finder_unknown", limit), Is.Not.Null);
    }

    [Test]
    public void ValidatesRequiredUtcRangeAndTwentyFourHourMaximum()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Validate(null, Utc(1), "finder_unknown", 1), Is.Not.Null);
            Assert.That(Validate(Utc(0), null, "finder_unknown", 1), Is.Not.Null);
            Assert.That(Validate(Utc(0).ToOffset(TimeSpan.FromHours(1)), Utc(1), "finder_unknown", 1), Is.Not.Null);
            Assert.That(Validate(Utc(1), Utc(1), "finder_unknown", 1), Is.Not.Null);
            Assert.That(Validate(Utc(1), Utc(0), "finder_unknown", 1), Is.Not.Null);
            Assert.That(Validate(Utc(0), Utc(25), "finder_unknown", 1), Is.Not.Null);
            Assert.That(Validate(Utc(0), Utc(24), "finder_unknown", 1), Is.Null);
            Assert.That(Validate(Utc(0), Utc(1), "blocked_or_outsped", 50), Is.Null);
            Assert.That(Validate(Utc(0), Utc(1), "blocked", 1), Is.Not.Null);
        });
    }

    [Test]
    public void DtoContainsOnlyTheApprovedShape()
    {
        var dtoType = typeof(PastFlip).Assembly.GetType(
            "Coflnet.Sky.SkyAuctionTracker.Models.MissedFlipDto");
        var profitChangeType = typeof(PastFlip).Assembly.GetType(
            "Coflnet.Sky.SkyAuctionTracker.Models.MissedFlipProfitChangeDto");
        var expected = new[]
        {
            "Cohort", "FinderType", "Flags", "ItemTag", "ItemTier", "Profit", "ProfitChanges",
            "PurchaseAuctionId", "PurchasePrice", "PurchaseTime", "SaleAuctionId", "SalePrice", "SaleTime", "TargetPrice"
        };

        Assert.That(dtoType.GetProperties().Select(property => property.Name).Order(), Is.EqualTo(expected.Order()));
        Assert.That(profitChangeType.GetProperties().Select(property => property.Name).Order(),
            Is.EqualTo(new[] { "Amount", "Label" }));
        Assert.That(dtoType.GetProperties().Select(property => property.Name),
            Has.None.Matches<string>(name => new[] { "Flipper", "Uid", "ItemName", "Timestamp", "ContextItemId" }.Contains(name)));

        var source = new PastFlip
        {
            Flipper = Guid.NewGuid(),
            Uid = 42,
            ItemName = "private display name",
            ItemTag = "TEST_TAG",
            PurchaseCost = 100,
            SellPrice = 150,
            ProfitChanges = [new PastFlip.ProfitChange
            {
                Label = "fee",
                Amount = -5,
                Timestamp = Utc(1).UtcDateTime,
                ContextItemId = 123
            }]
        };
        var map = dtoType.GetMethod("FromPastFlip", BindingFlags.Static | BindingFlags.NonPublic);
        var dto = map.Invoke(null, [source, "finder_unknown"]);
        var changes = ((IEnumerable)dtoType.GetProperty("ProfitChanges").GetValue(dto)).Cast<object>().Single();

        Assert.That(dtoType.GetProperty("PurchasePrice").GetValue(dto), Is.EqualTo(100));
        Assert.That(profitChangeType.GetProperty("Label").GetValue(changes), Is.EqualTo("fee"));
        Assert.That(profitChangeType.GetProperty("Amount").GetValue(changes), Is.EqualTo(-5));
    }

    [Test]
    public async Task MissedFlipQueriesBindExactIntegerPartitionsWithBoundsAndLimit()
    {
        using var cluster = Cluster.Builder().AddContactPoint("127.0.0.1").Build();
        var service = CreateStorageWithCapturedStatements(cluster, out var queries, out var statements);
        var start = Utc(0).UtcDateTime;
        var end = Utc(1).UtcDateTime;

        await service.GetMissedFlips(start, end, "finder_unknown", 7);
        await service.GetMissedFlips(start, end, "blocked_or_outsped", 7);

        var expectedPartitions = Enum.GetValues<LowPricedAuction.FinderType>()
            .Select(value => (int)value).Distinct().Order().ToList();
        var bindings = statements.Select(statement => statement.QueryValues).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(queries, Has.Count.EqualTo(1));
            Assert.That(queries.Single(), Does.Contain("WHERE FinderType = ?"));
            Assert.That(queries.Single(), Does.Contain("SellTime >= ?"));
            Assert.That(queries.Single(), Does.Contain("SellTime <= ?"));
            Assert.That(queries.Single(), Does.Contain("LIMIT ?"));
            Assert.That(queries.Single(), Does.Not.Contain("ALLOW FILTERING"));
            Assert.That(bindings.Select(values => values[0]).Order(), Is.EqualTo(expectedPartitions));
            Assert.That(bindings.Select(values => values[0]), Has.All.TypeOf<int>());
            Assert.That(bindings.Select(values => values[1]), Has.All.EqualTo(start));
            Assert.That(bindings.Select(values => values[2]), Has.All.EqualTo(end));
            Assert.That(bindings.Select(values => values[3]), Has.All.EqualTo(7));
        });
    }

    private static FlipStorageService CreateStorageWithCapturedStatements(
        ICluster cluster, out ConcurrentQueue<string> queries, out ConcurrentQueue<IStatement> statements)
    {
        var session = new Mock<ISession>();
        queries = new ConcurrentQueue<string>();
        statements = new ConcurrentQueue<IStatement>();
        var capturedQueries = queries;
        var capturedStatements = statements;
        session.SetupGet(value => value.Cluster).Returns(cluster);
        session.SetupGet(value => value.Keyspace).Returns("flips");
        session.Setup(value => value.PrepareAsync(It.IsAny<string>()))
            .Callback<string>(query => capturedQueries.Enqueue(query))
            .ReturnsAsync(new PreparedStatement());
        session.Setup(value => value.ExecuteAsync(It.IsAny<IStatement>(), It.IsAny<string>()))
            .Callback<IStatement, string>((statement, _) => capturedStatements.Enqueue(statement))
            .ReturnsAsync(new RowSet());
        var service = new FlipStorageService(
            Mock.Of<ILogger<FlipStorageService>>(), Mock.Of<IConfiguration>(), session.Object);
        var unknownFlips = new Table<PastFlip>(session.Object, new MappingConfiguration().Define(new Map<PastFlip>()
            .PartitionKey(flip => flip.FinderType)
            .ClusteringKey(flip => flip.SellTime, SortOrder.Descending)
            .ClusteringKey(flip => flip.Uid)
            .Column(flip => flip.FinderType, column => column.WithDbType<int>())), "unknown_flips2");
        typeof(FlipStorageService).GetField("unknownFlips", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(service, unknownFlips);
        return service;
    }

    private static PastFlip Flip(DateTime sellTime, int finderType, long uid, string purchaseAuctionId)
    {
        return new PastFlip
        {
            SellTime = sellTime,
            FinderType = (LowPricedAuction.FinderType)finderType,
            Uid = uid,
            PurchaseAuctionId = Guid.Parse(purchaseAuctionId)
        };
    }

    private static DateTimeOffset Utc(int hours)
    {
        return new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero).AddHours(hours);
    }

    private static string Validate(DateTimeOffset? start, DateTimeOffset? end, string cohort, int limit)
    {
        var validate = typeof(TrackerController).GetMethod(
            "ValidateMissedFlipsQuery", BindingFlags.Static | BindingFlags.NonPublic);
        return (string)validate.Invoke(null, [start, end, cohort, limit]);
    }

}
