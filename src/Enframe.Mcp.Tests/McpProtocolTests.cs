using System.Text;
using System.Text.Json;

namespace Enframe.Mcp.Tests;

/// <summary>
/// End-to-end tests for the MCP protocol layer: protected-resource metadata,
/// 401 handling and tools/list.
/// </summary>
[ClassDataSource<AppFixture>(Shared = SharedType.PerTestSession)]
public class McpProtocolTests(AppFixture app)
{
    [Test]
    public async Task Protected_resource_metadata_is_served_without_auth()
    {

        using var response = await AppFixture.Http.GetAsync(new Uri(app.McpBase, "mcp/.well-known/oauth-protected-resource"));
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        await Assert.That((int)response.StatusCode).IsEqualTo(200);
        await Assert.That(document.RootElement.GetProperty("resource").GetString())
            .IsEqualTo(app.ApiBase.OriginalString + "/mcp");
    }

    [Test]
    public async Task Unauthenticated_tools_list_returns_401_with_resource_metadata()
    {

        // No Authorization header: the auth middleware rejects the request before the
        // MCP protocol is touched.
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(app.McpBase, "mcp"))
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}", Encoding.UTF8, "application/json"),
        };
        using var response = await AppFixture.Http.SendAsync(request);
        var challenge = response.Headers.WwwAuthenticate?.ToString() ?? "";
        await Assert.That((int)response.StatusCode).IsEqualTo(401);
        await Assert.That(challenge).Contains("Bearer");
        await Assert.That(challenge).Contains("resource_metadata");
    }

    [Test]
    public async Task Authenticated_tools_list_contains_all_OpenAPI_tools()
    {

        await using var client = await app.CreateMcpClientAsync();
        var names = (await client.ListToolsAsync())
            .Select(tool => tool.Name)
            .ToArray();

        await Assert.That(names).Contains("CreateBank");
        await Assert.That(names).Contains("GetBank");
        await Assert.That(names).Contains("UpdateBank");
        await Assert.That(names).Contains("DeleteBank");
        await Assert.That(names).Contains("GetAllBanks");
        await Assert.That(names).Contains("GetBankTeller");
    }

    [Test]
    public async Task Put_and_delete_tools_declare_status_and_headers_output_schema()
    {

        // The 204 No Content responses declare no body, but the tools must still
        // declare an output schema (status plus the declared response headers), so
        // 204 tool results are typed instead of unstructured.
        await using var client = await app.CreateMcpClientAsync();
        var tools = (await client.ListToolsAsync()).ToList();

        foreach (var name in new[] { "UpdateBank", "DeleteBank" })
        {
            var tool = tools.Single(t => t.Name == name);
            var outputSchema = tool.ReturnJsonSchema ?? throw new Exception($"{name} declares no outputSchema.");
            using var document = JsonDocument.Parse(outputSchema.GetRawText());
            var root = document.RootElement;

            var required = root.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray();
            await Assert.That(required.Contains("status")).IsTrue();
            await Assert.That(required.Contains("body")).IsFalse();

            var headerNames = root.GetProperty("properties")
                .GetProperty("headers").GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
            await Assert.That(headerNames.Contains("API-Version")).IsTrue();
            await Assert.That(headerNames.Contains("X-Rate-Limit-Limit")).IsTrue();
        }
    }
}
