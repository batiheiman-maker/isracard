namespace FinMonitor.Api.Options;

public sealed class DeploymentOptions
{
    public const string SectionName = "Deployment";

    public string Mode { get; set; } = "SingleInstance";

    public bool IsDistributed => Mode.Equals("Distributed", StringComparison.OrdinalIgnoreCase);
}
