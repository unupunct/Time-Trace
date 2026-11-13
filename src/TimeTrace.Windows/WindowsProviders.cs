using System.Diagnostics;
using System.Text;
using TimeTrace.Core.Abstractions;
using TimeTrace.Windows.Interop;

namespace TimeTrace.Windows;

/// <summary>Reads the active window using Win32 APIs. Only the process and window title are read - never input, screen or clipboard content.</summary>
public sealed class WindowsForegroundProvider : IForegroundWindowProvider
{
    private readonly Dictionary<string, string> _nameCache = new(StringComparer.OrdinalIgnoreCase);

    public ForegroundWindowInfo? GetForegroundWindow()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;
        bool minimized = NativeMethods.IsIconic(hwnd);
        var title = GetTitle(hwnd);

        var (name, path) = GetProcess(pid);
        if (name == null) return null;

        // UWP/Store apps are hosted by ApplicationFrameHost; the real app owns a child window.
        if (string.Equals(name, "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            uint realPid = 0;
            NativeMethods.EnumChildWindows(hwnd, (child, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(child, out var cp);
                if (cp != 0 && cp != pid) { realPid = cp; return false; }
                return true;
            }, IntPtr.Zero);
            if (realPid != 0)
            {
                var (n2, p2) = GetProcess(realPid);
                if (n2 != null) { name = n2; path = p2; pid = realPid; }
            }
        }

        return new ForegroundWindowInfo((int)pid, name, path, DisplayName(name, path), title, minimized);
    }

    private static string? GetTitle(IntPtr hwnd)
    {
        try
        {
            int len = NativeMethods.GetWindowTextLengthW(hwnd);
            if (len <= 0) return null;
            var sb = new StringBuilder(Math.Min(len, 1024) + 1);
            NativeMethods.GetWindowTextW(hwnd, sb, sb.Capacity);
            var s = sb.ToString().Trim();
            return s.Length == 0 ? null : s;
        }
        catch (Exception) { return null; }
    }

    private static (string? Name, string? Path) GetProcess(uint pid)
    {
        IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (NativeMethods.QueryFullProcessImageNameW(h, 0, sb, ref size))
                {
                    var full = sb.ToString();
                    return (System.IO.Path.GetFileName(full), full);
                }
            }
            finally { NativeMethods.CloseHandle(h); }
        }
        // Elevated / protected process: the path is inaccessible, but the process name usually is.
        try { return (Process.GetProcessById((int)pid).ProcessName + ".exe", null); }
        catch (Exception) { return (null, null); }
    }

    private string DisplayName(string exeName, string? path)
    {
        var key = path ?? exeName;
        if (_nameCache.TryGetValue(key, out var cached)) return cached;
        string name = System.IO.Path.GetFileNameWithoutExtension(exeName);
        if (path != null)
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                var d = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription : vi.ProductName;
                if (!string.IsNullOrWhiteSpace(d)) name = d.Trim();
            }
            catch (Exception) { /* keep file name */ }
        }
        if (_nameCache.Count > 500) _nameCache.Clear();
        _nameCache[key] = name;
        return name;
    }
}

public sealed class WindowsIdleProvider : IIdleTimeProvider
{
    public TimeSpan GetIdleTime()
    {
        var info = new NativeMethods.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        if (!NativeMethods.GetLastInputInfo(ref info)) return TimeSpan.Zero;
        // Both values are 32-bit tick counts; unsigned subtraction handles the 49.7-day wrap-around.
        uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMs);
    }
}
