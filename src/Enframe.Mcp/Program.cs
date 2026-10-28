using Enframe.Mcp;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using ModelContextProtocol.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

// The URL prefix the MCP endpoint is served under (default "/mcp").
var rawMcpPath = configuration["Enframe:McpPath"];
var mcpPathBase = string.IsNullOrWhiteSpace(rawMcpPath)
    ? "/mcp"
    : "/" + rawMcpPath!.Trim().TrimStart('/').TrimEnd('/');

var document = await OpenApiTools.LoadDocumentAsync(configuration["Enframe:ApiSpecSource"]!);
// The base URL of the target API: the Enframe:ApiBaseUrl setting when given,
// otherwise inferred from the spec's `servers` (first server, variables at defaults).
var apiBaseUrl = !string.IsNullOrWhiteSpace(configuration["Enframe:ApiBaseUrl"])
    ? new Uri(configuration["Enframe:ApiBaseUrl"]!.TrimEnd('/'))
    : OpenApiTools.ResolveBaseUrl(document)
        ?? throw new InvalidOperationException(
            "Could not infer the base URL of the target API: the spec declares no usable `servers` entry. Set Enframe:ApiBaseUrl.");
// Client auth is enabled only when the spec declares an OAuth2 scheme with scopes
// and the authorization server is identifiable (the spec's openIdConnectUrl or
// Enframe:Authority); otherwise the server runs open (local dev, testing).
var specTokenAuth = OpenApiTools.ResolveTokenAuth(document);
var authEnabled = specTokenAuth is not null
    && (specTokenAuth.AuthorizationServer is not null
        || !string.IsNullOrWhiteSpace(configuration["Enframe:Authority"]));
builder.Services.AddHttpClient("target-api", client =>
{
    client.BaseAddress = apiBaseUrl;
    client.Timeout = TimeSpan.FromSeconds(30);
})
    .ConfigurePrimaryHttpMessageHandler(sp =>
    {
        // Recycle pooled connections so DNS changes and target-side idle timeouts
        // don't leave dead connections in the pool under load.
        var sockets = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // Don't reuse pooled connections that have been idle longer than this;
            // it must stay below the target's own idle-timeout to avoid dead connections.
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        };
        return sockets;
    });
var tools = OpenApiTools.Create(configuration["Enframe:ApiKey"]!, document);
var serverInfo = OpenApiTools.ResolveServerInfo(document);

if (authEnabled)
{
    var tokenAuth = specTokenAuth!;
    // RFC 8707 resource identifier
    var resource = !string.IsNullOrWhiteSpace(configuration["Enframe:Resource"])
        ? configuration["Enframe:Resource"]!
        : new Uri(apiBaseUrl, mcpPathBase).AbsoluteUri;
    var authoritySetting = configuration["Enframe:Authority"];
    var authorizationServer = !string.IsNullOrWhiteSpace(authoritySetting)
        ? authoritySetting
        : tokenAuth.AuthorizationServer!;
    builder.Services.AddHttpContextAccessor();

    builder.Services
        .AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.Authority = authorizationServer;
            // Accept both the audience and the resource identifier in `aud`.
            options.TokenValidationParameters.ValidAudiences = [tokenAuth.Audience, resource];
        })
        .AddMcp(options =>
        {
            options.ResourceMetadata = new()
            {
                Resource = resource,
                AuthorizationServers = [authorizationServer],
                ScopesSupported = tokenAuth.Scopes.ToArray(),
            };
        });

    builder.Services.AddAuthorization();
}

builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new()
    {
        WebsiteUrl = serverInfo.WebsiteUrl,
        Description = serverInfo.Description,
        Name = serverInfo.Name,
        Version = serverInfo.Version,
    };
    // Teach clients the Enframed structure (server-level instructions sent at initialize)
    options.ServerInstructions = LoadServerInstructions();
})
    .WithHttpTransport()
    .WithTools(tools);

builder.Services.AddHealthChecks();

var app = builder.Build();

app.UsePathBase(mcpPathBase);
if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
var mcpEndpoint = app.MapMcp();
if (authEnabled)
    mcpEndpoint.RequireAuthorization();

app.MapHealthChecks("/healthz");

app.Run();

// Server-level instructions describing the argument and result conventions every generated
// tool uses, so a client learns them once instead of per tool. Loaded once from the
// embedded ServerInstructions.md resource.
static string LoadServerInstructions()
{
    var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Enframe.Mcp.ServerInstructions.md")
        ?? throw new InvalidOperationException("Embedded resource 'ServerInstructions.md' not found.");
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd().Trim();
}
