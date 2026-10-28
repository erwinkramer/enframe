using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Enframe.Mcp.Tests;

/// Runs the Enframe MCP sidecar as a local process and captures its startup output.
internal sealed class SidecarProcess : IDisposable
{
    private static readonly Regex ListeningOn = new(@"Now listening on: http://(?:localhost|127\.0\.0\.1):(\d+)", RegexOptions.Compiled);

    private readonly Process _process;
    private readonly TextWriter _log;
    private readonly Action<string> _write;
    private readonly object _logLock = new();

    /// The port the sidecar bound to, parsed from its startup output.
    public int Port { get; private set; }

    /// Starts the sidecar on an ephemeral port and returns it once it has bound.
    /// <paramref name="resource"/> overrides the advertised MCP resource URL
    /// (<c>Enframe:Resource</c>) so it can point at the externally published
    /// address while the sidecar itself connects via 127.0.0.1.
    public static async Task<SidecarProcess> StartAsync(int apiPort, string subscriptionKey, string specSource = "Specs/openapi_bankapi_spec.json", string? resource = null)
    {
        var info = new ProcessStartInfo("dotnet", Path.Combine(AppContext.BaseDirectory, "Enframe.Mcp.dll"))
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            EnvironmentVariables =
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["ASPNETCORE_URLS"] = "http://127.0.0.1:0", // Kestrel requires an explicit IP for dynamic binding, not "localhost".
                // 127.0.0.1, not "localhost": on some machines "localhost" resolves
                // to a blackholed IPv6 loopback first and costs ~2 s per connect.
                ["Enframe:ApiBaseUrl"] = $"http://127.0.0.1:{apiPort}",
                ["Enframe:ApiKey"] = subscriptionKey,
                ["Enframe:ApiSpecSource"] = specSource,
            },
        };
        if (resource is not null)
            info.EnvironmentVariables["Enframe:Resource"] = resource;
        var process = Process.Start(info)!;

        var port = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "app-e2e");
        Directory.CreateDirectory(logDir);
        // Unique log per sidecar so parallel sidecars never fight over one file.
        var log = File.CreateText(Path.Combine(logDir, $"mcp-sidecar-{Guid.NewGuid():N}.log"));

        var sidecar = new SidecarProcess(process, log);
        _ = Task.Run(() =>
        {
            while (process.StandardOutput.ReadLine() is { } line)
            {
                sidecar.Write(line);
                var match = ListeningOn.Match(line);
                if (match.Success)
                {
                    port.TrySetResult(int.Parse(match.Groups[1].Value));
                }
            }
        });
        _ = Task.Run(() =>
        {
            while (process.StandardError.ReadLine() is { } line)
            {
                sidecar.Write(line);
            }
        });
        sidecar.Port = await port.Task.WaitAsync(TimeSpan.FromMinutes(1));
        return sidecar;
    }

    private SidecarProcess(Process process, TextWriter log)
    {
        _process = process;
        _log = log;
        _write = line =>
        {
            lock (_logLock)
            {
                log.WriteLine(line);
                log.Flush();
            }
        };
    }

    private void Write(string line) => _write(line);

    /// Terminates the process tree. Best effort: orphans are harmless.
    public void Terminate()
    {
        if (_process.HasExited)
        {
            return;
        }
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        Terminate();
        lock (_logLock)
        {
            _log.Flush();
            _log.Dispose();
        }
        _process.Dispose();
    }
}
