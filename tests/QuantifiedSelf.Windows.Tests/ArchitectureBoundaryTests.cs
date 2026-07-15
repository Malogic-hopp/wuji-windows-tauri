using System.IO;
using System.Reflection;
using System.Xml.Linq;
using QuantifiedSelf.Windows.ApplicationLayer.Analytics;
using QuantifiedSelf.Windows.ApplicationLayer.Models;

namespace QuantifiedSelf.Windows.Tests;

[Trait("Category", "Fast")]
public sealed class ArchitectureBoundaryTests
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

    private static readonly string[] UiSourceMarkers =
    [
        "System.Windows",
        "System.Windows.Forms",
        "LiveChartsCore",
        "SkiaSharp",
        "<UseWPF>true</UseWPF>",
        "<UseWindowsForms>true</UseWindowsForms>"
    ];

    [Fact]
    public void Solution_IncludesApplicationAndClientProjects()
    {
        var solution = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "QuantifiedSelf.Windows.sln"));

        Assert.Contains("QuantifiedSelf.Windows.Application", solution, StringComparison.Ordinal);
        Assert.Contains("QuantifiedSelf.Windows.Client", solution, StringComparison.Ordinal);
    }

    [Fact]
    public void Application_ProjectTargetsNet8AndReferencesOnlyCore()
    {
        var projectPath = GetProjectPath(
            "QuantifiedSelf.Windows.Application",
            "QuantifiedSelf.Windows.Application.csproj");

        Assert.Equal("net8.0", GetTargetFramework(projectPath));
        Assert.Equal(
            ["QuantifiedSelf.Windows.Core"],
            GetProjectReferences(projectPath));
    }

    [Fact]
    public void Client_ProjectTargetsWindowsAndReferencesExpectedFoundationProjects()
    {
        var projectPath = GetProjectPath(
            "QuantifiedSelf.Windows.Client",
            "QuantifiedSelf.Windows.Client.csproj");

        Assert.Equal("net8.0-windows10.0.19041", GetTargetFramework(projectPath));
        Assert.Equal(
            [
                "QuantifiedSelf.Windows.Application",
                "QuantifiedSelf.Windows.Core",
                "QuantifiedSelf.Windows.Infrastructure"
            ],
            GetProjectReferences(projectPath));
    }

    [Theory]
    [InlineData("QuantifiedSelf.Windows.Application", "QuantifiedSelf.Windows.Application.csproj")]
    [InlineData("QuantifiedSelf.Windows.Client", "QuantifiedSelf.Windows.Client.csproj")]
    public void ApplicationAndClient_SourceAndProjectFilesContainNoUiFrameworkReferences(
        string projectDirectory,
        string projectFileName)
    {
        var projectPath = GetProjectPath(projectDirectory, projectFileName);
        var files = Directory
            .EnumerateFiles(Path.GetDirectoryName(projectPath)!, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsBuildOutput(path));

        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            foreach (var marker in UiSourceMarkers)
            {
                Assert.False(
                    content.Contains(marker, StringComparison.OrdinalIgnoreCase),
                    $"UI framework marker '{marker}' must not appear in {Path.GetRelativePath(FindRepositoryRoot(), file)}.");
            }
        }
    }

    [Fact]
    public void ApplicationAssembly_ReferencesNoUiFrameworkAssemblies()
    {
        var applicationAssembly = typeof(AgentStatusSnapshot).Assembly;
        var referencedAssemblies = applicationAssembly
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var forbiddenAssembly in UiAssemblyNames)
        {
            Assert.DoesNotContain(forbiddenAssembly, referencedAssemblies);
        }
    }

    [Fact]
    public void MigratedModelsAndCalculators_AreOwnedByApplicationAssembly()
    {
        var applicationAssembly = typeof(AgentStatusSnapshot).Assembly;

        Assert.Same(applicationAssembly, typeof(AgentProcessInfo).Assembly);
        Assert.Same(applicationAssembly, typeof(FocusMetricsCalculator).Assembly);
        Assert.Same(applicationAssembly, typeof(HourActivityHeatmapCalculator).Assembly);
        Assert.Same(applicationAssembly, typeof(InsightSuggestionEngine).Assembly);
    }

    private static string GetProjectPath(string projectDirectory, string projectFileName)
        => Path.Combine(FindRepositoryRoot(), "src", projectDirectory, projectFileName);

    private static string GetTargetFramework(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        return document.Descendants("TargetFramework").Single().Value.Trim();
    }

    private static string[] GetProjectReferences(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        return document
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFileNameWithoutExtension(include!))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsBuildOutput(string path)
    {
        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var separator = Path.DirectorySeparatorChar;
        return normalized.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase);
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

        throw new DirectoryNotFoundException("Unable to locate the repository root from the test output directory.");
    }
}
