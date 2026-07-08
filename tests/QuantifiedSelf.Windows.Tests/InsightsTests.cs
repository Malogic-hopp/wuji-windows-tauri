using QuantifiedSelf.Windows.App.Services;
using QuantifiedSelf.Windows.App.ViewModels;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.Tests;

public sealed class InsightsTests
{
    // ── Classification tests ─────────────────────────────────────────────

    [Fact]
    public void ClassifyContext_CodeAndTerminal_AreBothDevelopment()
    {
        Assert.Equal("开发", FocusInterruptionInsightService.ClassifyContext("Code", "Program.cs"));
        Assert.Equal("开发", FocusInterruptionInsightService.ClassifyContext("Code.exe", ""));
        Assert.Equal("开发", FocusInterruptionInsightService.ClassifyContext("WindowsTerminal.exe", ""));
        Assert.Equal("开发", FocusInterruptionInsightService.ClassifyContext("Codex", ""));
        Assert.Equal("开发", FocusInterruptionInsightService.ClassifyContext("powershell.exe", ""));
        Assert.Equal("开发", FocusInterruptionInsightService.ClassifyContext("cmd.exe", ""));
    }

    [Fact]
    public void ClassifyContext_CodeToWeChat_IsDevelopmentToCommunication()
    {
        Assert.Equal("开发", FocusInterruptionInsightService.ClassifyContext("Code", ""));
        Assert.Equal("沟通", FocusInterruptionInsightService.ClassifyContext("WeChat.exe", ""));
        Assert.Equal("沟通", FocusInterruptionInsightService.ClassifyContext("weixin", ""));
    }

    [Fact]
    public void ClassifyContext_EdgeTechnicalTitle_IsResearch()
    {
        // Edge with general technical search → Research (no entertainment/comm/dev-specific keywords)
        Assert.Equal("研究",
            FocusInterruptionInsightService.ClassifyBrowserTitle("Wikipedia – C# programming language"));
        Assert.Equal("研究",
            FocusInterruptionInsightService.ClassifyBrowserTitle("ASP.NET Core Routing - Google Search"));
        // Note: "Microsoft Learn" matches the "microsoft learn" dev token → Development
    }

    [Fact]
    public void ClassifyContext_EdgeGitHubTitle_IsDevelopment()
    {
        Assert.Equal("开发",
            FocusInterruptionInsightService.ClassifyBrowserTitle(
                "GitHub - dotnet/runtime: .NET is a cross-platform runtime"));
        Assert.Equal("开发",
            FocusInterruptionInsightService.ClassifyBrowserTitle("localhost:5000/swagger"));
    }

    [Fact]
    public void ClassifyContext_EdgeEntertainmentTitle_IsEntertainment()
    {
        Assert.Equal("娱乐",
            FocusInterruptionInsightService.ClassifyBrowserTitle("bilibili - 哔哩哔哩"));
        Assert.Equal("娱乐",
            FocusInterruptionInsightService.ClassifyBrowserTitle("小红书 - 标记我的生活"));
        Assert.Equal("娱乐",
            FocusInterruptionInsightService.ClassifyBrowserTitle("微博 weibo.com"));
        Assert.Equal("娱乐",
            FocusInterruptionInsightService.ClassifyBrowserTitle("zhiboba 直播吧"));
    }

    [Fact]
    public void ClassifyContext_BrowserCommunicationTitle_IsCommunication()
    {
        Assert.Equal("沟通",
            FocusInterruptionInsightService.ClassifyBrowserTitle("Gmail - Inbox"));
        Assert.Equal("沟通",
            FocusInterruptionInsightService.ClassifyBrowserTitle("Outlook Mail"));
        Assert.Equal("沟通",
            FocusInterruptionInsightService.ClassifyBrowserTitle("微信网页版"));
    }

    // ── Work block detection tests ──────────────────────────────────────

    [Fact]
    public void DetectWorkBlocks_EmptyList_ReturnsEmpty()
    {
        var blocks = FocusInterruptionInsightService.DetectWorkBlocks([]);
        Assert.Empty(blocks);
    }

