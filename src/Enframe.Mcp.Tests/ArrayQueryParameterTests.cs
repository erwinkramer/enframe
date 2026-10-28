using ModelContextProtocol.Client;
using TUnit;

namespace Enframe.Mcp.Tests;

/// <summary>
/// End-to-end test for array query parameter serialization: the petstore v3 spec
/// declares <c>findPetsByTags</c>'s <c>tags</c> as an exploded form-style query
/// array, so a client argument of <c>["corgi","poodle"]</c> must reach the backend
/// as repeated parameters (<c>?tags=corgi&amp;tags=poodle</c>), not as a raw JSON
/// string.
/// </summary>
internal sealed class ArrayQueryParameterTests
{
    private const string Spec = "Specs/openapi_petstore_v3_spec.json";

    [Test]
    public async Task Array_query_parameter_is_sent_as_repeated_query_parameters()
    {
        await using var backend = await MockBackend.StartAsync(1);
        using var sidecar = await SidecarProcess.StartAsync(backend.Port, "test-key", Spec);
        await using var client = await CreateMcpClientAsync(sidecar.Port);

        var result = await client.CallToolAsync("findPetsByTags", new Dictionary<string, object?>
        {
            ["tags"] = new[] { "corgi", "poodle" },
        });

        await Assert.That(result.IsError == true).IsFalse();

        var rawUrl = backend.LastRawUrl ?? "";
        await Assert.That(rawUrl).Contains("tags=corgi");
        await Assert.That(rawUrl).Contains("tags=poodle");
        await Assert.That(rawUrl.Contains("%5B")).IsFalse(); // no raw JSON array bracket
        await Assert.That(rawUrl.Contains("%22")).IsFalse(); // no JSON quoting
    }

    // Self-contained spec (not in specs/, so no golden file is required): one
    // operation with two non-exploded array query parameters — form-style (join
    // with ',') and spaceDelimited-style (join with ' ').
    private const string NonExplodedSpecFile = "array_explode_test_spec.json";

    private const string NonExplodedSpec = $$"""
    {
      "openapi": "3.0.3",
      "info": { "title": "Array explode test", "version": "1.0.0" },
      "paths": {
        "/things": {
          "get": {
            "operationId": "listThings",
            "parameters": [
              {
                "name": "ids",
                "in": "query",
                "style": "form",
                "explode": false,
                "schema": { "type": "array", "items": { "type": "string" } }
              },
              {
                "name": "codes",
                "in": "query",
                "style": "spaceDelimited",
                "explode": false,
                "schema": { "type": "array", "items": { "type": "string" } }
              }
            ],
            "responses": {
              "200": {
                "description": "ok",
                "content": { "application/json": { "schema": { "type": "array", "items": { "type": "string" } } } }
              }
            }
          }
        }
      }
    }
    """;

    [Test]
    public async Task Non_exploded_array_query_parameters_are_joined_with_the_style_delimiter()
    {
        // Self-contained spec written into the test output directory so it does
        // not take part in the golden-schema tests over specs/.
        var specPath = Path.Combine(AppContext.BaseDirectory, NonExplodedSpecFile);
        await File.WriteAllTextAsync(specPath, NonExplodedSpec);

        await using var backend = await MockBackend.StartAsync(1);
        using var sidecar = await SidecarProcess.StartAsync(backend.Port, "test-key", NonExplodedSpecFile);
        await using var client = await CreateMcpClientAsync(sidecar.Port);

        var result = await client.CallToolAsync("listThings", new Dictionary<string, object?>
        {
            ["ids"] = new[] { "a", "b" },
            ["codes"] = new[] { "x", "y" },
        });

        await Assert.That(result.IsError == true).IsFalse();

        var rawUrl = backend.LastRawUrl ?? "";
        await Assert.That(rawUrl).Contains("ids=a%2Cb");      // form, explode:false -> comma-joined
        await Assert.That(rawUrl).Contains("codes=x%20y");    // spaceDelimited, explode:false
        await Assert.That(rawUrl.Contains("&ids=")).IsFalse(); // not repeated
    }

    [Test]
    public async Task Scalar_query_parameter_still_serializes_as_single_value()
    {
        // Regression guard: the array change must not alter scalar (string) handling.
        await using var backend = await MockBackend.StartAsync(1);
        using var sidecar = await SidecarProcess.StartAsync(backend.Port, "test-key", Spec);
        await using var client = await CreateMcpClientAsync(sidecar.Port);

        var result = await client.CallToolAsync("findPetsByStatus", new Dictionary<string, object?>
        {
            ["status"] = "available",
        });

        await Assert.That(result.IsError == true).IsFalse();

        var rawUrl = backend.LastRawUrl ?? "";
        await Assert.That(rawUrl).Contains("status=available");
    }

    private static async Task<McpClient> CreateMcpClientAsync(int port)
    {
        var transport = new HttpClientTransport(new()
        {
            Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
        });
        return await McpClient.CreateAsync(transport, new()
        {
            ClientInfo = new() { Name = "enframe-test", Version = "1.0.0" },
        });
    }
}
