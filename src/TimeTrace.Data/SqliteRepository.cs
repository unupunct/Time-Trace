using Microsoft.Data.Sqlite;
using TimeTrace.Core.Abstractions;
using TimeTrace.Core.Models;
using static TimeTrace.Data.SqliteDatabase;

namespace TimeTrace.Data;

public sealed class SqliteRepository : IActivityRepository, ICategoryRepository, ISettingsRepository
{
    private readonly SqliteDatabase _db;
    public SqliteRepository(SqliteDatabase db) => _db = db;

    // ------------------------------------------------------------ helpers
    private static SqliteCommand Cmd(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    private Task<T> Run<T>(Func<SqliteConnection, T> work, CancellationToken ct = default) =>
        Task.Run(() => { ct.ThrowIfCancellationRequested(); using var c = _db.Open(); return work(c); }, ct);

    private const string AppCols = "Id,Name,ExecutableName,ExecutablePath,IconCachePath,CategoryId,IsExcluded,FirstSeen,LastSeen";

    private static AppRecord ReadApp(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(0), Name = r.GetString(1), ExecutableName = r.GetString(2),
        ExecutablePath = r.IsDBNull(3) ? null : r.GetString(3), IconCachePath = r.IsDBNull(4) ? null : r.GetString(4),
        CategoryId = r.IsDBNull(5) ? null : r.GetInt32(5), IsExcluded = r.GetInt32(6) != 0,
        FirstSeen = ParseTs(r.GetString(7)), LastSeen = ParseTs(r.GetString(8)),
    };

    private const string SessCols = "Id,ApplicationId,ProcessId,WindowTitle,StartTimeUtc,EndTimeUtc,DurationSeconds,SessionType,IsOpen";

    private static ActivitySession ReadSession(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), ApplicationId = r.IsDBNull(1) ? null : r.GetInt32(1), ProcessId = r.GetInt32(2),
        WindowTitle = r.IsDBNull(3) ? null : r.GetString(3), StartTimeUtc = ParseTs(r.GetString(4)), EndTimeUtc = ParseTs(r.GetString(5)),
        DurationSeconds = r.GetDouble(6), SessionType = (SessionType)r.GetInt32(7), IsOpen = r.GetInt32(8) != 0,
    };

    // ------------------------------------------------------------ applications
    public Task<AppRecord?> GetApplicationByExecutableAsync(string executableName, CancellationToken ct = default) =>
        Run(c =>
        {
            using var cmd = Cmd(c, $"SELECT {AppCols} FROM Applications WHERE ExecutableName=$e", ("$e", executableName));
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadApp(r) : null;
        }, ct);

    public Task<AppRecord?> GetApplicationAsync(int id, CancellationToken ct = default) =>
        Run(c =>
        {
            using var cmd = Cmd(c, $"SELECT {AppCols} FROM Applications WHERE Id=$i", ("$i", id));
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadApp(r) : null;
        }, ct);

    /// <summary>Inserts an application, or returns the existing record when the executable is already known (no duplicates).</summary>
    public Task<AppRecord> AddApplicationAsync(AppRecord app, CancellationToken ct = default) =>
        Run(c =>
        {
            using (var cmd = Cmd(c, """
                INSERT INTO Applications(Name,ExecutableName,ExecutablePath,IconCachePath,CategoryId,IsExcluded,FirstSeen,LastSeen)
                VALUES($n,$e,$p,$i,$c,$x,$f,$l) ON CONFLICT(ExecutableName) DO NOTHING
                """,
                ("$n", app.Name), ("$e", app.ExecutableName), ("$p", app.ExecutablePath), ("$i", app.IconCachePath),
                ("$c", app.CategoryId), ("$x", app.IsExcluded ? 1 : 0), ("$f", Ts(app.FirstSeen)), ("$l", Ts(app.LastSeen))))
                cmd.ExecuteNonQuery();
            using var q = Cmd(c, $"SELECT {AppCols} FROM Applications WHERE ExecutableName=$e", ("$e", app.ExecutableName));
            using var r = q.ExecuteReader();
            r.Read();
            return ReadApp(r);
        }, ct);

