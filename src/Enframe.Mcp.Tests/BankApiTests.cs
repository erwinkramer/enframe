using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Enframe.Mcp.Tests;

/// <summary>
/// End-to-end tests exercising the bank API through the MCP sidecar.
/// </summary>
[ClassDataSource<AppFixture>(Shared = SharedType.PerTestSession)]
public class BankApiTests(AppFixture app)
{
    [Test]
    public async Task Query_banks_tool_returns_seeded_banks()
    {

        await using var client = await app.CreateMcpClientAsync();
        var result = await client.CallToolAsync("GetAllBanks", new Dictionary<string, object?>
        {
            ["body"] = new Dictionary<string, object?>
            {
                ["page"] = 1,
                ["pageSize"] = 3,
            },
        });

        var text = ToolText(result);
        using var document = JsonDocument.Parse(text);
        var count = document.RootElement.GetProperty("count").GetInt32();
        var firstId = document.RootElement.GetProperty("data")[0].GetProperty("id").GetString();
        await Assert.That(count).IsGreaterThanOrEqualTo(1);
        await Assert.That(firstId).IsNotNull();
        await Assert.That(firstId!).IsNotEmpty();
    }

    [Test]
    public async Task Create_get_update_bank_roundtrip()
    {

        var id = Guid.NewGuid().ToString();

        await using var client = await app.CreateMcpClientAsync();

        // Create through the MCP sidecar (201 + the created bank).
        var created = ToolText(await client.CallToolAsync("CreateBank", new Dictionary<string, object?>
        {
            ["body"] = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["name"] = "E2E branch",
                ["isCompliant"] = false,
                ["bankTier"] = "C",
            },
        }));
        await Assert.That(created).Contains(id);

        // The 204 update result carries status and headers only (and the API sends
        // no ETag on it), so fetch the ETag from the bank API directly (API key auth).
        using var get = await SendToBankAsync(HttpMethod.Get, $"/banks/{id}");
        var originalEtag = get.Headers.ETag?.Tag;
        await Assert.That((int)get.StatusCode).IsEqualTo(200);
        await Assert.That(originalEtag).IsNotNull();

        // Update through the MCP sidecar with an If-Match header (204, no body).
        var updated = await client.CallToolAsync("UpdateBank", new Dictionary<string, object?>
        {
            ["id"] = id,
            ["If-Match"] = originalEtag,
            ["body"] = new Dictionary<string, object?>
            {
                ["name"] = "E2E branch updated",
                ["isCompliant"] = true,
                ["bankTier"] = "A",
            },
        });
        using (var updatedDocument = JsonDocument.Parse(updated.StructuredContent?.GetRawText()
            ?? throw new Exception("Expected structured content for a 204 result.")))
        {
            // A 204 No Content maps to status and declared headers only, with no body.
            var updatedRoot = updatedDocument.RootElement;
            await Assert.That(updatedRoot.GetProperty("status").GetInt32()).IsEqualTo(204);
            await Assert.That(updatedRoot.TryGetProperty("body", out _)).IsFalse();
            await Assert.That(updatedRoot.GetProperty("headers").TryGetProperty("API-Version", out _)).IsTrue();
        }

