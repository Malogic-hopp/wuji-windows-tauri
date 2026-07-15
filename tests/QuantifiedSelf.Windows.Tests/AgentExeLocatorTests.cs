using System.IO;
using QuantifiedSelf.Windows.App.Services;
using QuantifiedSelf.Windows.Client.Agent;
using QuantifiedSelf.Windows.Tests.TestHelpers;

namespace QuantifiedSelf.Windows.Tests;

[Trait("Category", "Fast")]
public sealed class AgentExeLocatorTests
{
    [Fact]
    public void ResolveAgentExecutablePath_ReturnsNull_WhenNoCandidateFound()
    {
        using var workspace = new TempWorkspace("qsw-ael");
        var result = WindowsAgentProcessController.ResolveAgentExecutablePath(workspace.Path);
        Assert.Null(result);
    }

    [Fact]
    public void ResolveAgentExecutablePath_FindsExeInBaseDirectory()
    {
        using var workspace = new TempWorkspace("qsw-ael");
        var fakeExe = Path.Combine(workspace.Path, "QuantifiedSelf.Windows.Agent.exe");
        File.WriteAllText(fakeExe, "");

        var result = WindowsAgentProcessController.ResolveAgentExecutablePath(workspace.Path);

        Assert.NotNull(result);
        Assert.Equal(fakeExe, result, ignoreCase: true);
    }

    [Fact]
    public void ResolveAgentExecutablePath_PrefersIsolatedAgentSubdirectory()
    {
        using var workspace = new TempWorkspace("qsw-ael");
        var legacyExe = Path.Combine(workspace.Path, "QuantifiedSelf.Windows.Agent.exe");
        File.WriteAllText(legacyExe, "");

        var isolatedDir = Path.Combine(workspace.Path, "Agent");
        Directory.CreateDirectory(isolatedDir);
        var isolatedExe = Path.Combine(isolatedDir, "QuantifiedSelf.Windows.Agent.exe");
        File.WriteAllText(isolatedExe, "");

        var result = WindowsAgentProcessController.ResolveAgentExecutablePath(workspace.Path);

        Assert.NotNull(result);
        Assert.Equal(isolatedExe, result, ignoreCase: true);
    }

    [Fact]
    public void ResolveAgentExecutablePath_PrefersBaseDirOverEnvVar()
    {
        using var workspace = new TempWorkspace("qsw-ael");
        var baseDirExe = Path.Combine(workspace.Path, "QuantifiedSelf.Windows.Agent.exe");
        File.WriteAllText(baseDirExe, "");

        var envDir = Path.Combine(Path.GetTempPath(), "qsw-env-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(envDir);
            var envExe = Path.Combine(envDir, "QuantifiedSelf.Windows.Agent.exe");
            File.WriteAllText(envExe, "");

            var oldEnv = Environment.GetEnvironmentVariable("QUANTIFIEDSELF_WINDOWS_AGENT_EXE");
            try
            {
                Environment.SetEnvironmentVariable("QUANTIFIEDSELF_WINDOWS_AGENT_EXE", envExe);
                var result = WindowsAgentProcessController.ResolveAgentExecutablePath(workspace.Path);
                Assert.NotNull(result);
                Assert.Equal(baseDirExe, result, ignoreCase: true);
            }
            finally
            {
                Environment.SetEnvironmentVariable("QUANTIFIEDSELF_WINDOWS_AGENT_EXE", oldEnv);
            }
        }
        finally
        {
            if (Directory.Exists(envDir))
                Directory.Delete(envDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveAgentExecutablePath_FallsBackToEnvVar_WhenBaseDirEmpty()
    {
        using var workspace = new TempWorkspace("qsw-ael");
        var envDir = Path.Combine(Path.GetTempPath(), "qsw-env-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(envDir);
            var envExe = Path.Combine(envDir, "QuantifiedSelf.Windows.Agent.exe");
            File.WriteAllText(envExe, "");

            var oldEnv = Environment.GetEnvironmentVariable("QUANTIFIEDSELF_WINDOWS_AGENT_EXE");
            try
            {
                Environment.SetEnvironmentVariable("QUANTIFIEDSELF_WINDOWS_AGENT_EXE", envExe);
                var result = WindowsAgentProcessController.ResolveAgentExecutablePath(workspace.Path);
                Assert.NotNull(result);
                Assert.Equal(envExe, result, ignoreCase: true);
            }
            finally
            {
                Environment.SetEnvironmentVariable("QUANTIFIEDSELF_WINDOWS_AGENT_EXE", oldEnv);
            }
        }
        finally
        {
            if (Directory.Exists(envDir))
                Directory.Delete(envDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveAgentExecutablePath_IgnoresEnvVar_WhenFileMissing()
    {
        using var workspace = new TempWorkspace("qsw-ael");
        var envExe = Path.Combine(workspace.Path, "NonExistentAgent.exe");

        var oldEnv = Environment.GetEnvironmentVariable("QUANTIFIEDSELF_WINDOWS_AGENT_EXE");
        try
        {
            Environment.SetEnvironmentVariable("QUANTIFIEDSELF_WINDOWS_AGENT_EXE", envExe);
            var result = WindowsAgentProcessController.ResolveAgentExecutablePath(workspace.Path);
            Assert.Null(result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("QUANTIFIEDSELF_WINDOWS_AGENT_EXE", oldEnv);
        }
    }

    [Fact]
    public void ResolveAgentExecutablePath_IgnoresEmptyEnvVar()
    {
        using var workspace = new TempWorkspace("qsw-ael");

        var oldEnv = Environment.GetEnvironmentVariable("QUANTIFIEDSELF_WINDOWS_AGENT_EXE");
        try
        {
            Environment.SetEnvironmentVariable("QUANTIFIEDSELF_WINDOWS_AGENT_EXE", "");
            var result = WindowsAgentProcessController.ResolveAgentExecutablePath(workspace.Path);
            Assert.Null(result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("QUANTIFIEDSELF_WINDOWS_AGENT_EXE", oldEnv);
        }
    }

    [Fact]
    public void ResolveAgentExecutablePath_FindsDevelopmentPath_WhenTargetFrameworkDepthChanges()
    {
        using var workspace = new TempWorkspace("qsw-ael");
        var baseDir = Path.Combine(
            workspace.Path,
            "src",
            "QuantifiedSelf.Windows.App",
            "bin",
            "Debug",
            "net8.0-windows10.0.19041");
        Directory.CreateDirectory(baseDir);

        var agentBin = Path.Combine(
            workspace.Path,
            "src",
            "QuantifiedSelf.Windows.Agent",
            "bin",
            "Debug",
            "net8.0-windows");
        Directory.CreateDirectory(agentBin);
        var agentExe = Path.Combine(agentBin, "QuantifiedSelf.Windows.Agent.exe");
        File.WriteAllText(agentExe, "dev");

        var result = WindowsAgentProcessController.ResolveAgentExecutablePath(baseDir);

        Assert.NotNull(result);
        Assert.Equal(agentExe, result, ignoreCase: true);
    }
}
