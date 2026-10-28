namespace Enframe.Mcp.Tests;

/// <summary>
/// Spec files under test, copied from ..\specs to the test output dir.
/// New spec files dropped in ..\specs are picked up automatically.
/// </summary>
public static class Specs
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Specs");

    public static string File(string fileName) => System.IO.Path.Combine(Dir, fileName);

    public static IEnumerable<(string Name, string Path)> Files()
        => Directory.EnumerateFiles(Dir, "*.json")
            .Select(f => (Name: System.IO.Path.GetFileName(f), Path: f));
}