        // Read back from the bank API and confirm the update landed.
        using var verify = await SendToBankAsync(HttpMethod.Get, $"/banks/{id}");
        var verifyBody = await verify.Content.ReadAsStringAsync();
        await Assert.That((int)verify.StatusCode).IsEqualTo(200);
        await Assert.That(verifyBody).Contains("E2E branch updated");
    }

    [Test]
    public async Task Delete_bank_204_result_carries_status_and_declared_headers()
    {

        var id = Guid.NewGuid().ToString();

        // Create the bank, then delete it through the MCP sidecar with an If-Match
        // header and assert the 204 result shape: status and declared headers only.
        await using var client = await app.CreateMcpClientAsync();
        _ = ToolText(await client.CallToolAsync("CreateBank", new Dictionary<string, object?>
        {
            ["body"] = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["name"] = "204 delete probe",
            },
        }));
        using var get = await SendToBankAsync(HttpMethod.Get, $"/banks/{id}");
        var etag = get.Headers.ETag?.Tag;

        var result = await client.CallToolAsync("DeleteBank", new Dictionary<string, object?>
        {
            ["id"] = id,
            ["If-Match"] = etag,
        });
        using var document = JsonDocument.Parse(result.StructuredContent?.GetRawText()
            ?? throw new Exception("Expected structured content for a 204 result."));
        var root = document.RootElement;
        await Assert.That(root.GetProperty("status").GetInt32()).IsEqualTo(204);
        await Assert.That(root.TryGetProperty("body", out _)).IsFalse();
        await Assert.That(root.GetProperty("headers").TryGetProperty("API-Version", out _)).IsTrue();

        // The bank is gone: a follow-up GET reports 404.
        using var verify = await SendToBankAsync(HttpMethod.Get, $"/banks/{id}");
        await Assert.That((int)verify.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task Error_with_empty_body_reports_status_in_text()
    {
        // A GET for a bank that does not exist answers 404 with an empty body.
        // The tool result must still carry the status instead of an empty text block.
        var id = Guid.NewGuid().ToString();
        await using var client = await app.CreateMcpClientAsync();
        var result = await client.CallToolAsync("GetBank", new Dictionary<string, object?>
        {
            ["id"] = id,
        });
        await Assert.That(result.IsError).IsTrue();
        await Assert.That(TextOf(result)).StartsWith("HTTP 404");
        await Assert.That(result.StructuredContent).IsNull();
    }

    [Test]
    public async Task Error_with_json_body_keeps_body_text()
    {
        // A 422 with a ProblemDetails body passes the body through unchanged.
        var id = Guid.NewGuid().ToString();
        await using var client = await app.CreateMcpClientAsync();
        var create = new Dictionary<string, object?>
        {
            ["body"] = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["name"] = "duplicate probe",
            },
        };
        _ = ToolText(await client.CallToolAsync("CreateBank", create));
        var duplicate = await client.CallToolAsync("CreateBank", create);
        await Assert.That(duplicate.IsError).IsTrue();
        await Assert.That(TextOf(duplicate)).Contains("already been added");

        // Clean up so the shared bank store stays tidy.
        using var get = await SendToBankAsync(HttpMethod.Get, $"/banks/{id}");
        var etag = get.Headers.ETag?.Tag;
        await client.CallToolAsync("DeleteBank", new Dictionary<string, object?>
        {
            ["id"] = id,
            ["If-Match"] = etag,
        });
    }

    [Test]
    public async Task Get_teller_tool_returns_bank_teller()
    {

        await using var client = await app.CreateMcpClientAsync();
        var result = await client.CallToolAsync("GetBankTeller");
        var text = ToolText(result);
        using var document = JsonDocument.Parse(text);
        var profile = document.RootElement.GetProperty("gitHubProfile").GetString();
        await Assert.That(profile).IsNotNull();
        await Assert.That(profile).StartsWith("https://github.com/");
    }

    private Task<HttpResponseMessage> SendToBankAsync(HttpMethod method, string path, string? etag = null)
    {
        var request = new HttpRequestMessage(method, new Uri(app.ApiBase, path.TrimStart('/')));
        request.Headers.Add("Ocp-Apim-Subscription-Key", AppFixture.SubscriptionKey);
        if (etag is not null)
            request.Headers.Add("If-Match", etag);
        return AppFixture.Http.SendAsync(request);
    }

    /// <summary>The plain <c>text</c> payload of a tool result (throws if the tool errored).</summary>
    private static string ToolText(CallToolResult result)
    {
        if (result.IsError == true)
        {
            throw new Exception($"Tool reported an error: {TextOf(result)}");
        }
        return TextOf(result);
    }

    private static string TextOf(CallToolResult result)
        => result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "";
}

