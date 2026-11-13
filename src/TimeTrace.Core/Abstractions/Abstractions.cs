using TimeTrace.Core.Models;

namespace TimeTrace.Core.Abstractions;

/// <summary>Information about the window currently in the foreground.</summary>
public sealed record ForegroundWindowInfo(
    int ProcessId,
    string ExecutableName,
    string? ExecutablePath,
    string AppName,
    string? WindowTitle,
    bool IsMinimized = false);

public interface IForegroundWindowProvider
{
    /// <summary>Returns the foreground window, or null when there is none / it cannot be inspected.</summary>
    ForegroundWindowInfo? GetForegroundWindow();
}

public interface IIdleTimeProvider
{
    /// <summary>Time elapsed since the last user input (no input content is ever read).</summary>
    TimeSpan GetIdleTime();
}

public interface IClock
{
    DateTime UtcNow { get; }
    /// <summary>Monotonic milliseconds, unaffected by wall-clock changes.</summary>
    long MonotonicMilliseconds { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
    public long MonotonicMilliseconds => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
}

public interface IActivityRepository
{
    Task<AppRecord?> GetApplicationByExecutableAsync(string executableName, CancellationToken ct = default);
    Task<AppRecord?> GetApplicationAsync(int id, CancellationToken ct = default);
    Task<AppRecord> AddApplicationAsync(AppRecord app, CancellationToken ct = default);
    Task UpdateApplicationAsync(AppRecord app, CancellationToken ct = default);
    Task<IReadOnlyList<AppRecord>> GetApplicationsAsync(CancellationToken ct = default);
    Task DeleteApplicationAsync(int id, CancellationToken ct = default);

    Task<long> InsertSessionAsync(ActivitySession session, CancellationToken ct = default);
    Task UpdateSessionAsync(ActivitySession session, CancellationToken ct = default);
    /// <summary>Sessions overlapping [fromUtc, toUtc).</summary>
    Task<IReadOnlyList<ActivitySession>> GetSessionsAsync(DateTime fromUtc, DateTime toUtc, int? applicationId = null, CancellationToken ct = default);
    Task DeleteSessionsAsync(IEnumerable<long> ids, CancellationToken ct = default);
    Task DeleteAllActivityAsync(CancellationToken ct = default);
    Task<int> DeleteSessionsOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default);
    /// <summary>Closes sessions left open by an unexpected shutdown. Returns how many were recovered.</summary>
    Task<int> RecoverOpenSessionsAsync(CancellationToken ct = default);

    Task AddEventAsync(string eventType, DateTime timestampUtc, string? details, CancellationToken ct = default);
    Task<IReadOnlyList<TrackingEvent>> GetEventsAsync(int limit = 200, CancellationToken ct = default);
}

public interface ICategoryRepository
{
    Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default);
    Task<Category> AddCategoryAsync(Category category, CancellationToken ct = default);
    Task UpdateCategoryAsync(Category category, CancellationToken ct = default);
    /// <summary>Deletes a category; its applications and rules move to <paramref name="fallbackCategoryId"/>.</summary>
    Task DeleteCategoryAsync(int id, int fallbackCategoryId, CancellationToken ct = default);
    Task<IReadOnlyList<ClassificationRule>> GetRulesAsync(CancellationToken ct = default);
    Task<ClassificationRule> AddRuleAsync(ClassificationRule rule, CancellationToken ct = default);
    Task DeleteRuleAsync(int id, CancellationToken ct = default);
}

public interface ISettingsRepository
{
    Task<IReadOnlyDictionary<string, string>> GetAllSettingsAsync(CancellationToken ct = default);
    Task SetSettingsAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct = default);
}

public interface IAppClassifier
{
    /// <summary>Returns the category id for an executable, or null if no rule matches.</summary>
    Task<int?> ClassifyAsync(string executableName, string? executablePath, CancellationToken ct = default);
}
