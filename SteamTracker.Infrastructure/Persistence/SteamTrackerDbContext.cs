using Microsoft.EntityFrameworkCore;
using SteamTracker.Core.Entities;

namespace SteamTracker.Infrastructure.Persistence;

public class SteamTrackerDbContext(DbContextOptions<SteamTrackerDbContext> options) : DbContext(options)
{
    public DbSet<TrackedItem> TrackedItems => Set<TrackedItem>();
    public DbSet<PriceSnapshot> PriceSnapshots => Set<PriceSnapshot>();
    public DbSet<Alert> Alerts => Set<Alert>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SteamTrackerDbContext).Assembly);
    }
}
