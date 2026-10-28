using System.Net;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using ModelContextProtocol.Client;
using TUnit.Core.Interfaces;

namespace Enframe.Mcp.Tests;

/// End-to-end fixture for the Enframe MCP sidecar wrapping the bank API.
/// Shared across the e2e test classes for the whole test session.
public sealed class AppFixture : IAsyncInitializer, IAsyncDisposable
{
    public const string SubscriptionKey = "Lifetime Subscription";
    private const string ApiImage = "ghcr.io/erwinkramer/bank-api:latest";
    private const int ApiPort = 5201;

    /// The <c>aud</c> claim decoded from the minted token.
    public string Audience { get; private set; } = "";

    /// The sidecar's base URL, discovered from its startup output.
    public Uri McpBase { get; private set; } = null!;
    public Uri ApiBase { get; } = new($"http://localhost:{ApiPort}");

    public static HttpClient Http { get; } = new();

    private string _token = "";
    private IContainer? _api;
    private SidecarProcess? _sidecar;

    public async Task InitializeAsync()
    {
        var env = TestHelpers.LoadEnvFile();

        _api = new ContainerBuilder(ApiImage)
            .WithPortBinding(ApiPort, 8080)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r
                .ForPort(8080)
                .ForPath("/v1/health")
                .WithHeader("Ocp-Apim-Subscription-Key", SubscriptionKey)
                .ForStatusCode(HttpStatusCode.OK)))
            .Build();
        await _api.StartAsync();

        // Ephemeral port (0) so an orphan from a crashed run can never collide.
        // The advertised resource uses the published localhost address; the
        // sidecar's own backend connection stays on 127.0.0.1.
        _sidecar = await SidecarProcess.StartAsync(ApiPort, SubscriptionKey, resource: new Uri(ApiBase, "mcp").ToString());

        // The sidecar's base URL, derived from the port it actually bound.
        McpBase = new Uri($"http://127.0.0.1:{_sidecar.Port}");

        // Act as a real client: read the sidecar's protected-resource metadata for
        // the authorization server and the scope to request.
        var resourceText = await TestHelpers.WaitUntilAsync(async () =>
        {
            try
            {
                var response = await Http.GetAsync(new Uri(McpBase, "mcp/.well-known/oauth-protected-resource"));
                return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null;
            }
            catch
            {
                return null; // Sidecar not up yet; retry.
            }
        }, "MCP sidecar's protected-resource metadata", TimeSpan.FromMinutes(1));
        using var resource = JsonDocument.Parse(resourceText);
        var authorizationServer = resource.RootElement.GetProperty("authorization_servers")[0].GetString()!;
        var scope = resource.RootElement.GetProperty("scopes_supported")[0].GetString()!;

        _token = await TokenHelpers.MintTokenAsync(env["AZURE_CLIENT_ID"], env["AZURE_CLIENT_SECRET"], scope, authorizationServer);
        Audience = TokenHelpers.DecodeAudClaim(_token);
    }

    public async ValueTask DisposeAsync()
    {
        _sidecar?.Terminate();
        _sidecar?.Dispose();

        // Best effort: Testcontainers removes the container on dispose.
        if (_api is not null)
        {
            try
            {
                await _api.DisposeAsync();
            }
            catch
            {
            }
        }
    }

    public Task<McpClient> CreateMcpClientAsync()
    {
        var transport = new HttpClientTransport(new()
        {
            Endpoint = new Uri(McpBase, "mcp"),
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {_token}",
            },
        });
        return McpClient.CreateAsync(transport, new()
        {
            ClientInfo = new() { Name = "enframe-e2e", Version = "1.0.0" },
        });
    }
}