    public Task UpdateApplicationAsync(AppRecord a, CancellationToken ct = default) =>
        Run(c =>
        {
            using var cmd = Cmd(c, "UPDATE Applications SET Name=$n,ExecutablePath=$p,IconCachePath=$i,CategoryId=$c,IsExcluded=$x,LastSeen=$l WHERE Id=$id",
                ("$n", a.Name), ("$p", a.ExecutablePath), ("$i", a.IconCachePath), ("$c", a.CategoryId),
                ("$x", a.IsExcluded ? 1 : 0), ("$l", Ts(a.LastSeen)), ("$id", a.Id));
            return cmd.ExecuteNonQuery();
        }, ct);

    public Task<IReadOnlyList<AppRecord>> GetApplicationsAsync(CancellationToken ct = default) =>
        Run<IReadOnlyList<AppRecord>>(c =>
        {
            using var cmd = Cmd(c, $"SELECT {AppCols} FROM Applications ORDER BY Name COLLATE NOCASE");
            using var r = cmd.ExecuteReader();
            var list = new List<AppRecord>();
            while (r.Read()) list.Add(ReadApp(r));
            return list;
        }, ct);

    public Task DeleteApplicationAsync(int id, CancellationToken ct = default) =>
        Run(c => { using var cmd = Cmd(c, "DELETE FROM Applications WHERE Id=$i", ("$i", id)); return cmd.ExecuteNonQuery(); }, ct);

    // ------------------------------------------------------------ sessions
    public Task<long> InsertSessionAsync(ActivitySession s, CancellationToken ct = default) =>
        Run(c =>
        {
            using var cmd = Cmd(c, """
                INSERT INTO ActivitySessions(ApplicationId,ProcessId,WindowTitle,StartTimeUtc,EndTimeUtc,DurationSeconds,SessionType,IsOpen)
                VALUES($a,$p,$w,$s,$e,$d,$t,$o); SELECT last_insert_rowid();
                """,
                ("$a", s.ApplicationId), ("$p", s.ProcessId), ("$w", s.WindowTitle), ("$s", Ts(s.StartTimeUtc)), ("$e", Ts(s.EndTimeUtc)),
                ("$d", s.DurationSeconds), ("$t", (int)s.SessionType), ("$o", s.IsOpen ? 1 : 0));
            return Convert.ToInt64(cmd.ExecuteScalar());
        }, ct);

    public Task UpdateSessionAsync(ActivitySession s, CancellationToken ct = default) =>
        Run(c =>
        {
            using var cmd = Cmd(c, "UPDATE ActivitySessions SET WindowTitle=$w,EndTimeUtc=$e,DurationSeconds=$d,IsOpen=$o WHERE Id=$id",
                ("$w", s.WindowTitle), ("$e", Ts(s.EndTimeUtc)), ("$d", s.DurationSeconds), ("$o", s.IsOpen ? 1 : 0), ("$id", s.Id));
            return cmd.ExecuteNonQuery();
        }, ct);

    public Task<IReadOnlyList<ActivitySession>> GetSessionsAsync(DateTime fromUtc, DateTime toUtc, int? applicationId = null, CancellationToken ct = default) =>
        Run<IReadOnlyList<ActivitySession>>(c =>
        {
            var sql = $"SELECT {SessCols} FROM ActivitySessions WHERE EndTimeUtc > $f AND StartTimeUtc < $t" +
                      (applicationId.HasValue ? " AND ApplicationId=$a" : "") + " ORDER BY StartTimeUtc";
            using var cmd = Cmd(c, sql, ("$f", Ts(fromUtc)), ("$t", Ts(toUtc)));
            if (applicationId.HasValue) cmd.Parameters.AddWithValue("$a", applicationId.Value);
            using var r = cmd.ExecuteReader();
            var list = new List<ActivitySession>();
            while (r.Read()) list.Add(ReadSession(r));
            return list;
        }, ct);

