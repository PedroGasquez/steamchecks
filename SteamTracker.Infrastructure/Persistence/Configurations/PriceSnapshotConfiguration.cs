using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SteamTracker.Core.Entities;
using SteamTracker.Core.ValueObjects;

namespace SteamTracker.Infrastructure.Persistence.Configurations;

public class PriceSnapshotConfiguration : IEntityTypeConfiguration<PriceSnapshot>
{
    public void Configure(EntityTypeBuilder<PriceSnapshot> builder)
    {
        builder.HasKey(s => s.Id);

        // Money é um value object (readonly record struct) mapeado como
        // complex type — sem tabela própria, colunas embutidas em PriceSnapshot.
        builder.ComplexProperty(s => s.LowestPrice, money =>
        {
            money.Property(m => m.Amount).HasColumnName("LowestPriceAmount").HasPrecision(18, 2);
            money.Property(m => m.Currency).HasColumnName("LowestPriceCurrency").HasMaxLength(3);
        });

        // Money? (nullable): nem Complex Type (exige IsRequired — ver
        // github.com/dotnet/efcore/issues/31376) nem Owned Type (exige classe,
        // Money é struct) suportam propriedade opcional no EF Core 9. Guardamos
        // como jsonb em vez de duas colunas — perde filtro por Amount via SQL
        // direto, aceitável aqui porque MedianPrice é só exibido, não filtrado.
        builder.Property(s => s.MedianPrice)
            .HasConversion(
                money => money == null ? null : JsonSerializer.Serialize(money.Value, (JsonSerializerOptions?)null),
                json => json == null ? null : JsonSerializer.Deserialize<Money>(json, (JsonSerializerOptions?)null))
            .HasColumnName("MedianPrice")
            .HasColumnType("jsonb");

        // Consulta mais comum: histórico de um item ordenado no tempo.
        builder.HasIndex(s => new { s.TrackedItemId, s.CapturedAt });
    }
}
