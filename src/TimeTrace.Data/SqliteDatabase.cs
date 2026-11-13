using System.Globalization;
using Microsoft.Data.Sqlite;
using TimeTrace.Core.Tracking;

namespace TimeTrace.Data;

/// <summary>Owns the SQLite file: connection creation, schema migrations, backup and restore.</summary>
public sealed class SqliteDatabase
{
    public string Path { get; }
    private readonly string _connectionString;

    public const int CurrentSchemaVersion = 2;

    public SqliteDatabase(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true, ForeignKeys = true, DefaultTimeout = 15,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    // ---------------------------------------------------------------- migrations

    public void Migrate()
    {
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        var version = Convert.ToInt32(Scalar(c, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version > CurrentSchemaVersion)
            throw new InvalidOperationException($"Database schema version {version} is newer than this application supports ({CurrentSchemaVersion}). Please update TimeTrace.");

        foreach (var (target, script) in Migrations)
        {
            if (version >= target) continue;
            using var tx = c.BeginTransaction();
            script(c, tx);
            Exec(c, $"PRAGMA user_version = {target};", tx);
            tx.Commit();
            version = target;
        }
    }

    private static readonly (int Version, Action<SqliteConnection, SqliteTransaction> Apply)[] Migrations =
    {
        (1, (c, tx) =>
        {
            Exec(c, """
                CREATE TABLE Categories(
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    Color TEXT NOT NULL,
                    Description TEXT);
                CREATE TABLE Applications(
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    ExecutableName TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    ExecutablePath TEXT,
                    IconCachePath TEXT,
                    CategoryId INTEGER REFERENCES Categories(Id) ON DELETE SET NULL,
                    IsExcluded INTEGER NOT NULL DEFAULT 0,
                    FirstSeen TEXT NOT NULL,
                    LastSeen TEXT NOT NULL);
                CREATE TABLE ActivitySessions(
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ApplicationId INTEGER REFERENCES Applications(Id) ON DELETE CASCADE,
                    ProcessId INTEGER NOT NULL DEFAULT 0,
                    WindowTitle TEXT,
                    StartTimeUtc TEXT NOT NULL,
                    EndTimeUtc TEXT NOT NULL,
                    DurationSeconds REAL NOT NULL,
                    SessionType INTEGER NOT NULL DEFAULT 0,
                    IsOpen INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE TrackingEvents(
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    EventType TEXT NOT NULL,
                    TimestampUtc TEXT NOT NULL,
                    Details TEXT);
                CREATE TABLE Settings(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                CREATE INDEX IX_Sessions_Start ON ActivitySessions(StartTimeUtc);
                CREATE INDEX IX_Sessions_End ON ActivitySessions(EndTimeUtc);
                CREATE INDEX IX_Sessions_App_Start ON ActivitySessions(ApplicationId, StartTimeUtc);
                CREATE INDEX IX_Sessions_Open ON ActivitySessions(IsOpen) WHERE IsOpen = 1;
                CREATE INDEX IX_Apps_Category ON Applications(CategoryId);
                CREATE INDEX IX_Events_Time ON TrackingEvents(TimestampUtc);
                """, tx);
            foreach (var (name, color, desc) in DefaultCategories.All)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO Categories(Name,Color,Description) VALUES($n,$c,$d)";
                cmd.Parameters.AddWithValue("$n", name); cmd.Parameters.AddWithValue("$c", color); cmd.Parameters.AddWithValue("$d", desc);
                cmd.ExecuteNonQuery();
            }
        }),
        (2, (c, tx) =>
        {
            Exec(c, """
                CREATE TABLE ClassificationRules(
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    MatchType INTEGER NOT NULL,
                    Pattern TEXT NOT NULL COLLATE NOCASE,
                    CategoryId INTEGER NOT NULL REFERENCES Categories(Id) ON DELETE CASCADE,
                    UNIQUE(MatchType, Pattern));
                """, tx);
            foreach (var (exe, cat) in DefaultCategories.Rules)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO ClassificationRules(MatchType,Pattern,CategoryId) SELECT 0,$p,Id FROM Categories WHERE Name=$cat";
                cmd.Parameters.AddWithValue("$p", exe); cmd.Parameters.AddWithValue("$cat", cat);
                cmd.ExecuteNonQuery();
            }
        }),
    };

    // ---------------------------------------------------------------- backup / restore

    /// <summary>Creates a consistent copy of the database using the SQLite online backup API.</summary>
    public void BackupTo(string destinationPath)
    {
        var dest = System.IO.Path.GetFullPath(destinationPath);
        if (string.Equals(dest, Path, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Backup target must differ from the live database.");
        var dir = System.IO.Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(dest)) File.Delete(dest);
        using var src = Open();
        using var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dest, Pooling = false }.ToString());
        dst.Open();
        src.BackupDatabase(dst);
    }

    /// <summary>Validates that a file is a usable TimeTrace database (integrity + expected tables + supported version).</summary>
    public static void ValidateBackup(string file)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        if (!string.Equals(Convert.ToString(Scalar(c, "PRAGMA integrity_check;")), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The file failed the SQLite integrity check.");
        var tables = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));
        }
        foreach (var t in new[] { "Applications", "ActivitySessions", "Categories", "TrackingEvents", "Settings" })
            if (!tables.Contains(t, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException($"The file is not a TimeTrace database (missing table {t}).");
        var v = Convert.ToInt32(Scalar(c, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (v > CurrentSchemaVersion) throw new InvalidDataException("The backup was created by a newer version of TimeTrace.");
    }

    /// <summary>Replaces the live database with a validated backup. Callers must stop tracking first.</summary>
    public void RestoreFrom(string backupFile)
    {
        ValidateBackup(backupFile);
        SqliteConnection.ClearAllPools();
        var safety = Path + ".before-restore";
        if (File.Exists(Path)) File.Copy(Path, safety, overwrite: true);
        foreach (var ext in new[] { "-wal", "-shm" }) { var f = Path + ext; if (File.Exists(f)) File.Delete(f); }
        File.Copy(backupFile, Path, overwrite: true);
        Migrate();   // upgrades an older backup to the current schema
    }

    // ---------------------------------------------------------------- helpers

    internal static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    internal static string Ts(DateTime d) => DateTime.SpecifyKind(d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : d, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);

    internal static DateTime ParseTs(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
