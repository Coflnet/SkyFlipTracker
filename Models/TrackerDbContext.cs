using Microsoft.EntityFrameworkCore;

namespace Coflnet.Sky.SkyAuctionTracker.Models
{
    /// <summary>
    /// <see cref="DbContext"/> For flip tracking
    /// </summary>
    public class TrackerDbContext : DbContext
    {
        public DbSet<FlipEvent> FlipEvents { get; set; }
        public DbSet<Flip> Flips { get; set; }

        /// <summary>
        /// Creates a new instance of <see cref="TrackerDbContext"/>
        /// </summary>
        /// <param name="options"></param>
        public TrackerDbContext(DbContextOptions<TrackerDbContext> options)
        : base(options)
        {
        }

        /// <summary>
        /// Configures additional relations and indexes
        /// </summary>
        /// <param name="modelBuilder"></param>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<FlipEvent>(entity =>
            {
                entity.HasIndex(e => new { e.AuctionId, e.Type });
                entity.HasIndex(e => e.PlayerId);
                entity.HasIndex(e => e.Timestamp);
            });

            modelBuilder.Entity<Flip>(entity =>
            {
                entity.HasIndex(e => new { e.AuctionId });
                entity.HasIndex(e => e.Timestamp);
            });
        }
    }
}