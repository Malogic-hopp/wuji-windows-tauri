using System.IO;
using System.Reflection;
using System.Xml.Linq;
using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Agent;
using QuantifiedSelf.Windows.ApplicationLayer.Agent;
using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Data;
using QuantifiedSelf.Windows.ApplicationLayer.Activity;
using QuantifiedSelf.Windows.ApplicationLayer.Analytics;
using QuantifiedSelf.Windows.ApplicationLayer.Contracts.Agent;
using QuantifiedSelf.Windows.ApplicationLayer.Contracts.Activity;
using QuantifiedSelf.Windows.ApplicationLayer.Models;
using QuantifiedSelf.Windows.App.Services;
using QuantifiedSelf.Windows.App.ViewModels;
using QuantifiedSelf.Windows.Client.Agent;
using QuantifiedSelf.Windows.Infrastructure.Database;
using QuantifiedSelf.Windows.Infrastructure.Ipc;
using QuantifiedSelf.Windows.Infrastructure.RuntimeState;

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

    [Fact]
    public void App_ProjectReferencesClientCompositionLayer()
    {
        var projectPath = GetProjectPath(
            "QuantifiedSelf.Windows.App",
            "QuantifiedSelf.Windows.App.csproj");

        Assert.Contains("QuantifiedSelf.Windows.Client", GetProjectReferences(projectPath));
    }

    [Fact]
    public void Infrastructure_ProjectReferencesApplicationAndCore()
    {
        var projectPath = GetProjectPath(
            "QuantifiedSelf.Windows.Infrastructure",
            "QuantifiedSelf.Windows.Infrastructure.csproj");

        Assert.Equal(
            [
                "QuantifiedSelf.Windows.Application",
                "QuantifiedSelf.Windows.Core"
            ],
            GetProjectReferences(projectPath));
    }

    [Theory]
    [InlineData("QuantifiedSelf.Windows.Application", "QuantifiedSelf.Windows.Application.csproj")]
    [InlineData("QuantifiedSelf.Windows.Client", "QuantifiedSelf.Windows.Client.csproj")]
    [InlineData("QuantifiedSelf.Windows.Infrastructure", "QuantifiedSelf.Windows.Infrastructure.csproj")]
    public void HeadlessProjects_SourceAndProjectFilesContainNoUiFrameworkReferences(
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

    [Fact]
    public void ActivityPortsContractsAndUseCases_AreOwnedByApplicationAssembly()
    {
        var applicationAssembly = typeof(AgentStatusSnapshot).Assembly;

        Assert.Same(applicationAssembly, typeof(IOverviewQueryPort).Assembly);
        Assert.Same(applicationAssembly, typeof(IDailyStatsQueryPort).Assembly);
        Assert.Same(applicationAssembly, typeof(IOverviewDataService).Assembly);
        Assert.Same(applicationAssembly, typeof(DailyStatsService).Assembly);
        Assert.Same(applicationAssembly, typeof(FocusInterruptionInsightService).Assembly);
        Assert.Same(applicationAssembly, typeof(HourActivityHeatmapResult).Assembly);
    }

    [Fact]
    public void AgentPortsContractsAndServices_AreOwnedByApplicationAssembly()
    {
        var applicationAssembly = typeof(AgentStatusSnapshot).Assembly;

        Assert.Same(applicationAssembly, typeof(IAgentTransport).Assembly);
        Assert.Same(applicationAssembly, typeof(IAgentRuntimeStateReader).Assembly);
        Assert.Same(applicationAssembly, typeof(IAgentHealthStateReader).Assembly);
        Assert.Same(applicationAssembly, typeof(IAgentControlFallback).Assembly);
        Assert.Same(applicationAssembly, typeof(IAgentOptionsReader).Assembly);
        Assert.Same(applicationAssembly, typeof(IAgentProcessController).Assembly);
        Assert.Same(applicationAssembly, typeof(IAgentStatusService).Assembly);
        Assert.Same(applicationAssembly, typeof(IAgentControlService).Assembly);
        Assert.Same(applicationAssembly, typeof(IAgentProcessService).Assembly);
        Assert.Same(applicationAssembly, typeof(IAgentTransportHealthService).Assembly);
        Assert.Same(applicationAssembly, typeof(AgentStatusService).Assembly);
        Assert.Same(applicationAssembly, typeof(AgentControlService).Assembly);
        Assert.Same(applicationAssembly, typeof(AgentProcessService).Assembly);
        Assert.Same(applicationAssembly, typeof(AgentTransportHealthService).Assembly);
        Assert.Same(applicationAssembly, typeof(AgentTransportHealthSnapshot).Assembly);
    }

    [Fact]
    public void WindowsAgentProcessController_IsOwnedByClientAndImplementsApplicationPort()
    {
        var controllerType = typeof(WindowsAgentProcessController);

        Assert.Equal("QuantifiedSelf.Windows.Client", controllerType.Assembly.GetName().Name);
        Assert.True(typeof(IAgentProcessController).IsAssignableFrom(controllerType));
    }

    [Fact]
    public void InfrastructureAgentAdapters_ImplementApplicationPorts()
    {
        Assert.True(typeof(IAgentTransport).IsAssignableFrom(typeof(NamedPipeAgentControlClient)));
        Assert.True(typeof(IAgentRuntimeStateReader).IsAssignableFrom(typeof(FileAgentStateAdapter)));
        Assert.True(typeof(IAgentHealthStateReader).IsAssignableFrom(typeof(FileAgentStateAdapter)));
        Assert.True(typeof(IAgentControlFallback).IsAssignableFrom(typeof(FileAgentStateAdapter)));
        Assert.True(typeof(IAgentOptionsReader).IsAssignableFrom(typeof(FileAgentStateAdapter)));
    }

    [Fact]
    public void SqliteActivityAdapter_IsOwnedByInfrastructureAndImplementsAllQueryPorts()
    {
        var adapterType = typeof(SqliteActivityQueryAdapter);

        Assert.Equal("QuantifiedSelf.Windows.Infrastructure", adapterType.Assembly.GetName().Name);
        Assert.True(typeof(IOverviewQueryPort).IsAssignableFrom(adapterType));
        Assert.True(typeof(IDiagnosticsQueryPort).IsAssignableFrom(adapterType));
        Assert.True(typeof(ISampleQueryPort).IsAssignableFrom(adapterType));
        Assert.True(typeof(ISessionQueryPort).IsAssignableFrom(adapterType));
        Assert.True(typeof(IAppUsageQueryPort).IsAssignableFrom(adapterType));
        Assert.True(typeof(IDailyStatsQueryPort).IsAssignableFrom(adapterType));
    }

    [Fact]
    public void HeatmapUseCase_ReturnsFrameworkIndependentApplicationContract()
    {
        var method = typeof(IHourActivityHeatmapService).GetMethod(nameof(IHourActivityHeatmapService.GetHeatmapAsync));

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<HourActivityHeatmapResult>), method!.ReturnType);
        Assert.Equal(typeof(HourActivityHeatmapResult).Assembly, method.ReturnType.GenericTypeArguments[0].Assembly);
    }

    [Fact]
    public void AppServicesDirectory_ContainsNoMigratedActivityServices()
    {
        string[] migratedServices =
        [
            "OverviewDataService.cs",
            "DiagnosticsDataService.cs",
            "SamplesDataService.cs",
            "SessionsDataService.cs",
            "AppsDataService.cs",
            "DailyStatsService.cs",
            "WeeklyTrendService.cs",
            "FocusInterruptionInsightService.cs",
            "HourActivityHeatmapService.cs"
        ];

        var servicesDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "QuantifiedSelf.Windows.App",
            "Services");

        foreach (var serviceFile in migratedServices)
        {
            Assert.False(
                File.Exists(Path.Combine(servicesDirectory, serviceFile)),
                $"Migrated activity service must not remain in App: {serviceFile}");
        }
    }

    [Fact]
    public void AppServicesDirectory_ContainsNoMigratedAgentServices()
    {
        string[] migratedServices =
        [
            "AgentControlService.cs",
            "AgentIpcStatusService.cs",
            "AgentProcessService.cs",
            "AgentStatusService.cs"
        ];

        var servicesDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "QuantifiedSelf.Windows.App",
            "Services");

        foreach (var serviceFile in migratedServices)
        {
            Assert.False(
                File.Exists(Path.Combine(servicesDirectory, serviceFile)),
                $"Migrated Agent service must not remain in App: {serviceFile}");
        }
    }

    [Fact]
    public void AppSource_ContainsNoLegacyAgentIpcClientReference()
    {
        var appDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "QuantifiedSelf.Windows.App");

        var sourceFiles = Directory
            .EnumerateFiles(appDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path));

        foreach (var sourceFile in sourceFiles)
        {
            Assert.DoesNotContain(
                "IAgentIpcClient",
                File.ReadAllText(sourceFile),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WpfActivityViewModels_AcceptApplicationUseCaseInterfaces()
    {
        AssertConstructorAccepts<AppsViewModel, IAppsDataService>();
        AssertConstructorAccepts<SamplesViewModel, ISamplesDataService>();
        AssertConstructorAccepts<SessionsViewModel, ISessionsDataService>();
        AssertConstructorAccepts<InsightsViewModel, IFocusInterruptionInsightService>();
        AssertConstructorAccepts<DashboardViewModel, IDailyStatsService>();
        AssertConstructorAccepts<DashboardViewModel, IWeeklyTrendService>();
        AssertConstructorAccepts<DashboardViewModel, IHourActivityHeatmapService>();
        AssertConstructorAccepts<MainWindowViewModel, IOverviewDataService>();
        AssertConstructorAccepts<MainWindowViewModel, IDiagnosticsDataService>();
        AssertConstructorAccepts<SettingsViewModel, IDiagnosticsDataService>();
    }

    [Fact]
    public void WpfConsumers_AcceptApplicationAgentInterfaces()
    {
        AssertConstructorAccepts<MainWindowViewModel, IAgentProcessService>();
        AssertConstructorAccepts<MainWindowViewModel, IAgentControlService>();
        AssertConstructorAccepts<MainWindowViewModel, IAgentStatusService>();
        AssertConstructorAccepts<MainWindowViewModel, IAgentTransportHealthService>();
        AssertConstructorAccepts<SettingsViewModel, IAgentStatusService>();
        AssertConstructorAccepts<SettingsViewModel, IAgentControlService>();
        AssertConstructorAccepts<RefreshService, IAgentStatusService>();
        AssertConstructorAccepts<RefreshService, IAgentProcessService>();
    }

    private static void AssertConstructorAccepts<TConsumer, TDependency>()
    {
        var parameterTypes = typeof(TConsumer)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType);

        Assert.Contains(typeof(TDependency), parameterTypes);
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
