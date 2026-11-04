using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SteamTracker.Core.Abstractions;
using SteamTracker.Infrastructure.Persistence;
using SteamTracker.Tests.Fakes;

namespace SteamTracker.Tests.Support;

/// <summary>
/// Sobe a Api em memória trocando Npgsql por SQLite e a Steam por um fake.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public FakeSteamMarketClient Steam { get; } = new();
    public SqliteTestDatabase Database { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            // Sem remover as duas, o EF 9 acusa dois providers registrados.
            services.RemoveAll<DbContextOptions<SteamTrackerDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<SteamTrackerDbContext>>();
            services.AddDbContext<SteamTrackerDbContext>(o => o.UseSqlite(Database.Connection));

            services.RemoveAll<ISteamMarketClient>();
            services.AddSingleton<ISteamMarketClient>(Steam);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            Database.Dispose();
    }
}
