using System.Text;
using System.Text.Json;

namespace Enframe.Mcp.Tests;

/// Client-credentials token acquisition against the authorization server the sidecar advertises.
internal static class TokenHelpers
{
    /// <summary>
    /// Mints a client-credentials token for <paramref name="scope"/> via the
    /// <c>token_endpoint</c> found in the authorization server's well-known
    /// OpenID Connect metadata.
    /// </summary>
    public static async Task<string> MintTokenAsync(string clientId, string clientSecret, string scope, string authorizationServer)
    {
        // Trailing slash makes the reference resolve to ".../v2.0/.well-known/..." instead of
        // replacing the final path segment and hitting the v1 document.
        var discovery = await AppFixture.Http.GetFromJsonAsync<JsonElement>($"{authorizationServer.TrimEnd('/')}/.well-known/openid-configuration");
        var tokenEndpoint = discovery!.GetProperty("token_endpoint").GetString()!;

        using var response = await AppFixture.Http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = scope,
        }));
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Failed to mint token: {(int)response.StatusCode} {response.StatusCode}\n{body}");
        }
        return JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>Decodes the <c>aud</c> claim from the token's payload segment.</summary>
    public static string DecodeAudClaim(string token)
    {
        var payload = token.Split('.')[1];
        payload = payload.Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        var aud = document.RootElement.GetProperty("aud");
        return aud.ValueKind == JsonValueKind.String
            ? aud.GetString()!
            : aud.EnumerateArray().First().GetString()!;
    }
}
