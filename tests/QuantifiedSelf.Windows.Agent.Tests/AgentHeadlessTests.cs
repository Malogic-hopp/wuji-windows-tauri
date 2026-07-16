using QuantifiedSelf.Windows.Agent.Services;
using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Core.Options;

namespace QuantifiedSelf.Windows.Agent.Tests;

[Trait("Category", "Fast")]
public sealed class AgentHeadlessTests
{
    [Fact]
    public void ProcessedRequestCache_DetectsDuplicateWithoutUiRuntime()
    {
        var cache = new ProcessedRequestCache();

        Assert.False(cache.TryMarkProcessed("request-1"));
        Assert.True(cache.TryMarkProcessed("request-1"));
    }

    [Fact]
    public void ForegroundSamplePrivacyFilter_MasksTitleAndPreservesSampleMetadata()
    {
        var sampleTime = new DateTime(2026, 7, 16, 1, 2, 3, DateTimeKind.Utc);
        var filter = new ForegroundSamplePrivacyFilter();

        var result = filter.Apply(
            new ForegroundSample
            {
                Id = 7,
                SampleTimeUtc = sampleTime,
                ProcessName = "Code",
                WindowTitle = "private title",
                ActivityState = "Active"
            },
            new WindowsAgentOptions { MaskWindowTitles = true });

        Assert.True(result.ShouldWriteSample);
        Assert.NotNull(result.Sample);
        Assert.Equal(7, result.Sample!.Id);
        Assert.Equal(sampleTime, result.Sample.SampleTimeUtc);
        Assert.Equal("Code", result.Sample.ProcessName);
        Assert.Null(result.Sample.WindowTitle);
    }

    [Fact]
    public void AgentAssembly_DoesNotReferenceDesktopUiAssemblies()
    {
        var references = typeof(ProcessedRequestCache).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("PresentationCore", references);
        Assert.DoesNotContain("PresentationFramework", references);
        Assert.DoesNotContain("WindowsBase", references);
        Assert.DoesNotContain("System.Windows.Forms", references);
    }
}
