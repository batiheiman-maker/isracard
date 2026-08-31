namespace FinMonitor.Api.Options;

public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    public bool Enabled { get; set; }
    public string ConnectionString { get; set; } = "localhost:6379,abortConnect=false";
}
