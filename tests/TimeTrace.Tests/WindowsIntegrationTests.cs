using TimeTrace.Windows;

namespace TimeTrace.Tests;

/// <summary>Tests that touch the real Windows APIs. They only assert on structure, never on personal activity.</summary>
[Trait("Category", "Integration")]
public class WindowsIntegrationTests
{
    [Fact]
    public void Foreground_provider_does_not_throw_and_returns_consistent_data()
    {
        var info = new WindowsForegroundProvider().GetForegroundWindow();
        if (info == null) return;                       // headless session: no foreground window
        Assert.True(info.ProcessId > 0);
        Assert.False(string.IsNullOrWhiteSpace(info.ExecutableName));
        Assert.False(string.IsNullOrWhiteSpace(info.AppName));
    }

    [Fact]
    public void Idle_provider_returns_non_negative_time()
    {
        var idle = new WindowsIdleProvider().GetIdleTime();
        Assert.True(idle >= TimeSpan.Zero);
        Assert.True(idle < TimeSpan.FromDays(50));
    }

    [Fact]
    public void Startup_command_quotes_path_and_adds_background_flag()
    {
        Assert.Equal("\"C:\\Program Files\\TimeTrace\\TimeTrace.exe\" --background",
            StartupManager.BuildCommand(@"C:\Program Files\TimeTrace\TimeTrace.exe", true));
        Assert.Equal("\"C:\\a\\TimeTrace.exe\"", StartupManager.BuildCommand(@"C:\a\TimeTrace.exe", false));
    }

    [Fact]
    public void Startup_entry_can_be_enabled_and_disabled_using_a_private_value_name()
    {
        var m = new StartupManager("TimeTraceTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(m.IsEnabled());
            m.Enable(@"C:\x\TimeTrace.exe", false);
            Assert.True(m.IsEnabled());
        }
        finally { m.Disable(); }
        Assert.False(m.IsEnabled());
    }
}
