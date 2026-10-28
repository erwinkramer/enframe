namespace Enframe.Mcp.Tests;

/// Small test utilities shared by the e2e fixture.
internal static class TestHelpers
{
    /// <summary>Polls <paramref name="probe"/> every 250 ms until it returns non-null, or throws on timeout.</summary>
    public static async Task<T> WaitUntilAsync<T>(Func<Task<T?>> probe, string what, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            var result = await probe();
            if (result is not null)
            {
                return result;
            }
            try
            {
                await Task.Delay(250, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new Exception($"{what} not available within {timeout}.");
            }
        }
    }

    /// <summary>Loads key=value pairs from the <c>.env</c> file at the repo root.</summary>
    public static Dictionary<string, string> LoadEnvFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".env")))
        {
            directory = directory.Parent;
        }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in File.ReadLines(Path.Combine(directory?.FullName ?? AppContext.BaseDirectory, ".env")))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            values[line[..separator].Trim()] = line[(separator + 1)..].Trim().Trim('"');
        }
        return values;
    }
}
