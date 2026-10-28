using System.Net;
using System.Text;
using System.Text.Json;

namespace Enframe.Mcp.Tests;

/// <summary>
/// A minimal local backend that accepts every request (any method, path, or
/// body) and answers with a fixed JSON payload. It lets the proxy performance
/// test measure the sidecar in isolation, with no external API or Docker
/// dependency.
/// </summary>
internal sealed class MockBackend : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly byte[] _payload;
    private readonly Task _acceptLoop;

    /// The port the backend bound to.
    public int Port { get; }

    private readonly object _lastUrlLock = new();
    private string? _lastRawUrl;

    /// The raw URL (path + query) of the most recent request, for assertions on
    /// how the caller serialized its request line.
    public string? LastRawUrl
    {
        get { lock (_lastUrlLock) return _lastRawUrl; }
        private set { lock (_lastUrlLock) _lastRawUrl = value; }
    }

    public static async Task<MockBackend> StartAsync(int payloadItems = 500)
    {
        var payload = BuildPayload(payloadItems);

        // HttpListener has no dynamic (0) port: probe random ports until one binds.
        HttpListener? listener = null;
        var port = 0;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            port = Random.Shared.Next(20000, 60000);
            var candidate = new HttpListener();
            candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                candidate.Start();
                listener = candidate;
                break;
            }
            catch (HttpListenerException)
            {
                // Port is taken; try another.
            }
        }
        if (listener is null)
            throw new InvalidOperationException("Could not bind the mock backend to a free port.");

        var backend = new MockBackend(listener, payload);
        await backend.WaitForFirstResponseAsync();
        return backend;
    }

    private MockBackend(HttpListener listener, byte[] payload)
    {
        _listener = listener;
        _payload = payload;
        var prefix = listener.Prefixes.Cast<string>().Single();
        Port = new Uri(prefix).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// Waits until the backend answers a probe request, so callers can rely on it immediately.
    private async Task WaitForFirstResponseAsync()
    {
        using var http = new HttpClient();
        var response = await http.GetAsync($"http://127.0.0.1:{Port}/");
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                break; // Listener stopped.
            }

            // Each request is handled on its own so concurrent clients are served in parallel.
            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            LastRawUrl = context.Request.RawUrl; // recorded for test assertions
            // Drain the request body so the client can finish sending it.
            var buffer = new byte[8192];
            while (await context.Request.InputStream.ReadAsync(buffer.AsMemory()) > 0)
            {
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = _payload.Length;
            await context.Response.OutputStream.WriteAsync(_payload);
        }
        catch
        {
            // Best effort: a dropped client must not take the loop down.
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch
            {
            }
        }
    }

    /// A fixed JSON response with <paramref name="items"/> items: big enough to
    /// exercise the proxy's response handling without being extreme.
    private static byte[] BuildPayload(int items)
    {
        var json = JsonSerializer.Serialize(
            Enumerable.Range(1, items).Select(i => new
            {
                id = i,
                login = $"user{i}",
                name = $"User {i}",
                html_url = $"https://github.com/user{i}",
            }));
        return Encoding.UTF8.GetBytes(json);
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Stop();
        _listener.Close();
        try
        {
            await _acceptLoop;
        }
        catch
        {
        }
        _stopping.Dispose();
    }
}
