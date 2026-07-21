using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SteamTracker.Core.Entities;

namespace SteamTracker.Infrastructure.Persistence.Configurations;

public class TrackedItemConfiguration : IEntityTypeConfiguration<TrackedItem>
{
    public void Configure(EntityTypeBuilder<TrackedItem> builder)
    {
        builder.HasKey(t => t.Id);

        builder.Property(t => t.MarketHashName)
            .IsRequired()
            .HasMaxLength(255);

        builder.Property(t => t.DisplayName)
            .IsRequired()
            .HasMaxLength(255);

        // Mesmo item (appid + market_hash_name) não deve ser rastreado duas vezes.
        builder.HasIndex(t => new { t.AppId, t.MarketHashName }).IsUnique();

        builder.HasMany(t => t.Snapshots)
            .WithOne(s => s.TrackedItem)
            .HasForeignKey(s => s.TrackedItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(t => t.Alerts)
            .WithOne(a => a.TrackedItem)
            .HasForeignKey(a => a.TrackedItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
