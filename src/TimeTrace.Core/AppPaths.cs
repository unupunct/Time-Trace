using System.Text.Json;

namespace TimeTrace.Core;

/// <summary>
/// Locations of all user data. Everything lives outside the executable, in the user's local application data folder
/// (or a folder given with --data-dir, which also enables portable use).
/// </summary>
public sealed class AppPaths
{
    public string DataDirectory { get; }
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string IconCacheDirectory => Path.Combine(DataDirectory, "icons");
    public string BackupDirectory => Path.Combine(DataDirectory, "backups");
    private string BootstrapFile => Path.Combine(DataDirectory, "bootstrap.json");
    public string DefaultDatabasePath => Path.Combine(DataDirectory, "timetrace.db");

    public AppPaths(string? overrideDirectory = null)
    {
        DataDirectory = Path.GetFullPath(overrideDirectory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TimeTrace"));
        Directory.CreateDirectory(DataDirectory);
    }

    private sealed class Bootstrap { public string? DatabasePath { get; set; } }

    /// <summary>The configured database location (the location itself cannot live inside the database).</summary>
    public string DatabasePath
    {
        get
        {
            try
            {
                if (File.Exists(BootstrapFile))
                {
                    var b = JsonSerializer.Deserialize<Bootstrap>(File.ReadAllText(BootstrapFile));
                    if (!string.IsNullOrWhiteSpace(b?.DatabasePath) && Path.IsPathFullyQualified(b.DatabasePath)) return b.DatabasePath;
                }
            }
            catch (Exception) { /* fall back to default */ }
            return DefaultDatabasePath;
        }
    }

    public void SetDatabasePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Database path must be absolute.");
        File.WriteAllText(BootstrapFile, JsonSerializer.Serialize(new Bootstrap { DatabasePath = Path.GetFullPath(path) }));
    }
}
