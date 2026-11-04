using SteamTracker.Infrastructure;
using SteamTracker.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.Configure<PriceCollectionOptions>(builder.Configuration.GetSection("PriceCollection"));
builder.Services.AddScoped<PriceCollector>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
