using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SteamTracker.Core.Entities;

namespace SteamTracker.Infrastructure.Persistence.Configurations;

public class AlertConfiguration : IEntityTypeConfiguration<Alert>
{
    public void Configure(EntityTypeBuilder<Alert> builder)
    {
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Destination)
            .IsRequired()
            .HasMaxLength(500);

        // Money? (nullable): ver comentário equivalente em PriceSnapshotConfiguration.
        // A comparação de TargetPrice acontece em C# (Worker avalia o alerta),
        // não em SQL, então perder o filtro nativo por Amount não pesa aqui.
        builder.Property(a => a.TargetPrice)
            .HasConversion(m => MoneyJsonConversion.Serialize(m), j => MoneyJsonConversion.Deserialize(j))
            .HasColumnName("TargetPrice")
            .HasColumnType("jsonb");

        builder.HasIndex(a => a.TrackedItemId);
    }
}
