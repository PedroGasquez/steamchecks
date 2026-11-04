using Microsoft.EntityFrameworkCore;
using SteamTracker.Core.Entities;
using SteamTracker.Core.Enums;
using SteamTracker.Core.ValueObjects;
using SteamTracker.Infrastructure.Persistence.Repositories;
using SteamTracker.Tests.Support;

namespace SteamTracker.Tests.Persistence;

public class RepositoryTests
{
    private static TrackedItem NovoItem(string nome = "AK-47 | Redline (Field-Tested)", int appId = 730)
        => new(appId, nome, nome, ItemType.Skin);

    [Fact]
    public async Task GetActiveAsync_IgnoraItensInativos()
    {
        using var db = new SqliteTestDatabase();
        var ativo = NovoItem("Item A");
        var inativo = NovoItem("Item B");
        inativo.Deactivate();

        using (var ctx = db.CreateContext())
        {
            var repo = new TrackedItemRepository(ctx);
            await repo.AddAsync(ativo);
            await repo.AddAsync(inativo);
        }

        using var leitura = db.CreateContext();
        var resultado = await new TrackedItemRepository(leitura).GetActiveAsync();

        var unico = Assert.Single(resultado);
        Assert.Equal(ativo.Id, unico.Id);
    }

    [Fact]
    public async Task ExistsAsync_MesmoAppIdENome_RetornaTrue()
    {
        using var db = new SqliteTestDatabase();
        using (var ctx = db.CreateContext())
            await new TrackedItemRepository(ctx).AddAsync(NovoItem("Item A"));

        using var leitura = db.CreateContext();
        var repo = new TrackedItemRepository(leitura);

        Assert.True(await repo.ExistsAsync(730, "Item A"));
        Assert.False(await repo.ExistsAsync(730, "Item B"));
        Assert.False(await repo.ExistsAsync(440, "Item A"));
    }

    [Fact]
    public async Task GetByIdAsync_IdInexistente_RetornaNull()
    {
        using var db = new SqliteTestDatabase();
        using var ctx = db.CreateContext();

        Assert.Null(await new TrackedItemRepository(ctx).GetByIdAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetByItemAsync_OrdenaDoMaisRecenteERespeitaLimit()
    {
        using var db = new SqliteTestDatabase();
        var item = NovoItem();
        var baseTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        using (var ctx = db.CreateContext())
        {
            await new TrackedItemRepository(ctx).AddAsync(item);
            // Fora de ordem de propósito, pra provar que a ordenação vem do CapturedAt.
            var offsets = new[] { 1, 3, 2 };
            foreach (var h in offsets)
            {
                var snap = new PriceSnapshot(item.Id, new Money(10m + h, "BRL"), null, h);
                ctx.Entry(snap).Property(s => s.CapturedAt).CurrentValue = baseTime.AddHours(h);
                await new PriceSnapshotRepository(ctx).AddAsync(snap);
            }
        }

        using var leitura = db.CreateContext();
        var resultado = await new PriceSnapshotRepository(leitura).GetByItemAsync(item.Id, 2);

        Assert.Equal(2, resultado.Count);
        Assert.True(resultado[0].CapturedAt > resultado[1].CapturedAt);
        Assert.Equal(baseTime.AddHours(3), resultado[0].CapturedAt);
        Assert.Equal(baseTime.AddHours(2), resultado[1].CapturedAt);
    }

    [Fact]
    public async Task PriceSnapshot_IdaEVoltaNoBanco_PreservaMoneyObrigatorioEOpcional()
    {
        using var db = new SqliteTestDatabase();
        var item = NovoItem();

        using (var ctx = db.CreateContext())
        {
            await new TrackedItemRepository(ctx).AddAsync(item);
            await new PriceSnapshotRepository(ctx).AddAsync(
                new PriceSnapshot(item.Id, new Money(11.42m, "BRL"), new Money(12m, "BRL"), 1234));
        }

        using var leitura = db.CreateContext();
        var lido = Assert.Single(await new PriceSnapshotRepository(leitura).GetByItemAsync(item.Id, 10));

        Assert.Equal(new Money(11.42m, "BRL"), lido.LowestPrice);
        Assert.Equal(new Money(12m, "BRL"), lido.MedianPrice);
        Assert.Equal(1234, lido.Volume);
    }

    [Fact]
    public async Task PriceSnapshot_MedianPriceNulo_VoltaNulo()
    {
        using var db = new SqliteTestDatabase();
        var item = NovoItem();

        using (var ctx = db.CreateContext())
        {
            await new TrackedItemRepository(ctx).AddAsync(item);
            await new PriceSnapshotRepository(ctx).AddAsync(
                new PriceSnapshot(item.Id, new Money(5m, "BRL"), null, null));
        }

        using var leitura = db.CreateContext();
        var lido = Assert.Single(await new PriceSnapshotRepository(leitura).GetByItemAsync(item.Id, 10));

        Assert.Null(lido.MedianPrice);
        Assert.Null(lido.Volume);
    }

    [Fact]
    public async Task AddAsync_ItemDuplicado_LancaDbUpdateException()
    {
        using var db = new SqliteTestDatabase();
        using (var ctx = db.CreateContext())
            await new TrackedItemRepository(ctx).AddAsync(NovoItem("Item A"));

        using var outro = db.CreateContext();
        await Assert.ThrowsAsync<DbUpdateException>(
            () => new TrackedItemRepository(outro).AddAsync(NovoItem("Item A")));
    }
}
