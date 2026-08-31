namespace FinMonitor.Api.Options;

public sealed class CorsOptions
{
    public const string SectionName = "Cors";

    public string AllowedOrigin { get; set; } = "http://localhost:5173";
}
