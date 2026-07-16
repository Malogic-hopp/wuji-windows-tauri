using System.Text.Json;
using QuantifiedSelf.Windows.Bridge.ContractGen;

namespace QuantifiedSelf.Windows.Client.Bridge.Tests;

[Trait("Category", "Fast")]
public sealed class BridgeContractTests
{
    private static readonly string[] UiAssemblyNames =
    [
        "PresentationCore",
        "PresentationFramework",
        "WindowsBase",
        "System.Windows.Forms",
        "LiveChartsCore",
        "LiveChartsCore.SkiaSharpView.WPF",
        "SkiaSharp"
    ];

    [Fact]
    public void BridgeAssembly_ReferencesNoDesktopUiFrameworks()
    {
        var references = typeof(BridgeHost).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var uiAssemblyName in UiAssemblyNames)
        {
            Assert.DoesNotContain(uiAssemblyName, references);
        }
    }

    [Fact]
    public void GeneratedArtifacts_MatchCanonicalSchema()
    {
        var drift = ContractGenerator.FindDrift(FindRepositoryRoot());

        Assert.Empty(drift);
    }

    [Fact]
    public void Schema_ContainsOnlyStageOneMethodsAndStableErrors()
    {
        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "contracts",
            "wuji-bridge",
            "v1",
            "bridge.schema.json");
        using var document = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var root = document.RootElement;

        Assert.Equal("1.0", root.GetProperty("x-wuji-api-version").GetString());
        Assert.Equal(
            ["bridge.hello", "client.initialize", "bridge.shutdown"],
            root.GetProperty("x-wuji-methods")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray());
        Assert.Contains(
            "request_timeout",
            root.GetProperty("x-wuji-error-codes")
                .EnumerateArray()
                .Select(value => value.GetString()));
    }

    [Fact]
    public void GeneratedArtifacts_ContainNoMachineSpecificAbsolutePaths()
    {
        var repositoryRoot = FindRepositoryRoot();
        foreach (var artifact in ContractGenerator.Generate(repositoryRoot))
        {
            Assert.DoesNotContain(repositoryRoot, artifact.Content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C:\\Users\\", artifact.Content, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QuantifiedSelf.Windows.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate repository root.");
    }
}
