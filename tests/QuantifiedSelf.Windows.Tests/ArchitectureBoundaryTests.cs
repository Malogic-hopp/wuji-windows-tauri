using System.IO;
using System.Reflection;
using System.Xml.Linq;
using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Agent;
using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Settings;
using QuantifiedSelf.Windows.ApplicationLayer.Agent;
using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Data;
using QuantifiedSelf.Windows.ApplicationLayer.Activity;
using QuantifiedSelf.Windows.ApplicationLayer.Analytics;
using QuantifiedSelf.Windows.ApplicationLayer.Contracts.Agent;
using QuantifiedSelf.Windows.ApplicationLayer.Contracts.Activity;
using QuantifiedSelf.Windows.ApplicationLayer.Models;
using QuantifiedSelf.Windows.ApplicationLayer.Settings;
using QuantifiedSelf.Windows.App.Services;
using QuantifiedSelf.Windows.App.ViewModels;
using QuantifiedSelf.Windows.Client;
using QuantifiedSelf.Windows.Client.Agent;
using QuantifiedSelf.Windows.Client.Settings;
using QuantifiedSelf.Windows.Client.Startup;
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
        Assert.Contains("QuantifiedSelf.Windows.Client.Bridge", solution, StringComparison.Ordinal);
        Assert.Contains("QuantifiedSelf.Windows.Agent.Runtime", solution, StringComparison.Ordinal);
        Assert.Contains("QuantifiedSelf.Windows.Bridge.ContractGen", solution, StringComparison.Ordinal);
    }

    [Fact]
    public void Solution_IncludesLayeredTestProjects()
    {
        var solution = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "QuantifiedSelf.Windows.sln"));
        string[] projectNames =
        [
            "QuantifiedSelf.Windows.Core.Tests",
            "QuantifiedSelf.Windows.Application.Tests",
            "QuantifiedSelf.Windows.Infrastructure.Tests",
            "QuantifiedSelf.Windows.Client.Tests",
            "QuantifiedSelf.Windows.Client.Bridge.Tests",
            "QuantifiedSelf.Windows.App.Tests",
            "QuantifiedSelf.Windows.Agent.Tests"
        ];

        foreach (var projectName in projectNames)
        {
            Assert.Contains(projectName, solution, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LayeredTestProjects_HaveExpectedTargetsAndReferences()
    {
        var expectations = new[]
        {
            new TestProjectExpectation(
                "QuantifiedSelf.Windows.Core.Tests",
                "net8.0",
                ["QuantifiedSelf.Windows.Core"]),
            new TestProjectExpectation(
                "QuantifiedSelf.Windows.Application.Tests",
                "net8.0",
                ["QuantifiedSelf.Windows.Application", "QuantifiedSelf.Windows.Core"]),
            new TestProjectExpectation(
                "QuantifiedSelf.Windows.Infrastructure.Tests",
                "net8.0-windows",
                ["QuantifiedSelf.Windows.Core", "QuantifiedSelf.Windows.Infrastructure"]),
            new TestProjectExpectation(
                "QuantifiedSelf.Windows.Client.Tests",
                "net8.0-windows10.0.19041",
                ["QuantifiedSelf.Windows.Client", "QuantifiedSelf.Windows.Core"]),
            new TestProjectExpectation(
                "QuantifiedSelf.Windows.Client.Bridge.Tests",
                "net8.0-windows10.0.19041",
                [
                    "QuantifiedSelf.Windows.Bridge.ContractGen",
                    "QuantifiedSelf.Windows.Client",
                    "QuantifiedSelf.Windows.Client.Bridge"
                ]),
            new TestProjectExpectation(
                "QuantifiedSelf.Windows.App.Tests",
                "net8.0-windows10.0.19041",
                ["QuantifiedSelf.Windows.App", "QuantifiedSelf.Windows.Core"]),
            new TestProjectExpectation(
                "QuantifiedSelf.Windows.Agent.Tests",
                "net8.0-windows",
                [
                    "QuantifiedSelf.Windows.Agent",
                    "QuantifiedSelf.Windows.Agent.Runtime",
                    "QuantifiedSelf.Windows.Core"
                ])
        };

        foreach (var expectation in expectations)
        {
            var projectPath = GetTestProjectPath(expectation.ProjectName);
            Assert.Equal(expectation.TargetFramework, GetTargetFramework(projectPath));
            Assert.Equal(expectation.ProjectReferences, GetProjectReferences(projectPath));
        }
    }

    [Theory]
    [InlineData("QuantifiedSelf.Windows.Core.Tests")]
    [InlineData("QuantifiedSelf.Windows.Application.Tests")]
    public void HeadlessFastTestProjects_ContainNoUiFrameworkMarkers(string projectName)
    {
        var projectPath = GetTestProjectPath(projectName);
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var files = Directory
            .EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsBuildOutput(path));

        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            foreach (var marker in UiSourceMarkers)
            {
                Assert.DoesNotContain(marker, content, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void LegacyMixedTestProject_ExcludesMigratedClientAndAppFiles()
    {
        var projectPath = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "QuantifiedSelf.Windows.Tests",
            "QuantifiedSelf.Windows.Tests.csproj");
        var document = XDocument.Load(projectPath);
        var removedFiles = document
            .Descendants("Compile")
            .Select(element => element.Attribute("Remove")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("AdaptiveLayoutTests.cs", removedFiles);
        Assert.Contains("AgentExeLocatorTests.cs", removedFiles);
        Assert.Contains("InsightsTests.cs", removedFiles);
        Assert.Contains("TodayPageTests.cs", removedFiles);
        Assert.Contains("WujiClientTests.cs", removedFiles);
    }

    [Fact]
    public void OrdinaryTestProjects_DoNotReferenceAgentExecutableProject()
    {
        var testsDirectory = Path.Combine(FindRepositoryRoot(), "tests");
        var ordinaryProjects = Directory
            .EnumerateFiles(testsDirectory, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}QuantifiedSelf.Windows.Agent.Tests{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase));

        foreach (var projectPath in ordinaryProjects)
        {
            Assert.DoesNotContain(
                "QuantifiedSelf.Windows.Agent",
                GetProjectReferences(projectPath));
        }
    }

    [Fact]
    public void OrdinaryTestSources_DoNotStartOperatingSystemProcesses()
    {
        var testsDirectory = Path.Combine(FindRepositoryRoot(), "tests");
        var forbiddenCall = string.Concat("Process", ".Start(");
        var sourceFiles = Directory
            .EnumerateFiles(testsDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}QuantifiedSelf.Windows.Agent.Tests{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsBuildOutput(path));

        foreach (var sourceFile in sourceFiles)
        {
            Assert.DoesNotContain(
                forbiddenCall,
                File.ReadAllText(sourceFile),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LegacyMixedTestOutput_DoesNotContainAgentAppHost()
    {
        Assert.False(File.Exists(Path.Combine(
            AppContext.BaseDirectory,
            "QuantifiedSelf.Windows.Agent.exe")));
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
    public void Bridge_ProjectTargetsWindowsAndReferencesOnlyClient()
    {
        var projectPath = GetProjectPath(
            "QuantifiedSelf.Windows.Client.Bridge",
            "QuantifiedSelf.Windows.Client.Bridge.csproj");

        Assert.Equal("net8.0-windows10.0.19041", GetTargetFramework(projectPath));
        Assert.Equal(
            ["QuantifiedSelf.Windows.Client"],
            GetProjectReferences(projectPath));
    }

    [Fact]
    public void ContractGenerator_TargetsNet8AndHasNoProjectReferences()
    {
        var projectPath = Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "QuantifiedSelf.Windows.Bridge.ContractGen",
            "QuantifiedSelf.Windows.Bridge.ContractGen.csproj");

        Assert.Equal("net8.0", GetTargetFramework(projectPath));
        Assert.Empty(GetProjectReferences(projectPath));
    }

    [Fact]
    public void App_ProjectReferencesOnlyPresentationContracts()
    {
        var projectPath = GetProjectPath(
            "QuantifiedSelf.Windows.App",
            "QuantifiedSelf.Windows.App.csproj");

        Assert.Equal(
            [
                "QuantifiedSelf.Windows.Application",
                "QuantifiedSelf.Windows.Client",
                "QuantifiedSelf.Windows.Core"
            ],
            GetProjectReferences(projectPath));
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

    [Fact]
    public void AgentRuntime_IsHeadlessLibraryWithNoExecutableAppHost()
    {
        var projectPath = GetProjectPath(
            "QuantifiedSelf.Windows.Agent.Runtime",
            "QuantifiedSelf.Windows.Agent.Runtime.csproj");

        Assert.Equal("net8.0-windows", GetTargetFramework(projectPath));
        Assert.Equal(
            [
                "QuantifiedSelf.Windows.Core",
                "QuantifiedSelf.Windows.Infrastructure"
            ],
            GetProjectReferences(projectPath));

        var document = XDocument.Load(projectPath);
        var outputType = document
            .Descendants("OutputType")
            .Select(element => element.Value)
            .SingleOrDefault();
        Assert.True(string.IsNullOrWhiteSpace(outputType)
            || string.Equals(outputType, "Library", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("QuantifiedSelf.Windows.Application", "QuantifiedSelf.Windows.Application.csproj")]
    [InlineData("QuantifiedSelf.Windows.Client", "QuantifiedSelf.Windows.Client.csproj")]
    [InlineData("QuantifiedSelf.Windows.Infrastructure", "QuantifiedSelf.Windows.Infrastructure.csproj")]
    [InlineData("QuantifiedSelf.Windows.Client.Bridge", "QuantifiedSelf.Windows.Client.Bridge.csproj")]
    [InlineData("QuantifiedSelf.Windows.Agent.Runtime", "QuantifiedSelf.Windows.Agent.Runtime.csproj")]
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
    public void SettingsPortsAndUseCase_AreOwnedByApplicationAssembly()
    {
        var applicationAssembly = typeof(AgentStatusSnapshot).Assembly;

        Assert.Same(applicationAssembly, typeof(IAppSettingsStore).Assembly);
        Assert.Same(applicationAssembly, typeof(IAgentOptionsStore).Assembly);
        Assert.Same(applicationAssembly, typeof(ISettingsService).Assembly);
        Assert.Same(applicationAssembly, typeof(SettingsService).Assembly);
        Assert.Same(applicationAssembly, typeof(ClientSettingsSnapshot).Assembly);
        Assert.Same(applicationAssembly, typeof(ClientSettingsUpdateResult).Assembly);
    }

    [Fact]
    public void ClientSettingsUseCases_ReturnFrameworkIndependentSafeContracts()
    {
        var getMethod = typeof(ISettingsService).GetMethod(nameof(ISettingsService.GetClientSettingsAsync));
        var updateMethod = typeof(ISettingsService).GetMethod(nameof(ISettingsService.UpdateClientSettingsAsync));

        Assert.NotNull(getMethod);
        Assert.NotNull(updateMethod);
        Assert.Equal(typeof(Task<ClientSettingsSnapshot>), getMethod!.ReturnType);
        Assert.Equal(typeof(ClientSettingsSnapshot), updateMethod!.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(Task<ClientSettingsUpdateResult>), updateMethod.ReturnType);

        var publicContract = string.Join(
            Environment.NewLine,
            typeof(ClientAppSettings).GetProperties().Select(property => property.Name)
                .Concat(typeof(ClientAgentOptions).GetProperties().Select(property => property.Name)));
        string[] forbidden =
        [
            "StartAppOnWindowsLogin", "LastSelectedPage", "ExcludedProcesses", "ExcludedTitlePatterns",
            "UseMockCapture", "DataRoot", "DatabasePath", "ExecutablePath", "PipeName", "Registry"
        ];
        foreach (var marker in forbidden)
        {
            Assert.DoesNotContain(marker, publicContract, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void WindowsAgentProcessController_IsOwnedByClientAndImplementsApplicationPort()
    {
        var controllerType = typeof(WindowsAgentProcessController);

        Assert.Equal("QuantifiedSelf.Windows.Client", controllerType.Assembly.GetName().Name);
        Assert.True(typeof(IAgentProcessController).IsAssignableFrom(controllerType));
    }

    [Fact]
    public void WindowsSettingsStoreAdapter_IsOwnedByClientAndImplementsApplicationPorts()
    {
        var adapterType = typeof(WindowsSettingsStoreAdapter);

        Assert.Equal("QuantifiedSelf.Windows.Client", adapterType.Assembly.GetName().Name);
        Assert.True(typeof(IAppSettingsStore).IsAssignableFrom(adapterType));
        Assert.True(typeof(IAgentOptionsStore).IsAssignableFrom(adapterType));
    }

    [Fact]
    public void StartupSdkTypes_AreOwnedByClientAssembly()
    {
        var clientAssembly = typeof(WindowsAgentProcessController).Assembly;

        Assert.Same(clientAssembly, typeof(IStartupRegistry).Assembly);
        Assert.Same(clientAssembly, typeof(RegistryStartupRegistry).Assembly);
        Assert.Same(clientAssembly, typeof(IStartupRegistrationService).Assembly);
        Assert.Same(clientAssembly, typeof(StartupRegistrationService).Assembly);
        Assert.Same(clientAssembly, typeof(StartupRegistrationStatus).Assembly);
        Assert.Same(clientAssembly, typeof(StartupCommandBuilder).Assembly);
        Assert.Same(clientAssembly, typeof(StartupLaunchOptions).Assembly);
    }

    [Fact]
    public void ClientFacadeAndFeatureInterfaces_AreOwnedByClientAssembly()
    {
        var clientAssembly = typeof(WujiClientFactory).Assembly;

        Assert.Same(clientAssembly, typeof(IWujiClient).Assembly);
        Assert.Same(clientAssembly, typeof(IAgentClient).Assembly);
        Assert.Same(clientAssembly, typeof(IActivityClient).Assembly);
        Assert.Same(clientAssembly, typeof(IDiagnosticsClient).Assembly);
        Assert.Same(clientAssembly, typeof(ISettingsClient).Assembly);
        Assert.Same(clientAssembly, typeof(IStartupClient).Assembly);
        Assert.Same(clientAssembly, typeof(WujiClientOptions).Assembly);
        Assert.Same(clientAssembly, typeof(WujiClientContext).Assembly);
        Assert.Same(clientAssembly, typeof(WujiClientPaths).Assembly);
    }

    [Fact]
    public void ClientPublicPathContract_DoesNotExposeCorePathImplementation()
    {
        var publicMethods = typeof(WujiClientPaths)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);

        Assert.DoesNotContain(
            publicMethods,
            method => method.ReturnType.FullName == "QuantifiedSelf.Windows.Core.Paths.WindowsAgentPaths"
                || method.GetParameters().Any(parameter =>
                    parameter.ParameterType.FullName == "QuantifiedSelf.Windows.Core.Paths.WindowsAgentPaths"));
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
    public void AppServicesDirectory_ContainsNoMigratedSettingsOrStartupServices()
    {
        string[] migratedServices =
        [
            "SettingsService.cs",
            "IStartupRegistry.cs",
            "RegistryStartupRegistry.cs",
            "IStartupRegistrationService.cs",
            "StartupRegistrationService.cs",
            "StartupRegistrationStatus.cs",
            "StartupCommandBuilder.cs",
            "StartupLaunchOptions.cs"
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
                $"Migrated settings/startup service must not remain in App: {serviceFile}");
        }

        Assert.Equal(
            "QuantifiedSelf.Windows.App",
            typeof(StartupRegistrationDisplayModel).Assembly.GetName().Name);
        Assert.Equal(
            "QuantifiedSelf.Windows.App",
            typeof(WindowStartupPolicy).Assembly.GetName().Name);
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
    public void AppSource_ContainsNoInfrastructureAgentOrCompositionImplementationReferences()
    {
        string[] forbiddenMarkers =
        [
            "QuantifiedSelf.Windows.Infrastructure",
            "QuantifiedSelf.Windows.Agent",
            "RuntimeStateStore",
            "AgentHealthStateStore",
            "AgentControlFileStore",
            "NamedPipeAgentControlClient",
            "SqliteActivityQueryAdapter",
            "WindowsAgentProcessController",
            "RegistryStartupRegistry",
            "WindowsSettingsStoreAdapter"
        ];

        var appDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "QuantifiedSelf.Windows.App");
        var sourceFiles = Directory
            .EnumerateFiles(appDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path));

        foreach (var sourceFile in sourceFiles)
        {
            var source = File.ReadAllText(sourceFile);
            foreach (var marker in forbiddenMarkers)
            {
                Assert.DoesNotContain(marker, source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void BridgeSource_ContainsNoInfrastructureAgentAppOrUiReferences()
    {
        string[] forbiddenMarkers =
        [
            "QuantifiedSelf.Windows.Infrastructure",
            "QuantifiedSelf.Windows.Agent",
            "QuantifiedSelf.Windows.App.",
            "QuantifiedSelf.Windows.App\\",
            "System.Windows",
            "System.Windows.Forms",
            "LiveChartsCore",
            "SkiaSharp"
        ];

        var bridgeDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "QuantifiedSelf.Windows.Client.Bridge");
        var sourceFiles = Directory
            .EnumerateFiles(bridgeDirectory, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsBuildOutput(path));

        foreach (var sourceFile in sourceFiles)
        {
            var source = File.ReadAllText(sourceFile);
            foreach (var marker in forbiddenMarkers)
            {
                Assert.DoesNotContain(marker, source, StringComparison.Ordinal);
            }
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

    [Fact]
    public void AgentStopOrchestration_IsOwnedByApplicationAndWpfUsesUnifiedMethod()
    {
        var stopMethod = typeof(IAgentProcessService).GetMethod(nameof(IAgentProcessService.StopAgentAsync));
        Assert.NotNull(stopMethod);
        Assert.Equal(typeof(Task<AgentStopResult>), stopMethod!.ReturnType);
        Assert.Null(typeof(IAgentProcessService).GetMethod("StopAgentGracefullyAsync"));
        Assert.Null(typeof(IAgentProcessService).GetMethod("KillAgentAsFallbackAsync"));

        var viewModelSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "QuantifiedSelf.Windows.App",
            "ViewModels",
            "MainWindowViewModel.cs"));
        Assert.Contains("_processService.StopAgentAsync()", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("StopAgentGracefullyAsync", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("KillAgentAsFallbackAsync", viewModelSource, StringComparison.Ordinal);
    }

    [Fact]
    public void WpfSettingsConsumers_UseApplicationAndClientInterfaces()
    {
        AssertConstructorAccepts<MainWindowViewModel, ISettingsService>();
        AssertConstructorAccepts<SettingsViewModel, ISettingsService>();

        AssertPropertyHasType<MainWindowViewModel, IStartupRegistrationService>(
            "StartupRegistrationService");
        AssertPropertyHasType<SettingsViewModel, IStartupRegistrationService>(
            "StartupRegistrationService");
    }

    [Fact]
    public void WpfCompositionAcceptsClientFeatureInterfaces()
    {
        AssertConstructorAccepts<SamplesViewModel, IActivityClient>();
        AssertConstructorAccepts<SessionsViewModel, IActivityClient>();
        AssertConstructorAccepts<AppsViewModel, IActivityClient>();
        AssertConstructorAccepts<DashboardViewModel, IActivityClient>();
        AssertConstructorAccepts<InsightsViewModel, IActivityClient>();
        AssertConstructorAccepts<SettingsViewModel, ISettingsClient>();
        AssertConstructorAccepts<SettingsViewModel, IAgentClient>();
        AssertConstructorAccepts<SettingsViewModel, IDiagnosticsClient>();
        AssertConstructorAccepts<SettingsViewModel, IStartupClient>();
        AssertConstructorAccepts<MainWindowViewModel, IAgentClient>();
        AssertConstructorAccepts<MainWindowViewModel, IActivityClient>();
        AssertConstructorAccepts<MainWindowViewModel, IDiagnosticsClient>();
        AssertConstructorAccepts<MainWindowViewModel, ISettingsClient>();
        AssertConstructorAccepts<MainWindowViewModel, IStartupClient>();
        AssertConstructorAccepts<RefreshService, IAgentClient>();
    }

    private static void AssertConstructorAccepts<TConsumer, TDependency>()
    {
        var parameterTypes = typeof(TConsumer)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType);

        Assert.Contains(typeof(TDependency), parameterTypes);
    }

    private static void AssertPropertyHasType<TConsumer, TDependency>(string propertyName)
    {
        var property = typeof(TConsumer).GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotNull(property);
        Assert.Equal(typeof(TDependency), property!.PropertyType);
    }

    private static string GetProjectPath(string projectDirectory, string projectFileName)
        => Path.Combine(FindRepositoryRoot(), "src", projectDirectory, projectFileName);

    private static string GetTestProjectPath(string projectName)
        => Path.Combine(FindRepositoryRoot(), "tests", projectName, $"{projectName}.csproj");

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

    private sealed record TestProjectExpectation(
        string ProjectName,
        string TargetFramework,
        string[] ProjectReferences);
}
