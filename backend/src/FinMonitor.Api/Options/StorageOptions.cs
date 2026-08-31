namespace FinMonitor.Api.Options;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string Provider { get; set; } = "InMemory";
    public string? ConnectionString { get; set; }

    public bool IsPostgres => Provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase);
    public bool IsInMemory => Provider.Equals("InMemory", StringComparison.OrdinalIgnoreCase);
}
