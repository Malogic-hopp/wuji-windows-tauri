using System.Text.RegularExpressions;
using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Core.Options;

namespace QuantifiedSelf.Windows.Agent.Services;

public sealed class ForegroundSamplePrivacyFilter
{
    public ForegroundSamplePrivacyDecision Apply(ForegroundSample sample, WindowsAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(options);

        if (IsExcludedProcess(sample.ProcessName, options.ExcludedProcesses))
        {
            return Excluded($"Excluded by process privacy rule: {sample.ProcessName}", true);
        }

        if (IsExcludedTitle(sample.WindowTitle, options.ExcludedTitlePatterns))
        {
            return Excluded("Excluded by title privacy rule");
        }

        return new ForegroundSamplePrivacyDecision
        {
            ShouldWriteSample = true,
            Sample = new ForegroundSample
            {
                Id = sample.Id,
                SampleTimeUtc = sample.SampleTimeUtc,
                ProcessName = sample.ProcessName,
                WindowTitle = options.MaskWindowTitles ? null : sample.WindowTitle,
                ExecutablePath = sample.ExecutablePath,
                IdleSeconds = sample.IdleSeconds,
                ActivityState = sample.ActivityState
            }
        };
    }

    private static ForegroundSamplePrivacyDecision Excluded(string reason, bool closeOpenSession = true)
    {
        return new ForegroundSamplePrivacyDecision
        {
            ShouldCloseOpenSession = closeOpenSession,
            Reason = reason
        };
    }

    private static bool IsExcludedProcess(string processName, IEnumerable<string> excludedProcesses)
    {
        return (excludedProcesses ?? Array.Empty<string>()).Any(pattern => MatchesProcessName(processName, pattern));
    }

    private static bool MatchesProcessName(string processName, string pattern)
    {
        if (string.IsNullOrWhiteSpace(processName) || string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var candidate = processName.Trim();
        var normalizedPattern = pattern.Trim();

        if (string.Equals(candidate, normalizedPattern, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var candidateWithoutExtension = Path.GetFileNameWithoutExtension(candidate);
        if (string.Equals(candidateWithoutExtension, normalizedPattern, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var patternWithoutExtension = Path.GetFileNameWithoutExtension(normalizedPattern);
        return string.Equals(candidate, patternWithoutExtension, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidateWithoutExtension, patternWithoutExtension, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcludedTitle(string? title, IEnumerable<string> excludedTitlePatterns)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        foreach (var pattern in excludedTitlePatterns ?? Array.Empty<string>())
        {
            if (MatchesTitlePattern(title, pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesTitlePattern(string title, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var trimmedPattern = pattern.Trim();
        if (trimmedPattern.Contains('*') || trimmedPattern.Contains('?'))
        {
            var regexPattern = "^" + Regex.Escape(trimmedPattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";

            return Regex.IsMatch(title, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        }

        return title.Contains(trimmedPattern, StringComparison.OrdinalIgnoreCase);
    }
}
