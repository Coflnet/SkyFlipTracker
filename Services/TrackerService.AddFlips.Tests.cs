using System;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

internal class TrackerServiceAddFlipsTests
{
    [Test]
    public async Task AddFlips_MatchesDuplicatesByAuctionAndFinder()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new TrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        // Synthetic batch identities, unrelated to any historical incident.
        var saved = CreateFlip(101, LowPricedAuction.FinderType.SNIPER, 2_000_000);
        db.Flips.Add(saved);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var duplicate = CreateFlip(101, saved.FinderType, 2_100_000);
        var otherAuction = CreateFlip(102, saved.FinderType, 3_000_000);
        var otherFinder = CreateFlip(101, LowPricedAuction.FinderType.TFM, 2_200_000);
        var tracker = new TrackerService(db, NullLogger<TrackerService>.Instance,
            null, null, null, null, null, null, null, null, null, null, null, null);

        await tracker.AddFlips(new[] { duplicate, otherAuction, otherFinder });

        db.ChangeTracker.Clear();
        var rows = await db.Flips.OrderBy(f => f.AuctionId).ThenBy(f => f.FinderType).ToListAsync();
        Assert.That(rows.Select(f => (f.AuctionId, f.FinderType, f.TargetPrice, f.Timestamp)),
            Is.EquivalentTo(new[] { saved, otherAuction, otherFinder }
                .Select(f => (f.AuctionId, f.FinderType, f.TargetPrice, f.Timestamp))));
    }

    private static Flip CreateFlip(long auctionId, LowPricedAuction.FinderType finder, int targetPrice)
    {
        return new Flip
        {
            AuctionId = auctionId,
            FinderType = finder,
            TargetPrice = targetPrice,
            Timestamp = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc)
        };
    }
}
