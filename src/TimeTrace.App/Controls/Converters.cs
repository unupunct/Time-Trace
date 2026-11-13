using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TimeTrace.App.Controls;

public sealed class HexBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, Brush> Cache = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = value as string;
        if (string.IsNullOrEmpty(hex)) return Brushes.Gray;
        if (Cache.TryGetValue(hex, out var b)) return b;
        try { var sb = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); sb.Freeze(); Cache[hex] = sb; return sb; }
        catch (Exception) { return Brushes.Gray; }
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class IconPathConverter : IValueConverter
{
    private static readonly Dictionary<string, ImageSource?> Cache = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value as string;
        if (string.IsNullOrEmpty(path)) return null;
        if (Cache.TryGetValue(path, out var img)) return img;
        try
        {
            if (!File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;       // do not keep the file locked
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = 48;
            bmp.EndInit();
            bmp.Freeze();
            Cache[path] = bmp;
            return bmp;
        }
        catch (Exception) { return null; }
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class ResourceKeyBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Services.ThemeService.Brush(value as string ?? "FaintBrush");
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool invert = parameter as string == "invert";
        bool has = value != null && !(value is string s && s.Length == 0);
        return has ^ invert ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        targetType == typeof(Visibility) ? ((value as bool?) == true ? Visibility.Collapsed : Visibility.Visible) : !((value as bool?) == true);
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => !((v as bool?) == true);
}
