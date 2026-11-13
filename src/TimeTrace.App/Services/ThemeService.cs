using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TimeTrace.App.Services;

/// <summary>Switches between the dark and light resource dictionaries and applies the user's accent colour.</summary>
public sealed class ThemeService
{
    private ResourceDictionary? _current;
    public bool IsDark { get; private set; } = true;
    public event Action? Changed;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public void Apply(string theme, string accentHex)
    {
        var app = System.Windows.Application.Current;
        IsDark = !string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/TimeTrace;component/Themes/{(IsDark ? "Dark" : "Light")}.xaml") };
        if (_current != null) app.Resources.MergedDictionaries.Remove(_current);
        app.Resources.MergedDictionaries.Insert(0, dict);
        _current = dict;

        var accent = (Color)ColorConverter.ConvertFromString(accentHex);
        app.Resources["AccentColor"] = accent;
        app.Resources["AccentBrush"] = Frozen(accent);
        app.Resources["AccentSoftBrush"] = Frozen(Color.FromArgb(IsDark ? (byte)0x28 : (byte)0x22, accent.R, accent.G, accent.B));
        double lum = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255.0;
        app.Resources["AccentFgBrush"] = Frozen(lum > 0.55 ? Color.FromRgb(0x0B, 0x1A, 0x10) : Colors.White);

        try { app.ThemeMode = IsDark ? ThemeMode.Dark : ThemeMode.Light; } catch (Exception) { /* Fluent theme unavailable: custom styles still apply */ }
        foreach (Window w in app.Windows) ApplyTitleBar(w);
        Changed?.Invoke();
    }

    public void ApplyTitleBar(Window w)
    {
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            int dark = IsDark ? 1 : 0;
            DwmSetWindowAttribute(h, 20, ref dark, sizeof(int));     // DWMWA_USE_IMMERSIVE_DARK_MODE
        }
        catch (Exception) { /* cosmetic only */ }
    }

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>Looks up a theme brush for custom-drawn controls.</summary>
    public static Brush Brush(string key, Brush? fallback = null) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? fallback ?? Brushes.Gray;
}
