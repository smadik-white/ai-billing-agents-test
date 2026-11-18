using System.Diagnostics;
using System.IO.Pipes;

namespace SaasBilling.IntegrationTests.Infrastructure;

/// <summary>
/// Cheap Docker availability probe used to turn integration tests into skips on machines
/// without a container runtime instead of failing the whole suite.
/// </summary>
public static class DockerProbe
{
    private static readonly Lazy<bool> Available = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsAvailable => Available.Value;

    private static bool Probe()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST")))
        {
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", "docker_engine", PipeDirection.InOut);
                pipe.Connect(500);
                return true;
            }
            catch
            {
                // Fall through to the CLI probe.
            }
        }
        else if (File.Exists("/var/run/docker.sock"))
        {
            return true;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "info")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
