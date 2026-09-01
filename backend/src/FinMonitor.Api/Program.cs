using System.Text.Json.Serialization;
using FinMonitor.Api.Endpoints;
using FinMonitor.Api.ExceptionHandling;
using FinMonitor.Api.Health;
using FinMonitor.Api.HostedServices;
using FinMonitor.Api.Hubs;
using FinMonitor.Api.Options;
using FinMonitor.Api.Realtime;
using FinMonitor.Domain.Realtime;
using FinMonitor.Domain.Repositories;
using FinMonitor.Domain.Services;

var builder = WebApplication.CreateBuilder(args);

const string CorsPolicy = "Frontend";

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()); // why?
});


var storageOptions = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
var redisOptions = builder.Configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>() ?? new RedisOptions();
var deploymentOptions = builder.Configuration.GetSection(DeploymentOptions.SectionName).Get<DeploymentOptions>() ?? new DeploymentOptions();
var corsOptions = builder.Configuration.GetSection(CorsOptions.SectionName).Get<CorsOptions>() ?? new CorsOptions();


if (!storageOptions.IsPostgres && !storageOptions.IsInMemory)
{
    throw new InvalidOperationException(
        $"Storage:Provider must be 'InMemory' or 'Postgres', but was '{storageOptions.Provider}'.");
}

// Also registered for DI (IOptions<T>) so a future component that needs one of these can take
// a constructor dependency on it instead of builder.Configuration directly.
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<RedisOptions>(builder.Configuration.GetSection(RedisOptions.SectionName));
builder.Services.Configure<DeploymentOptions>(builder.Configuration.GetSection(DeploymentOptions.SectionName));
builder.Services.Configure<CorsOptions>(builder.Configuration.GetSection(CorsOptions.SectionName));

builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicy, policy => policy
        .WithOrigins(corsOptions.AllowedOrigin)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

// Deployment:Mode=Distributed is a separate switch from Storage:Provider/Redis:Enabled so a
// misconfiguration fails loudly at startup instead of running degraded and inconsistent.
if (deploymentOptions.IsDistributed)
{
    if (!storageOptions.IsPostgres)
    {
        throw new InvalidOperationException(
            "Deployment:Mode=Distributed requires Storage:Provider=Postgres. Refusing to start " +
            "with a per-pod in-memory store, which would silently diverge across replicas instead " +
            "of failing loudly.");
    }

    if (!redisOptions.Enabled)
    {
        throw new InvalidOperationException(
            "Deployment:Mode=Distributed requires Redis:Enabled=true. Refusing to start without " +
            "the SignalR cross-pod backplane, which would silently drop real-time updates for " +
            "clients connected to a different pod than the one that handled a POST.");
    }
}

// In-memory per pod by default; Postgres in distributed mode so GET is consistent across pods -
// see PostgresTransactionRepository / the ADR in README.md for why Postgres over SQLite.
if (storageOptions.IsPostgres)
{
    var connectionString = storageOptions.ConnectionString
        ?? throw new InvalidOperationException("Storage:ConnectionString is required when Storage:Provider is Postgres.");
    builder.Services.AddSingleton<ITransactionRepository>(new PostgresTransactionRepository(connectionString));
}
else
{
    builder.Services.AddSingleton<ITransactionRepository, InMemoryTransactionRepository>();
}

builder.Services.AddHostedService<StorageStartupHostedService>();

builder.Services.AddSingleton<TransactionBroadcastQueue>();
builder.Services.AddSingleton<ITransactionBroadcaster, SignalRTransactionBroadcaster>();
builder.Services.AddHostedService<TransactionBroadcastWorker>();
builder.Services.AddSingleton<ITransactionService, TransactionService>();

// AddJsonProtocol configures the hub's own wire format - ConfigureHttpJsonOptions above only
// covers plain HTTP responses, so without this the enum serializes as a raw int over the hub.
var signalR = builder.Services.AddSignalR().AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
if (redisOptions.Enabled)
{
    signalR.AddStackExchangeRedis(redisOptions.ConnectionString, options =>
    {
        options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("finmonitor:");
    });
}

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<StorageExceptionHandler>();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors(CorsPolicy);

app.MapTransactionEndpoints();
app.MapHub<TransactionHub>("/hubs/transactions");

app.MapGet("/healthz", () =>
    Results.Ok("Healthy"))
    .WithName("HealthCheck");

app.Run();

