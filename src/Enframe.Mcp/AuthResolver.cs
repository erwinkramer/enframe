using Microsoft.OpenApi;

namespace Enframe.Mcp;

/// <summary>How a tool authenticates to the target API, inferred from the spec's `security` requirements.</summary>
public enum AuthMode
{
    /// <summary>Forward the client's bearer token from the IdP (http-bearer / oauth2 / openIdConnect schemes).</summary>
    Bearer,

    /// <summary>Send the APIM subscription key (apiKey scheme).</summary>
    ApiKey,

    /// <summary>No security is declared (or it uses a scheme we cannot supply); no credentials are sent.</summary>
    None,
}

/// <summary>The resolved auth mode for one operation.</summary>
public sealed record OperationAuth(AuthMode Mode, string? ApiKeyHeaderName);

/// <summary>
/// Resolves the authentication mode of operations from an OpenAPI document.
/// </summary>
internal static class AuthResolver
{
    /// <summary>
    /// Resolves how an operation is authenticated: operation-level `security` replaces (not merges) the
    /// document-level default; within it each requirement is an alternative, and token-based schemes win
    /// because the MCP SDK's OAuth flow provides the client's bearer token.
    /// </summary>
    public static OperationAuth ResolveAuthMode(OpenApiDocument document, OpenApiOperation operation)
    {
        var requirements = operation.Security ?? document.Security;
        if (requirements is null)
            return new OperationAuth(AuthMode.None, null);

        var schemes = requirements.SelectMany(r => r.Keys);
        if (schemes.Any(IsTokenScheme))
            return new OperationAuth(AuthMode.Bearer, null);

        var apiKeyScheme = schemes.FirstOrDefault(s => s.Type == SecuritySchemeType.ApiKey);
        return apiKeyScheme is null
            ? new OperationAuth(AuthMode.None, null)
            : new OperationAuth(AuthMode.ApiKey, apiKeyScheme.Name);
    }

    private static bool IsTokenScheme(IOpenApiSecurityScheme scheme) =>
        (scheme.Type == SecuritySchemeType.Http && string.Equals(scheme.Scheme, "bearer", StringComparison.OrdinalIgnoreCase))
        || scheme.Type is SecuritySchemeType.OAuth2 or SecuritySchemeType.OpenIdConnect;
}
