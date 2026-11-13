using System.Text.RegularExpressions;
using TimeTrace.Core.Settings;

namespace TimeTrace.Core.Tracking;

public sealed record TrackingOptions(
    TimeSpan PollInterval,
    TimeSpan IdleThreshold,
    bool TrackWindowTitles,
    IReadOnlyList<string> ExcludedExecutables,
    IReadOnlyList<string> ExcludedPaths,
    IReadOnlyList<string> ExcludedTitleRules,
    bool PauseOnLock,
    TimeSpan CheckpointInterval)
{
    public static TrackingOptions From(AppSettings s) => new(
        TimeSpan.FromSeconds(s.PollIntervalSeconds), TimeSpan.FromMinutes(s.IdleThresholdMinutes), s.TrackWindowTitles,
        s.ExcludedExecutables.ToList(), s.ExcludedPaths.ToList(), s.ExcludedTitleRules.ToList(), s.PauseOnLock, TimeSpan.FromSeconds(30));

    public static TrackingOptions Default => From(new AppSettings());
}

/// <summary>Decides whether a window must be ignored. All inputs are treated as untrusted text, never executed.</summary>
public static class ExclusionMatcher
{
    public static bool IsExcluded(TrackingOptions o, string executableName, string? path, string? title)
    {
        if (o.ExcludedExecutables.Any(x => string.Equals(x, executableName, StringComparison.OrdinalIgnoreCase))) return true;
        if (path != null && o.ExcludedPaths.Any(p => path.Contains(p, StringComparison.OrdinalIgnoreCase))) return true;
        if (!string.IsNullOrEmpty(title) && o.ExcludedTitleRules.Any(r => TitleMatches(r, title))) return true;
        return false;
    }

    /// <summary>Rule is a case-insensitive substring, or a regular expression when prefixed with "re:".</summary>
    public static bool TitleMatches(string rule, string title)
    {
        if (rule.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
        {
            try { return Regex.IsMatch(title, rule[3..], RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); }
            catch (Exception) { return false; }
        }
        return title.Contains(rule, StringComparison.OrdinalIgnoreCase);
    }
}
