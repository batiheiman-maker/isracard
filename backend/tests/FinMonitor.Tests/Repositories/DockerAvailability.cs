using System.Diagnostics;

namespace FinMonitor.Tests.Repositories;

// Testcontainers-backed tests need a running Docker daemon. Checked once and cached so a
// developer/CI runner without Docker gets a clean "skipped", not a failing test suite - the
// assignment requires tests to be "executable automatically", which a hard dependency on an
// unmentioned external tool would violate.
internal static class DockerAvailability
{
    public static readonly bool IsAvailable = Check();

    private static bool Check()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "info",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return false;
            }

            return process.WaitForExit(5_000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
