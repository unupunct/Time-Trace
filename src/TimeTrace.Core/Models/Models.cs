namespace TimeTrace.Core.Models;

public enum SessionType { Active = 0, Idle = 1 }

public sealed class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#6B7280";
    public string? Description { get; set; }
}

public sealed class AppRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string ExecutableName { get; set; } = "";
    public string? ExecutablePath { get; set; }
    public string? IconCachePath { get; set; }
    public int? CategoryId { get; set; }
    public bool IsExcluded { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
}

public sealed class ActivitySession
{
    public long Id { get; set; }
    public int? ApplicationId { get; set; }
    public int ProcessId { get; set; }
    public string? WindowTitle { get; set; }
    public DateTime StartTimeUtc { get; set; }
    public DateTime EndTimeUtc { get; set; }
    public double DurationSeconds { get; set; }
    public SessionType SessionType { get; set; }
    public bool IsOpen { get; set; }
}

public sealed class TrackingEvent
{
    public long Id { get; set; }
    public string EventType { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
    public string? Details { get; set; }
}

public enum RuleMatchType { ExecutableName = 0, PathContains = 1 }

public sealed class ClassificationRule
{
    public int Id { get; set; }
    public RuleMatchType MatchType { get; set; }
    public string Pattern { get; set; } = "";
    public int CategoryId { get; set; }
}

public static class TrackingEventTypes
{
    public const string Started = "Started";
    public const string Stopped = "Stopped";
    public const string Paused = "Paused";
    public const string Resumed = "Resumed";
    public const string Suspend = "Suspend";
    public const string Resume = "SystemResume";
    public const string Locked = "Locked";
    public const string Unlocked = "Unlocked";
    public const string IdleStart = "IdleStart";
    public const string IdleEnd = "IdleEnd";
    public const string Recovered = "Recovered";
    public const string Error = "Error";
    public const string Shutdown = "Shutdown";
}
