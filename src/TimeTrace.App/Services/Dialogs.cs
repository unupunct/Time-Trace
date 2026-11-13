using System.Windows;
using Microsoft.Win32;

namespace TimeTrace.App.Services;

/// <summary>Thin wrappers over standard dialogs so view models stay free of UI types.</summary>
public static class Dialogs
{
    private static Window? Owner => System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? System.Windows.Application.Current?.MainWindow;

    public static bool Confirm(string title, string message, string confirmText = "Yes") =>
        System.Windows.MessageBox.Show(Owner!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public static void Info(string title, string message) =>
        System.Windows.MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Error(string title, string message) =>
        System.Windows.MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public static string? PickFolder(string title, string? initial = null)
    {
        var d = new OpenFolderDialog { Title = title };
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) d.InitialDirectory = initial;
        return d.ShowDialog(Owner) == true ? d.FolderName : null;
    }

    public static string? SaveFile(string title, string fileName, string filter)
    {
        var d = new SaveFileDialog { Title = title, FileName = fileName, Filter = filter, OverwritePrompt = true };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public static string? OpenFile(string title, string filter)
    {
        var d = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            if (File.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception) { /* nothing sensible to do */ }
    }
}
