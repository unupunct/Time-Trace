using TimeTrace.Core.Abstractions;
using TimeTrace.Core.Models;

namespace TimeTrace.Core.Tracking;

public sealed class RuleClassifier : IAppClassifier
{
    private readonly ICategoryRepository _repo;
    public RuleClassifier(ICategoryRepository repo) => _repo = repo;

    public async Task<int?> ClassifyAsync(string executableName, string? executablePath, CancellationToken ct = default)
        => Classify(await _repo.GetRulesAsync(ct), executableName, executablePath);

    public static int? Classify(IEnumerable<ClassificationRule> rules, string executableName, string? path)
    {
        foreach (var r in rules)
        {
            if (r.MatchType == RuleMatchType.ExecutableName &&
                string.Equals(r.Pattern, executableName, StringComparison.OrdinalIgnoreCase)) return r.CategoryId;
            if (r.MatchType == RuleMatchType.PathContains && path != null &&
                path.Contains(r.Pattern, StringComparison.OrdinalIgnoreCase)) return r.CategoryId;
        }
        return null;
    }
}

public static class DefaultCategories
{
    public const string FallbackName = "Other";

    public static readonly (string Name, string Color, string Description)[] All =
    {
        ("Productivity", "#4F9DFF", "Office, notes and planning tools"),
        ("Development", "#3DDC84", "Code editors, terminals and developer tools"),
        ("Communication", "#B07CFF", "Chat, email and meetings"),
        ("Entertainment", "#FF7A9C", "Media and video"),
        ("Gaming", "#FF9F4A", "Games and game launchers"),
        ("Browsing", "#2FC8D9", "Web browsers"),
        ("System", "#8A94A6", "Windows shell and utilities"),
        ("Other", "#6B7280", "Everything not classified yet"),
    };

    /// <summary>Default executable-name rules: executable → category name.</summary>
    public static readonly (string Exe, string Category)[] Rules =
    {
        ("chrome.exe","Browsing"),("msedge.exe","Browsing"),("firefox.exe","Browsing"),("brave.exe","Browsing"),("opera.exe","Browsing"),("vivaldi.exe","Browsing"),
        ("code.exe","Development"),("devenv.exe","Development"),("rider64.exe","Development"),("idea64.exe","Development"),("pycharm64.exe","Development"),
        ("windowsterminal.exe","Development"),("powershell.exe","Development"),("pwsh.exe","Development"),("cmd.exe","Development"),("wt.exe","Development"),("claude.exe","Development"),
        ("winword.exe","Productivity"),("excel.exe","Productivity"),("powerpnt.exe","Productivity"),("onenote.exe","Productivity"),("notepad.exe","Productivity"),("notepad++.exe","Productivity"),("obsidian.exe","Productivity"),("notion.exe","Productivity"),("acrord32.exe","Productivity"),
        ("slack.exe","Communication"),("teams.exe","Communication"),("ms-teams.exe","Communication"),("discord.exe","Communication"),("outlook.exe","Communication"),("zoom.exe","Communication"),("telegram.exe","Communication"),("whatsapp.exe","Communication"),("thunderbird.exe","Communication"),
        ("spotify.exe","Entertainment"),("vlc.exe","Entertainment"),("wmplayer.exe","Entertainment"),
        ("steam.exe","Gaming"),("epicgameslauncher.exe","Gaming"),("battle.net.exe","Gaming"),
        ("explorer.exe","System"),("taskmgr.exe","System"),("systemsettings.exe","System"),("mmc.exe","System"),("regedit.exe","System"),("searchhost.exe","System"),("applicationframehost.exe","System"),
    };
}
