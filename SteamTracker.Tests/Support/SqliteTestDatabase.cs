using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SteamTracker.Infrastructure.Persistence;

namespace SteamTracker.Tests.Support;

/// <summary>
/// Banco SQLite em memória por teste. A conexão fica aberta enquanto o objeto
/// viver (o banco :memory: some ao fechar), e cada CreateContext() devolve um
/// contexto novo sobre o mesmo banco — assim escrita e leitura não dividem o
/// change tracker.
/// </summary>
public sealed class SqliteTestDatabase : IDisposable
{
    public SqliteConnection Connection { get; }

    public SqliteTestDatabase()
    {
        Connection = new SqliteConnection("DataSource=:memory:");
        Connection.Open();

        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();
    }

    public SteamTrackerDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SteamTrackerDbContext>()
            .UseSqlite(Connection)
            .Options;
        return new SteamTrackerDbContext(options);
    }

    public void Dispose() => Connection.Dispose();
}
