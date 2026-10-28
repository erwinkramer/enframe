using System.Text.Json.Nodes;
using Enframe.Schema;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using ModelContextProtocol.Protocol;

namespace Enframe.Mcp;

/// <summary>
/// Creates MCP tools dynamically from an OpenAPI specification.
/// </summary>
public static class OpenApiTools
{
    /// <summary>Shared client for fetching a spec from a URL.</summary>
    private static readonly HttpClient Http = new();

    /// <summary>Token-based auth details inferred from the spec's <c>securitySchemes</c>.</summary>
    public sealed record SpecTokenAuth(string Audience, IReadOnlyList<string> Scopes, string? AuthorizationServer);

    /// <summary>Server identity (name, version, description, website URL) inferred from the spec's <c>info</c> object.</summary>
    public sealed record SpecServerInfo(string Name, string Version, string? Description, string? WebsiteUrl);

    /// <summary>
    /// Sanitizes a candidate tool name to the characters MCP allows
    /// ([a-zA-Z0-9_.-]); empty or whitespace-only candidates return null.
    /// </summary>
    private static string? SanitizeToolName(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;
        return new string(candidate.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
    }

    /// <summary>
    /// Reads the MCP server's identity from the spec's <c>info</c> object: <c>title</c>
    /// and <c>version</c> (falling back to defaults when omitted), <c>description</c>,
    /// and <c>contact.url</c> as the website URL.
    /// </summary>
    public static SpecServerInfo ResolveServerInfo(OpenApiDocument document)
    {
        var info = document.Info;
        return new SpecServerInfo(
            Name: info.Title ?? "Enframe",
            Version: info.Version ?? "1.0.0",
            Description: info.Description,
            WebsiteUrl: info.Contact?.Url?.OriginalString);
    }

    /// <summary>
    /// Infers the base URL of the target API from the spec's <c>servers</c>: the first
    /// server's URL with its server variables substituted by their <c>default</c>
    /// (or first enumerated) value, resolved against the spec document's location
    /// when relative. Returns null when the spec declares no usable http(s) base URL.
    /// </summary>
    public static Uri? ResolveBaseUrl(OpenApiDocument document)
    {
        var server = document.Servers?.FirstOrDefault();
        if (server is null || string.IsNullOrWhiteSpace(server.Url))
            return null;

        // Server variables are replaced by their default (or first enumerated) value.
        var url = server.ReplaceServerUrlVariables();

        // A relative server URL is relative to the spec document's location.
        // (Uri.TryCreate treats a bare "/path" as an absolute file:// URI, so it
        // only counts as absolute here when it actually names an http(s) host.)
        var baseUri = Uri.TryCreate(url, UriKind.Absolute, out var absolute)
            && absolute.Scheme is "http" or "https"
            ? absolute
            : new Uri(document.BaseUri, url);
        return baseUri.Scheme is "http" or "https" ? baseUri : null;
    }

    /// <summary>
    /// Infers the token-based auth details from the spec's <c>securitySchemes</c>:
    /// the OAuth2 flow's scopes are the scopes a client must request; the base of a
    /// <c>/.default</c> scope (or the first scope) is the audience the authorization
    /// server stamps in the token's <c>aud</c> claim; and the <c>openIdConnectUrl</c>
    /// (minus its <c>/.well-known/openid-configuration</c> suffix) is the authorization
    /// server. Returns null when the spec declares no usable token-based auth.
    /// </summary>
    public static SpecTokenAuth? ResolveTokenAuth(OpenApiDocument document)
    {
        var schemes = document.Components?.SecuritySchemes ?? new Dictionary<string, IOpenApiSecurityScheme>();
        var scopes = new List<string>();
        string? authorizationServer = null;

        foreach (var scheme in schemes.Values)
        {
            if (scheme.Type == SecuritySchemeType.OAuth2)
            {
                // Every flow may declare scopes; keep the first occurrence of each.
                var scopeSet = new HashSet<string>();
                OpenApiOAuthFlow[] flows = scheme.Flows is not null
                    ? [scheme.Flows.AuthorizationCode!, scheme.Flows.ClientCredentials!, scheme.Flows.Implicit!, scheme.Flows.Password!, scheme.Flows.DeviceAuthorization!]
                    : [];
                foreach (var flow in flows)
                {
                    if (flow?.Scopes is not null)
                        foreach (var flowScope in flow.Scopes.Keys)
                            if (scopeSet.Add(flowScope))
                                scopes.Add(flowScope);
                }
            }
            else if (scheme.Type == SecuritySchemeType.OpenIdConnect && scheme.OpenIdConnectUrl is not null)
            {
                // .../v2.0/.well-known/openid-configuration -> .../v2.0
                authorizationServer = scheme.OpenIdConnectUrl.AbsoluteUri;
                const string suffix = "/.well-known/openid-configuration";
                if (authorizationServer.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    authorizationServer = authorizationServer[..^suffix.Length];
            }
        }

        // IdPs typically stamp the token's aud claim with the scope's base (e.g. Entra
        // drops the "/.default" suffix); fall back to the first scope.
        var scope = scopes.FirstOrDefault(s => s.EndsWith("/.default", StringComparison.Ordinal))
            ?? scopes.FirstOrDefault();
        if (scope is null)
            return null;
        const string defaultSuffix = "/.default";
        var audience = scope.EndsWith(defaultSuffix, StringComparison.Ordinal)
            ? scope[..^defaultSuffix.Length]
            : scope;

        return new SpecTokenAuth(audience, scopes, authorizationServer);
    }

    public static IList<OpenApiTool> Create(string apiKey, OpenApiDocument document)
    {
        var tools = new List<OpenApiTool>();
        // Shared across all tools: component schemas are serialized and inlined once per
        // document, then deep-cloned into the tools that reference them.
        var inlined = new Dictionary<string, JsonNode>();
        foreach (var (path, pathItem) in document.Paths)
        {
            if (pathItem.Operations is null)
                continue;

            foreach (var (verb, operation) in pathItem.Operations)
            {
                // Infer the auth mode from the operation's `security` requirements.
                // (Auth is handled transparently at call time; it doesn't belong in the tool name.)
                var auth = AuthResolver.ResolveAuthMode(document, operation);

                // Path, query, and header parameters become tool arguments.
                // (Header parameters carry things like the If-Match entity tag for
                // optimistic concurrency, which the API requires even though it
                // doesn't belong in the URL.)
                var parameters = new Dictionary<string, ToolParameter>();
                foreach (var param in operation.Parameters ?? [])
                {
                    if (param.In is not (ParameterLocation.Path or ParameterLocation.Query or ParameterLocation.Header))
                        continue;
                    parameters[param.Name!] = new(param.In.Value, param.Required, param.Schema, param.Description, param.Style, param.Explode);
                }

                // Infer the success (2xx) response shape the same way the request shape is
                // inferred: status code, declared response headers, and body.
                var response = SchemaBuilder.ResolveSuccessResponse(operation);
                var responseSchema = SchemaBuilder.ResolveSuccessResponseSchema(operation);
                var responseHeaders = response?.Headers is { Count: > 0 }
                    ? response.Headers.Keys.ToList()
                    : null;

                var title = operation.Summary ?? $"{verb} {path}";
                // The spec's operationId is the tool name: unique per document by the
                // OpenAPI rule. MCP tool names allow only [a-zA-Z0-9_.-].
                var name = SanitizeToolName(operation.OperationId)
                    ?? throw new InvalidOperationException(
                        $"Operation {verb} {path} declares no operationId, which the OpenAPI spec requires; add one.");
                var tool = new Tool
                {
                    Name = name,
                    Title = title,
                    Description = operation.Description ?? title,
                    InputSchema = SchemaBuilder.BuildInputSchema(document, parameters, operation.RequestBody, inlined).ToJsonElement(),
                    OutputSchema = response is null ? null : SchemaBuilder.BuildOutputSchema(document, response, inlined).ToJsonElement(),

                    Annotations = new ToolAnnotations
                    {
                        ReadOnlyHint = verb == HttpMethod.Get || verb == HttpMethod.Query,
                        DestructiveHint = verb == HttpMethod.Put || verb == HttpMethod.Delete,
                        IdempotentHint = verb != HttpMethod.Post,
                    },
                };

                // OpenAPI tags aren't part of the MCP tool shape; carry them in _meta.
                if (operation.Tags is { Count: > 0 })
                    tool.Meta = new JsonObject
                    {
                        ["tags"] = new JsonArray(operation.Tags.Select(t => (JsonNode?)t.Name).ToArray()),
                    };

                tools.Add(new OpenApiTool(tool, verb, path, parameters, responseSchema, responseHeaders, apiKey, auth));
            }
        }

        // Fail fast if an operation needs a subscription key that was not provided:
        // otherwise the error would only surface when that tool is called.
        if (string.IsNullOrWhiteSpace(apiKey) && tools.Any(t => t.Auth.Mode == AuthMode.ApiKey))
            throw new InvalidOperationException(
                "The spec protects at least one operation with an apiKey scheme but no API key is configured: set Enframe:ApiKey.");

        return tools;
    }

    /// <summary>
    /// Loads the OpenAPI spec from the given source: an http(s) URL is fetched,
    /// anything else is treated as a path relative to the executable.
    /// </summary>
    public static async Task<OpenApiDocument> LoadDocumentAsync(string specSource)
    {
        byte[] bytes;
        Uri baseUri;
        if (Uri.TryCreate(specSource, UriKind.Absolute, out var url)
            && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
        {
            bytes = await Http.GetByteArrayAsync(url);
            baseUri = url;
        }
        else
        {
            var specPath = Path.Combine(AppContext.BaseDirectory, specSource);
            bytes = File.ReadAllBytes(specPath);
            baseUri = new Uri(specPath);
        }
        // The 3.x OpenApiJsonReader only accepts a MemoryStream (the 2.x Stream overload is gone).
        return new OpenApiJsonReader().Read(new MemoryStream(bytes), baseUri, new()).Document!;
    }
}
