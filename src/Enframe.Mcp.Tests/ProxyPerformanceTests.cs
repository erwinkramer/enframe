using System.Diagnostics;
using ModelContextProtocol.Client;
using TUnit;

namespace Enframe.Mcp.Tests;

/// <summary>
/// Performance test for the MCP proxy: measures the sidecar's overhead compared to
/// calling the backend API directly, using a self-contained local mock backend
/// (no Docker, no external API, no auth).
///
/// Two payload sizes are benchmarked (small and large) because the proxy's JSON
/// re-encoding cost only shows up with bigger responses. For each size two load
/// shapes are measured:
///   steady state — 4 MCP sessions each making calls one at a time (one request
///   in flight per session, like a typical client), compared against a direct
///   baseline with the same concurrency;
///   burst — all 200 calls fired at once, compared against a direct burst
///   baseline, to expose CPU-bound scaling.
/// </summary>
internal sealed class ProxyPerformanceTests
{
    private const int Sessions = 4;
    private const int CallsPerSession = 50;
    private const string Spec = "Specs/openapi_petstore_v2_spec.json";

    [Test]
    public async Task Proxied_tool_calls_keep_up_with_direct_backend_calls()
    {
        await RunBenchmarkAsync(10, "small (10 items)");
        await RunBenchmarkAsync(500, "large (500 items)");
    }

    private async Task RunBenchmarkAsync(int payloadItems, string label)
    {
        var output = TestContext.Current!.OutputWriter;
        await using var backend = await MockBackend.StartAsync(payloadItems);
        using var sidecar = await SidecarProcess.StartAsync(backend.Port, "test-key", Spec);
        var baseUri = new Uri($"http://127.0.0.1:{backend.Port}");
        var totalCalls = Sessions * CallsPerSession;

        // Baseline 1: 200 concurrent GETs straight at the backend (backend ceiling).
        var (directBurstLatencies, directBurstErrors, directBurstElapsed) =
            await MeasureDirectBurstAsync(baseUri, totalCalls);

        // Baseline 2: 4 workers each firing 50 GETs one at a time (fair steady-state baseline).
        var (directSteadyLatencies, directSteadyErrors, directSteadyElapsed) =
            await MeasureDirectSteadyAsync(baseUri);

        // Through the proxy, steady state: 4 MCP sessions, one call in flight each.
        var clients = new List<McpClient>(Sessions);
        for (var i = 0; i < Sessions; i++)
            clients.Add(await CreateMcpClientAsync(sidecar.Port));

        // Warm up: one tool call per session (discarded) so JIT and connection
        // setup don't skew the measured calls.
        foreach (var client in clients)
            await CallToolOnceAsync(client);

        // Steady state: one call in flight per session, 4 in total.
        var steadyWorkers = new Task<(double[] LatenciesMs, int Errors)>[Sessions];
        for (var c = 0; c < Sessions; c++)
        {
            var client = clients[c];
            steadyWorkers[c] = Task.Run(async () =>
            {
                var latencies = new double[CallsPerSession];
                var errors = 0;
                for (var i = 0; i < CallsPerSession; i++)
                {
                    var (latency, ok) = await CallToolOnceAsync(client);
                    latencies[i] = latency;
                    if (!ok) errors++;
                }
                return (latencies, errors);
            });
        }
        var steadyStopwatch = Stopwatch.StartNew();
        var steadyPerWorker = await Task.WhenAll(steadyWorkers);
        var steadyElapsed = steadyStopwatch.Elapsed;
        var steadyLatencies = steadyPerWorker.SelectMany(w => w.LatenciesMs).OrderBy(x => x).ToArray();
        var steadyErrors = steadyPerWorker.Sum(w => w.Errors);

        // Through the proxy, burst: every session fires all its calls at once.
        var burstTasks = new List<Task<(double LatencyMs, bool Ok)>>(totalCalls);
        foreach (var client in clients)
            for (var i = 0; i < CallsPerSession; i++)
                burstTasks.Add(CallToolOnceAsync(client));
        var burstStopwatch = Stopwatch.StartNew();
        var burstResults = await Task.WhenAll(burstTasks);
        var burstElapsed = burstStopwatch.Elapsed;
        foreach (var client in clients)
            await client.DisposeAsync();

        var burstLatencies = burstResults.Select(r => r.LatencyMs).OrderBy(x => x).ToArray();
        var burstErrors = burstResults.Count(r => !r.Ok);

        // Report the numbers so the overhead is visible even when the test passes.
        output.WriteLine($"--- {label} ---");
        output.WriteLine(
            $"direct  : burst {totalCalls} calls in {directBurstElapsed.TotalMilliseconds:F0} ms ({totalCalls / directBurstElapsed.TotalSeconds:F0} req/s), p50 {Percentile(directBurstLatencies, 50):F1} ms, p95 {Percentile(directBurstLatencies, 95):F1} ms, errors {directBurstErrors}" +
            $", steady p50 {Percentile(directSteadyLatencies, 50):F1} ms, p95 {Percentile(directSteadyLatencies, 95):F1} ms, errors {directSteadyErrors}");
        output.WriteLine(
            $"proxied : steady {totalCalls} calls in {steadyElapsed.TotalMilliseconds:F0} ms ({totalCalls / steadyElapsed.TotalSeconds:F0} req/s), p50 {Percentile(steadyLatencies, 50):F1} ms, p95 {Percentile(steadyLatencies, 95):F1} ms, errors {steadyErrors}" +
            $", burst {totalCalls} calls in {burstElapsed.TotalMilliseconds:F0} ms ({totalCalls / burstElapsed.TotalSeconds:F0} req/s), p50 {Percentile(burstLatencies, 50):F1} ms, p95 {Percentile(burstLatencies, 95):F1} ms, errors {burstErrors}");
        output.WriteLine(
            $"overhead: steady-state p95 +{Percentile(steadyLatencies, 95) - Percentile(directSteadyLatencies, 95):F1} ms (direct p95 {Percentile(directSteadyLatencies, 95):F1} ms), " +
            $"burst throughput is {100.0 * (totalCalls / burstElapsed.TotalSeconds) / (totalCalls / directBurstElapsed.TotalSeconds):F1}% of the direct burst");

        if (directBurstErrors > 0 || directSteadyErrors > 0)
            throw new Exception($"[{label}] direct baseline calls failed; rerun the test.");
        if (steadyErrors > 0)
            throw new Exception($"[{label}] {steadyErrors} of {totalCalls} steady-state tool calls failed.");
        if (burstErrors > 0)
            throw new Exception($"[{label}] {burstErrors} of {totalCalls} burst tool calls failed.");

        // Steady state: the per-call overhead must stay small compared to the backend
        // itself (at fixed low concurrency, throughput is just 1/latency, so latency
        // is the meaningful metric).
        var steadyOverhead = Percentile(steadyLatencies, 95) - Percentile(directSteadyLatencies, 95);
        if (steadyOverhead > Math.Max(25, Percentile(directSteadyLatencies, 95)))
            throw new Exception($"[{label}] steady-state proxy p95 exceeds the like-for-like direct baseline p95 by {steadyOverhead:F1} ms.");

        // Burst: 200-way concurrency is CPU-bound for the proxy (it re-encodes every
        // response), so a large gap to the raw-backend burst is expected and the
        // ratio is reported only. A hard floor still catches catastrophic stalls
        // (e.g. a blackholed hostname adds seconds per call).
        var burstRate = totalCalls / burstElapsed.TotalSeconds;
        if (burstRate < 50)
            throw new Exception($"[{label}] burst proxy throughput {burstRate:F0} req/s is below the 50 req/s floor; the proxy is stalling under load.");
    }

