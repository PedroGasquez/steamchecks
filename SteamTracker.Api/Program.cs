using SteamTracker.Core.Abstractions;
using SteamTracker.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapGet("/api/items/{appId:int}/{marketHashName}/price",
    async (int appId, string marketHashName, int currency, ISteamMarketClient client, CancellationToken cancellationToken) =>
    {
        var result = await client.GetPriceAsync(appId, marketHashName, currency, cancellationToken);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    })
    .WithName("GetItemPrice");

app.Run();
