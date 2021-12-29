using System.Threading.Tasks;
using Coflnet.Sky.SkyAuctionTracker.Models;
using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;

namespace Coflnet.Sky.SkyAuctionTracker.Services
{
    public class TrackerService
    {
        private TrackerDbContext db;

        public TrackerService(TrackerDbContext db)
        {
            this.db = db;
        }

        public async Task<Flip> AddFlip(Flip flip)
        {
            if (flip.Timestamp == default)
            {
                flip.Timestamp = DateTime.Now;
            }
            var flipAlreadyExists = await db.Flips.Where(f => f.AuctionId == flip.AuctionId && f.FinderType == flip.FinderType).AnyAsync();
            if (flipAlreadyExists)
            {
                return flip;
            }
            db.Flips.Add(flip);
            await db.SaveChangesAsync();
            return flip;
        }

        public async Task AddFlips(IEnumerable<Flip> flipsToSave)
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    var flips = flipsToSave.ToList();
                    var lookup = flips.Select(f => f.AuctionId).ToHashSet();
                    await db.Database.BeginTransactionAsync();
                    var existing = await db.Flips.Where(f => lookup.Contains(f.AuctionId)).ToListAsync();
                    var newFlips = flips.Where(f => !existing.Where(ex => f.AuctionId ==  f.AuctionId && ex.FinderType  == f.FinderType ).Any()).ToList();
                    db.Flips.AddRange(newFlips);
                    await db.SaveChangesAsync();
                    await db.Database.CommitTransactionAsync();
                    break;
                }
                catch (Exception e)
                {
                    dev.Logger.Instance.Error(e,"saving flips");
                    Console.WriteLine("saving failed");
                    await Task.Delay(500);
                }
            }
        }

        public async Task<FlipEvent> AddEvent(FlipEvent flipEvent)
        {
            if (flipEvent.Timestamp == default)
            {
                flipEvent.Timestamp = DateTime.Now;
            }
            var flipEventAlreadyExists = await db.FlipEvents.Where(f => f.AuctionId == flipEvent.AuctionId && f.Type == flipEvent.Type && f.PlayerId == flipEvent.PlayerId)
                    .AnyAsync();
            if (flipEventAlreadyExists)
            {
                return flipEvent;
            }
            db.FlipEvents.Add(flipEvent);
            await db.SaveChangesAsync();
            return flipEvent;
        }
    }
}
