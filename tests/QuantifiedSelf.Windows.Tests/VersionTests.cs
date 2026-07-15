using System.Reflection;
using QuantifiedSelf.Windows.Client.Agent;
using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Core.Options;

namespace QuantifiedSelf.Windows.Tests;

/// <summary>
/// Verify that Directory.Build.props sets the unified version 0.1.0
/// on all projects in the solution.
/// </summary>
[Trait("Category", "Fast")]
public sealed class VersionTests
{
    [Trait("Category", "Fast")]
    [Trait("Category", "Fast")]
    [Fact]
    public void CoreAssemblyVersion_Is_0_1_0_0()
    {
        var version = typeof(AppSettings).Assembly.GetName().Version;
        Assert.NotNull(version);
        Assert.Equal(0, version!.Major);
        Assert.Equal(1, version.Minor);
        Assert.Equal(0, version.Build);
        // Revision is typically 0 unless specified
    }

    [Trait("Category", "Fast")]
    [Trait("Category", "Fast")]
    [Fact]
    public void CoreAssemblyFileVersion_Is_0_1_0_0()
    {
        var assembly = typeof(AppSettings).Assembly;
        var fileVersionAttr = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>();
        Assert.NotNull(fileVersionAttr);
        Assert.StartsWith("0.1.0", fileVersionAttr!.Version);
    }

    [Trait("Category", "Fast")]
    [Trait("Category", "Fast")]
    [Fact]
    public void AppAssemblyInformationalVersion_Contains_0_1_0()
    {
        var assembly = typeof(WindowsAgentProcessController).Assembly;
        var infoVersionAttr = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        Assert.NotNull(infoVersionAttr);
        Assert.StartsWith("0.1.0", infoVersionAttr!.InformationalVersion);
    }

    [Trait("Category", "Fast")]
    [Trait("Category", "Fast")]
    [Fact]
    public void AgentAssemblyVersion_Is_0_1_0_0()
    {
        var assembly = typeof(QuantifiedSelf.Windows.Agent.Worker).Assembly;
        var version = assembly.GetName().Version;
        Assert.NotNull(version);
        Assert.Equal(0, version!.Major);
        Assert.Equal(1, version.Minor);
        Assert.Equal(0, version.Build);
    }
}
