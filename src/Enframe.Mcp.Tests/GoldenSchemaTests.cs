using System.Text.Json;
using ModelContextProtocol;

namespace Enframe.Mcp.Tests;

/// <summary>
/// Compares the MCP schema output for each spec against the checked-in golden file
/// specs\expected\&lt;spec&gt;.mcp.json (in the source tree), so any spec or conversion
/// change that alters a field is caught. The actual output is written to TestResults\
/// for easy diffing. After a deliberate change, review the diff and copy the actual
/// file over the golden.
/// </summary>
public class GoldenSchemaTests
{
    /// <summary>
    /// Serializes the created tools exactly as the MCP library sends them in a
    /// tools/list response, using the library's own serializer.
    /// </summary>
    private static async Task<string> SerializeToolsWithMcpLibrary(string specFileName)
    {
        var tools = OpenApiTools.Create("test-key", await OpenApiTools.LoadDocumentAsync(Specs.File(specFileName)))
            .Select(t => t.ProtocolTool)
            .ToList();

        // Clone the library's default options so the golden file is readable (indented).
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions) { WriteIndented = true };
        return JsonSerializer.Serialize(tools, options);
    }

    private static string ExpectedSchemaPath(string specFileName)
        => Path.Combine(Specs.Dir, "expected", Path.GetFileNameWithoutExtension(specFileName) + ".mcp.json");

    [Test]
    [MethodDataSource(typeof(Specs), nameof(Specs.Files))]
    public async Task Spec_tools_match_expected_MCP_schema_output(string name, string _)
    {
        var expectedPath = ExpectedSchemaPath(name);
        await Assert.That(File.Exists(expectedPath)).IsTrue();

        var actualJson = await SerializeToolsWithMcpLibrary(name);
        var actualPath = Path.Combine(AppContext.BaseDirectory, "TestResults", $"{name}.mcp.actual.json");
        Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
        File.WriteAllText(actualPath, actualJson);

        using var actual = JsonDocument.Parse(actualJson);
        using var expected = JsonDocument.Parse(File.ReadAllText(expectedPath));

        if (!JsonElement.DeepEquals(actual.RootElement, expected.RootElement))
            throw new Exception(
                $"MCP schema output for '{name}' differs from the golden file.\n" +
                $"Actual:   {actualPath}\n" +
                $"Expected: {expectedPath}\n" +
                "Diff the two files; if the change is intentional, copy the actual file over the golden.");
    }

    /// <summary>
    /// Regenerates the golden files under Specs\expected\ in the test output dir;
    /// copy them over specs\expected\ in the source tree when the change is deliberate.
    /// Gated behind an env var so a normal test run never overwrites them:
    ///     $env:MCP_REGENERATE_GOLDENS = "1"
    ///     dotnet test --treenode-filter "/*/*/GoldenSchemaTests/Regenerate_expected_MCP_schema_outputs"
    /// </summary>
    [Test]
    [MethodDataSource(typeof(Specs), nameof(Specs.Files))]
    public async Task Regenerate_expected_MCP_schema_outputs(string name, string _)
    {
        if (Environment.GetEnvironmentVariable("MCP_REGENERATE_GOLDENS") != "1")
            return; // no-op unless explicitly regenerating

        var sourceDir = Path.Combine(Specs.Dir, "expected");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, Path.GetFileNameWithoutExtension(name) + ".mcp.json"), await SerializeToolsWithMcpLibrary(name));
    }
}
