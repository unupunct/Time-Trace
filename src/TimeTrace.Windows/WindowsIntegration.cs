using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace TimeTrace.Windows;

/// <summary>Opt-in "start with Windows" using the standard per-user Run registry key (no admin rights needed).</summary>
public sealed class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _valueName;
    public StartupManager(string valueName = "TimeTrace") => _valueName = valueName;

    public static string BuildCommand(string exePath, bool startMinimized) =>
        $"\"{exePath}\"" + (startMinimized ? " --background" : "");

    public bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(_valueName) is string;
    }

    public void Enable(string exePath, bool startMinimized)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        k.SetValue(_valueName, BuildCommand(exePath, startMinimized), RegistryValueKind.String);
    }

    public void Disable()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        k?.DeleteValue(_valueName, throwOnMissingValue: false);
    }
}

/// <summary>Translates Windows power/session notifications into simple events.</summary>
public sealed class SystemStateMonitor : IDisposable
{
    public event Action? Suspending, Resumed, Locked, Unlocked, SessionEnding;

    public SystemStateMonitor()
    {
        SystemEvents.PowerModeChanged += OnPower;
        SystemEvents.SessionSwitch += OnSwitch;
        SystemEvents.SessionEnding += OnEnding;
    }

    private void OnPower(object? s, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Suspending?.Invoke();
        else if (e.Mode == PowerModes.Resume) Resumed?.Invoke();
    }

    private void OnSwitch(object? s, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect: Locked?.Invoke(); break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect: Unlocked?.Invoke(); break;
        }
    }

    private void OnEnding(object? s, SessionEndingEventArgs e) => SessionEnding?.Invoke();

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPower;
        SystemEvents.SessionSwitch -= OnSwitch;
        SystemEvents.SessionEnding -= OnEnding;
    }
}

/// <summary>Extracts and caches application icons as PNG files in the user's data folder.</summary>
public sealed class IconCache
{
    private readonly string _dir;
    public IconCache(string dir) { _dir = dir; Directory.CreateDirectory(dir); }

    /// <summary>Returns the cached PNG path for an executable, extracting it first if needed. Null when no icon is available.</summary>
    public string? GetOrCreate(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return null;
        var file = Path.Combine(_dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(exePath.ToLowerInvariant())))[..16] + ".png");
        if (File.Exists(file)) return file;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon == null) return null;
            using var bmp = icon.ToBitmap();
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            return file;
        }
        catch (Exception) { return null; }
    }
}