    [Fact]
    public void DetectWorkBlocks_LongCodingBlock_IsIdentified()
    {
        // Build a 90-minute coding block with 60s sampling
        var now = new DateTime(2026, 7, 7, 14, 0, 0, DateTimeKind.Utc);
        var samples = new List<ForegroundSample>();
        for (var i = 0; i < 90; i++) // 90 samples at ~60s each = 90min
        {
            samples.Add(new ForegroundSample
            {
                Id = i + 1,
                SampleTimeUtc = now.AddMinutes(i),
                ProcessName = "Code",
                WindowTitle = "Program.cs",
                ActivityState = "Active",
                Context = "开发",
            });
        }

        var blocks = FocusInterruptionInsightService.DetectWorkBlocks(samples);
        Assert.NotEmpty(blocks);
        var block = blocks[0];
        Assert.True(block.Duration.TotalMinutes >= 89);
        Assert.Equal("开发", block.PrimaryContext);
        Assert.Equal("Code", block.PrimaryApp);
        Assert.Equal(0, block.ContextSwitchCount);
        Assert.True(block.IsRecognizedFocusBlock);
    }

    [Fact]
    public void DetectWorkBlocks_BlockWithInterruptions_FindsInterrupters()
    {
        // Build a 60-minute work block interleaved with WeChat
        var now = new DateTime(2026, 7, 7, 15, 0, 0, DateTimeKind.Utc);
        var samples = new List<ForegroundSample>();

        for (var i = 0; i < 40; i++)
        {
            // Code for 50s, then WeChat for 10s
            samples.Add(new ForegroundSample
            {
                Id = i * 2 + 1,
                SampleTimeUtc = now.AddSeconds(i * 60),
                ProcessName = "Code",
                ActivityState = "Active",
                Context = "开发",
            });
            samples.Add(new ForegroundSample
            {
                Id = i * 2 + 2,
                SampleTimeUtc = now.AddSeconds(i * 60 + 10),
                ProcessName = "WeChat",
                ActivityState = "Active",
                Context = "沟通",
            });
        }

        var blocks = FocusInterruptionInsightService.DetectWorkBlocks(samples);
        Assert.NotEmpty(blocks);
        var block = blocks[0];

        // Should not be recognized as focus due to high switch count
        Assert.False(block.IsRecognizedFocusBlock);
        Assert.True(block.ContextSwitchCount > 10);

        // Should have WeChat as an interrupter
        Assert.NotEmpty(block.TopInterruptions);
        Assert.Contains(block.TopInterruptions, src =>
            src.AppName.Equals("WeChat", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DetectWorkBlocks_LargeGap_BreaksSegment()
    {
        var now = new DateTime(2026, 7, 7, 14, 0, 0, DateTimeKind.Utc);
        var samples = new List<ForegroundSample>
        {
            new() { Id = 1, SampleTimeUtc = now, ProcessName = "Code", ActivityState = "Active", Context = "开发" },
            new() { Id = 2, SampleTimeUtc = now.AddMinutes(1), ProcessName = "Code", ActivityState = "Active", Context = "开发" },
            // 10-minute gap → breaks the block
            new() { Id = 3, SampleTimeUtc = now.AddMinutes(11), ProcessName = "Code", ActivityState = "Active", Context = "开发" },
            new() { Id = 4, SampleTimeUtc = now.AddMinutes(12), ProcessName = "Code", ActivityState = "Active", Context = "开发" },
        };

        var blocks = FocusInterruptionInsightService.DetectWorkBlocks(samples);
        Assert.True(blocks.Count >= 2, "10-minute gap should create two separate blocks");
    }

    // ── Empty data tests ────────────────────────────────────────────────

    [Fact]
    public void CountSwitches_NoSwitches_ReturnsEmpty()
    {
        var samples = new List<ForegroundSample>
        {
            new() { Id = 1, SampleTimeUtc = DateTime.UtcNow, ProcessName = "Code", ActivityState = "Active", Context = "开发" },
        };

        var (raw, meaningful) = FocusInterruptionInsightService.CountSwitches(samples);
        Assert.Empty(raw);
        Assert.Empty(meaningful);
    }

    [Fact]
    public void CountSwitches_SameContextSwitch_RawOnly()
    {
        var now = DateTime.UtcNow;
        var samples = new List<ForegroundSample>
        {
            new() { Id = 1, SampleTimeUtc = now, ProcessName = "Code", ActivityState = "Active", Context = "开发" },
            new() { Id = 2, SampleTimeUtc = now.AddSeconds(60), ProcessName = "Codex", ActivityState = "Active", Context = "开发" },
        };

        var (raw, meaningful) = FocusInterruptionInsightService.CountSwitches(samples);
        Assert.Single(raw);           // Code → Codex is a raw hop (app changed)
        Assert.Empty(meaningful);      // But same context → not meaningful
    }

    // ── Formatting tests ──────────────────────────────────────────────

    [Fact]
    public void FormatMinutes_EdgeCases()
    {
        Assert.Equal("0m", FocusInterruptionInsightService.FormatMinutes(0));
        Assert.Equal("5m", FocusInterruptionInsightService.FormatMinutes(300));
        Assert.Equal("1h 0m", FocusInterruptionInsightService.FormatMinutes(3600));
        Assert.Equal("2h 30m", FocusInterruptionInsightService.FormatMinutes(9000));
    }

    // ── Generate texts tests ──────────────────────────────────────────

    [Fact]
    public void GenerateTexts_EmptyWorkBlocks_ReturnsSafeText()
    {
        var (summary, action) = FocusInterruptionInsightService.GenerateTexts(
            [], [], [], []);
        Assert.False(string.IsNullOrWhiteSpace(summary));
        // action empty when no data
        Assert.True(string.IsNullOrEmpty(action));
    }

    [Fact]
    public void GenerateTexts_FragmentedBlocks_ExplainsWhy()
    {
        var now = DateTime.Now;
        var block = new WorkBlockInsight
        {
            StartLocal = now,
            EndLocal = now.AddMinutes(45),
            PrimaryContext = "开发",
            PrimaryApp = "Code",
            ContextSwitchCount = 50,
            IsRecognizedFocusBlock = false,
            ExplanationText = "Too many context switches",
        };
        var interrupter = new InterruptionSourceInsight
        {
            AppName = "WeChat",
            Context = "沟通",
            Count = 25,
            DisplayText = "WeChat · 25×",
        };

        var (summary, action) = FocusInterruptionInsightService.GenerateTexts(
            [block], [interrupter], [], []);

        Assert.Contains("WeChat", summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(action));
    }

    [Fact]
    public async Task InsightsViewModel_LoadAsync_UsesSelectedDate()
    {
        DateOnly? requestedDate = null;
        var viewModel = new InsightsViewModel((date, _) =>
        {
            requestedDate = date;
            return Task.FromResult(new FocusInterruptionInsight
            {
                Date = date,
                ActiveSampleCount = 7,
                RawToolHopCount = 3,
                MeaningfulContextSwitchCount = 2,
                SummaryText = "测试日期洞察",
            });
        });

        viewModel.SelectedDate = new DateTime(2026, 7, 6);

        await viewModel.LoadAsync();

        Assert.Equal(new DateOnly(2026, 7, 6), requestedDate);
        Assert.Equal("2026-07-06", viewModel.SelectedDateText);
        Assert.Equal("7", viewModel.ActiveSampleText);
        Assert.True(viewModel.HasInsightData);
        Assert.Equal(1, viewModel.InsightDataCount);
        Assert.Equal("测试日期洞察", viewModel.SummaryText);
    }

    [Fact]
    public async Task InsightsViewModel_ActiveDataWithoutWorkBlocks_StillShowsInsight()
    {
        var viewModel = new InsightsViewModel((date, _) =>
            Task.FromResult(new FocusInterruptionInsight
            {
                Date = date,
                ActiveSampleCount = 12,
                MeaningfulContextSwitchCount = 5,
                SummaryText = "今天有零散活动，但未形成较长连续工作块。",
                ActionText = "试试安排一个 25 分钟不受打扰的 Code-Only 块。",
                WorkBlocks = [],
            }));

        await viewModel.LoadAsync();

        Assert.Empty(viewModel.WorkBlocks);
        Assert.True(viewModel.HasInsightData);
        Assert.Equal(1, viewModel.InsightDataCount);
        Assert.Contains("零散活动", viewModel.SummaryText, StringComparison.Ordinal);
        Assert.Contains("25 分钟", viewModel.ActionText, StringComparison.Ordinal);
    }
}
