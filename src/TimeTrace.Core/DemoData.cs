using TimeTrace.Core.Abstractions;
using TimeTrace.Core.Models;
using TimeTrace.Core.Tracking;

namespace TimeTrace.Core;

/// <summary>Generates synthetic activity (never real data) for screenshots, demos and tests. Enabled only with --demo-data and --data-dir.</summary>
public static class DemoData
{
    private static readonly (string Exe, string Name, string[] Titles)[] Apps =
    {
        ("code.exe", "Visual Studio Code", new[] { "Program.cs - TimeTrace", "README.md - TimeTrace", "Settings.cs - TimeTrace" }),
        ("chrome.exe", "Google Chrome", new[] { "Stack Overflow - Where Developers Learn", "GitHub - Pull requests", "Docs - WPF overview" }),
        ("slack.exe", "Slack", new[] { "Slack | #engineering", "Slack | Direct messages" }),
        ("windowsterminal.exe", "Windows Terminal", new[] { "PowerShell", "dotnet test" }),
        ("winword.exe", "Microsoft Word", new[] { "Quarterly plan.docx - Word" }),
        ("outlook.exe", "Outlook", new[] { "Inbox - Outlook" }),
        ("spotify.exe", "Spotify", new[] { "Spotify Premium" }),
        ("explorer.exe", "File Explorer", new[] { "Documents", "Downloads" }),
        ("steam.exe", "Steam", new[] { "Steam" }),
    };

    public static async Task GenerateAsync(IActivityRepository activity, ICategoryRepository categories, DateTime nowUtc, int days = 14, int seed = 42)
    {
        var rnd = new Random(seed);
        var rules = await categories.GetRulesAsync();
        var records = new List<(AppRecord App, string[] Titles)>();
        foreach (var (exe, name, titles) in Apps)
        {
            var cat = RuleClassifier.Classify(rules, exe, null);
            var rec = await activity.AddApplicationAsync(new AppRecord { Name = name, ExecutableName = exe, CategoryId = cat, FirstSeen = nowUtc.AddDays(-days), LastSeen = nowUtc });
            records.Add((rec, titles));
        }
        var localToday = nowUtc.ToLocalTime().Date;
        for (int d = days - 1; d >= 0; d--)
        {
            var day = localToday.AddDays(-d);
            bool weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var t = day.AddHours(weekend ? 10 + rnd.NextDouble() * 2 : 8 + rnd.NextDouble() * 1.5);
            var endOfDay = day.AddHours(weekend ? 15 : 18.5);
            if (d == 0) endOfDay = nowUtc.ToLocalTime().AddMinutes(-2);
            while (t < endOfDay)
            {
                if (Math.Abs((t - day.AddHours(12.5)).TotalMinutes) < 20 && rnd.NextDouble() < 0.9)
                {
                    var idleEnd = t.AddMinutes(35 + rnd.Next(25));
                    await Insert(activity, null, t, idleEnd, null, SessionType.Idle);
                    t = idleEnd; continue;
                }
                var pick = weekend ? records[rnd.Next(records.Count)] : Weighted(records, rnd);
                var minutes = 4 + rnd.NextDouble() * (pick.App.ExecutableName is "code.exe" ? 70 : 28);
                var end = t.AddMinutes(minutes);
                if (end > endOfDay) end = endOfDay;
                if (end <= t) break;
                await Insert(activity, pick.App.Id, t, end, pick.Titles[rnd.Next(pick.Titles.Length)], SessionType.Active);
                t = end;
            }
        }
    }

    private static (AppRecord App, string[] Titles) Weighted(List<(AppRecord App, string[] Titles)> r, Random rnd)
    {
        double x = rnd.NextDouble();
        int i = x < 0.34 ? 0 : x < 0.58 ? 1 : x < 0.68 ? 2 : x < 0.78 ? 3 : x < 0.85 ? 4 : x < 0.91 ? 5 : x < 0.94 ? 6 : x < 0.98 ? 7 : 8;
        return r[i];
    }

    private static Task Insert(IActivityRepository repo, int? appId, DateTime localStart, DateTime localEnd, string? title, SessionType type) =>
        repo.InsertSessionAsync(new ActivitySession
        {
            ApplicationId = appId, ProcessId = 1000, WindowTitle = title, StartTimeUtc = localStart.ToUniversalTime(), EndTimeUtc = localEnd.ToUniversalTime(),
            DurationSeconds = (localEnd - localStart).TotalSeconds, SessionType = type,
        });
}
