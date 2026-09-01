using System.Text.Json.Serialization;
using FinMonitor.Api.Endpoints;
using FinMonitor.Api.ExceptionHandling;
using FinMonitor.Api.HostedServices;
using FinMonitor.Api.Hubs;
using FinMonitor.Api.Options;
using FinMonitor.Api.Realtime;
using FinMonitor.Domain.Realtime;
using FinMonitor.Domain.Repositories;
using FinMonitor.Domain.Services;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

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


if (!storageOptions.IsPostgresEf && !storageOptions.IsInMemory)
{
    throw new InvalidOperationException(
        $"Storage:Provider must be 'InMemory' or 'PostgresEf', but was '{storageOptions.Provider}'.");
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
    if (!storageOptions.IsPostgresEf)
    {
        throw new InvalidOperationException(
            "Deployment:Mode=Distributed requires Storage:Provider=PostgresEf. Refusing to start " +
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

string? postgresConnectionString = null;
if (storageOptions.IsPostgresEf)
{
    postgresConnectionString = storageOptions.ConnectionString
        ?? throw new InvalidOperationException("Storage:ConnectionString is required when Storage:Provider is PostgresEf.");
    builder.Services.AddPooledDbContextFactory<FinMonitorDbContext>(options => options.UseNpgsql(postgresConnectionString));
}

if (redisOptions.Enabled)
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        ConnectionMultiplexer.Connect(redisOptions.ConnectionString));

    builder.Services.AddKeyedSingleton<ITransactionRepository, RedisRecentTransactionsCache>("cache");
}

if(storageOptions.IsPostgresEf)
{ 
    builder.Services.AddKeyedSingleton<ITransactionRepository, EfTransactionRepository>("db");
}
else
{
    builder.Services.AddKeyedSingleton<ITransactionRepository, InMemoryTransactionRepository>("db");
}

builder.Services.AddSingleton<ITransactionRepository>(sp =>
    sp.GetRequiredKeyedService<ITransactionRepository>(redisOptions.Enabled ? "cache" : "db"));

builder.Services.AddHostedService<StorageStartupHostedService>();

builder.Services.AddSingleton<ITransactionBroadcaster, SignalRTransactionBroadcaster>();
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
        options.Configuration.ChannelPrefix = RedisChannel.Literal("finmonitor:");
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

public partial class Program { }