    public Task DeleteSessionsAsync(IEnumerable<long> ids, CancellationToken ct = default) =>
        Run(c =>
        {
            using var tx = c.BeginTransaction();
            foreach (var id in ids)
            {
                using var cmd = Cmd(c, "DELETE FROM ActivitySessions WHERE Id=$i", ("$i", id));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return 0;
        }, ct);

    /// <summary>Deletes all sessions, tracking events and detected applications. Applications the user explicitly excluded are kept (they are configuration), as are categories and rules.</summary>
    public Task DeleteAllActivityAsync(CancellationToken ct = default) =>
        Run(c =>
        {
            Exec(c, "DELETE FROM ActivitySessions; DELETE FROM TrackingEvents; DELETE FROM Applications WHERE IsExcluded=0;");
            Exec(c, "DELETE FROM sqlite_sequence WHERE name IN ('ActivitySessions','TrackingEvents');");
            Exec(c, "VACUUM;");
            return 0;
        }, ct);

    public Task<int> DeleteSessionsOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default) =>
        Run(c =>
        {
            int n;
            using (var cmd = Cmd(c, "DELETE FROM ActivitySessions WHERE EndTimeUtc < $c AND IsOpen=0", ("$c", Ts(cutoffUtc)))) n = cmd.ExecuteNonQuery();
            using (var cmd = Cmd(c, "DELETE FROM TrackingEvents WHERE TimestampUtc < $c", ("$c", Ts(cutoffUtc)))) cmd.ExecuteNonQuery();
            return n;
        }, ct);

    public Task<int> RecoverOpenSessionsAsync(CancellationToken ct = default) =>
        Run(c =>
        {
            using var cmd = Cmd(c, "UPDATE ActivitySessions SET IsOpen=0 WHERE IsOpen=1");
            return cmd.ExecuteNonQuery();
        }, ct);

    // ------------------------------------------------------------ events
    public Task AddEventAsync(string eventType, DateTime timestampUtc, string? details, CancellationToken ct = default) =>
        Run(c =>
        {
            using var cmd = Cmd(c, "INSERT INTO TrackingEvents(EventType,TimestampUtc,Details) VALUES($t,$s,$d)",
                ("$t", eventType), ("$s", Ts(timestampUtc)), ("$d", details));
            return cmd.ExecuteNonQuery();
        }, ct);

    public Task<IReadOnlyList<TrackingEvent>> GetEventsAsync(int limit = 200, CancellationToken ct = default) =>
        Run<IReadOnlyList<TrackingEvent>>(c =>
        {
            using var cmd = Cmd(c, "SELECT Id,EventType,TimestampUtc,Details FROM TrackingEvents ORDER BY Id DESC LIMIT $l", ("$l", limit));
            using var r = cmd.ExecuteReader();
            var list = new List<TrackingEvent>();
            while (r.Read()) list.Add(new TrackingEvent { Id = r.GetInt64(0), EventType = r.GetString(1), TimestampUtc = ParseTs(r.GetString(2)), Details = r.IsDBNull(3) ? null : r.GetString(3) });
            return list;
        }, ct);