    private static async Task<McpClient> CreateMcpClientAsync(int port)
    {
        var transport = new HttpClientTransport(new()
        {
            Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
        });
        return await McpClient.CreateAsync(transport, new()
        {
            ClientInfo = new() { Name = "enframe-perf", Version = "1.0.0" },
        });
    }

    /// <summary>One proxied tool call, measured and error-tolerant.</summary>
    private static async Task<(double LatencyMs, bool Ok)> CallToolOnceAsync(McpClient client)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await client.CallToolAsync("getUserByName", new Dictionary<string, object?>
            {
                ["username"] = "octocat",
            });
            return (stopwatch.Elapsed.TotalMilliseconds, result.IsError != true);
        }
        catch
        {
            return (stopwatch.Elapsed.TotalMilliseconds, false);
        }
    }

    /// <summary><paramref name="count"/> concurrent GETs straight at the backend.</summary>
    private static async Task<(double[] LatenciesMs, int Errors, TimeSpan Elapsed)> MeasureDirectBurstAsync(Uri baseUri, int count)
    {
        using var http = new HttpClient { BaseAddress = baseUri };
        var tasks = new Task<(double LatencyMs, bool Ok)>[count];
        for (var i = 0; i < count; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/user/octocat");
            tasks[i] = MeasureAsync(() => http.SendAsync(request));
        }
        var stopwatch = Stopwatch.StartNew();
        var results = await Task.WhenAll(tasks);
        var elapsed = stopwatch.Elapsed;
        return (results.Select(r => r.LatencyMs).OrderBy(x => x).ToArray(), results.Count(r => !r.Ok), elapsed);
    }

    /// <summary>4 workers each firing 50 GETs one at a time, straight at the backend.</summary>
    private static async Task<(double[] LatenciesMs, int Errors, TimeSpan Elapsed)> MeasureDirectSteadyAsync(Uri baseUri)
    {
        using var http = new HttpClient { BaseAddress = baseUri };
        var tasks = new List<Task<(double LatencyMs, bool Ok)[]>>(Sessions);
        for (var w = 0; w < Sessions; w++)
        {
            var worker = Task.Run(async () =>
            {
                var results = new List<(double LatencyMs, bool Ok)>(CallsPerSession);
                for (var i = 0; i < CallsPerSession; i++)
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, "/user/octocat");
                    results.Add(await MeasureAsync(() => http.SendAsync(request)));
                }
                return results.ToArray();
            });
            tasks.Add(worker);
        }
        var stopwatch = Stopwatch.StartNew();
        var perWorker = await Task.WhenAll(tasks);
        var elapsed = stopwatch.Elapsed;
        var all = perWorker.SelectMany(w => w);
        return (all.Select(r => r.LatencyMs).OrderBy(x => x).ToArray(), all.Count(r => !r.Ok), elapsed);
    }

    private static async Task<(double LatencyMs, bool Ok)> MeasureAsync(Func<Task<HttpResponseMessage>> send)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await send();
            return (stopwatch.Elapsed.TotalMilliseconds, response.IsSuccessStatusCode);
        }
        catch
        {
            return (stopwatch.Elapsed.TotalMilliseconds, false);
        }
    }

    private static double Percentile(double[] sorted, double p)
        => sorted[(int)Math.Ceiling(p / 100.0 * sorted.Length) - 1];
}