    // ------------------------------------------------------------ categories & rules
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default) =>
        Run<IReadOnlyList<Category>>(c =>
        {
            using var cmd = Cmd(c, "SELECT Id,Name,Color,Description FROM Categories ORDER BY Id");
            using var r = cmd.ExecuteReader();
            var list = new List<Category>();
            while (r.Read()) list.Add(new Category { Id = r.GetInt32(0), Name = r.GetString(1), Color = r.GetString(2), Description = r.IsDBNull(3) ? null : r.GetString(3) });
            return list;
        }, ct);

    public Task<Category> AddCategoryAsync(Category cat, CancellationToken ct = default) =>
        Run(c =>
        {
            using var cmd = Cmd(c, "INSERT INTO Categories(Name,Color,Description) VALUES($n,$c,$d); SELECT last_insert_rowid();",
                ("$n", cat.Name.Trim()), ("$c", cat.Color), ("$d", cat.Description));
            cat.Id = Convert.ToInt32(cmd.ExecuteScalar());
            return cat;
        }, ct);

    public Task UpdateCategoryAsync(Category cat, CancellationToken ct = default) =>
        Run(c =>
        {
            using var cmd = Cmd(c, "UPDATE Categories SET Name=$n,Color=$c,Description=$d WHERE Id=$i",
                ("$n", cat.Name.Trim()), ("$c", cat.Color), ("$d", cat.Description), ("$i", cat.Id));
            return cmd.ExecuteNonQuery();
        }, ct);

    public Task DeleteCategoryAsync(int id, int fallbackCategoryId, CancellationToken ct = default) =>
        Run(c =>
        {
            if (id == fallbackCategoryId) throw new InvalidOperationException("Cannot delete the fallback category.");
            using var tx = c.BeginTransaction();
            foreach (var sql in new[]
            {
                "UPDATE Applications SET CategoryId=$f WHERE CategoryId=$i",
                "UPDATE OR IGNORE ClassificationRules SET CategoryId=$f WHERE CategoryId=$i",
                "DELETE FROM Categories WHERE Id=$i",
            })
            {
                using var cmd = Cmd(c, sql, ("$f", fallbackCategoryId), ("$i", id));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return 0;
        }, ct);

    public Task<IReadOnlyList<ClassificationRule>> GetRulesAsync(CancellationToken ct = default) =>
        Run<IReadOnlyList<ClassificationRule>>(c =>
        {
            using var cmd = Cmd(c, "SELECT Id,MatchType,Pattern,CategoryId FROM ClassificationRules ORDER BY MatchType DESC, Id");
            using var r = cmd.ExecuteReader();
            var list = new List<ClassificationRule>();
            while (r.Read()) list.Add(new ClassificationRule { Id = r.GetInt32(0), MatchType = (RuleMatchType)r.GetInt32(1), Pattern = r.GetString(2), CategoryId = r.GetInt32(3) });
            return list;
        }, ct);

    public Task<ClassificationRule> AddRuleAsync(ClassificationRule rule, CancellationToken ct = default) =>
        Run(c =>
        {
            using var cmd = Cmd(c, """
                INSERT INTO ClassificationRules(MatchType,Pattern,CategoryId) VALUES($m,$p,$c)
                ON CONFLICT(MatchType,Pattern) DO UPDATE SET CategoryId=excluded.CategoryId;
                SELECT Id FROM ClassificationRules WHERE MatchType=$m AND Pattern=$p;
                """, ("$m", (int)rule.MatchType), ("$p", rule.Pattern.Trim()), ("$c", rule.CategoryId));
            rule.Id = Convert.ToInt32(cmd.ExecuteScalar());
            return rule;
        }, ct);

    public Task DeleteRuleAsync(int id, CancellationToken ct = default) =>
        Run(c => { using var cmd = Cmd(c, "DELETE FROM ClassificationRules WHERE Id=$i", ("$i", id)); return cmd.ExecuteNonQuery(); }, ct);

    // ------------------------------------------------------------ settings
    public Task<IReadOnlyDictionary<string, string>> GetAllSettingsAsync(CancellationToken ct = default) =>
        Run<IReadOnlyDictionary<string, string>>(c =>
        {
            using var cmd = Cmd(c, "SELECT Key,Value FROM Settings");
            using var r = cmd.ExecuteReader();
            var d = new Dictionary<string, string>();
            while (r.Read()) d[r.GetString(0)] = r.GetString(1);
            return d;
        }, ct);

    public Task SetSettingsAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct = default) =>
        Run(c =>
        {
            using var tx = c.BeginTransaction();
            foreach (var (k, v) in values)
            {
                using var cmd = Cmd(c, "INSERT INTO Settings(Key,Value) VALUES($k,$v) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value", ("$k", k), ("$v", v));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return 0;
        }, ct);
}
