using System.IO;
using Microsoft.Win32;

namespace DeskRelay;

static class StartupSettings
{
    const string SettingsPath = @"Software\CrossDesk";
    const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsInstalled
    {
        get
        {
            using var installation = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskRelay_is1");
            return installation?.GetValue("InstallLocation") is string location &&
                string.Equals(Environment.ProcessPath, Path.Combine(location, "CrossDesk.exe"),
                    StringComparison.OrdinalIgnoreCase);
        }
    }

    public static bool Load()
    {
        using var settings = Registry.CurrentUser.CreateSubKey(SettingsPath);
        if (settings.GetValue("StartupConfigured") is null && IsInstalled)
        {
            SetEnabled(true);
        }
        using var run = Registry.CurrentUser.OpenSubKey(RunPath);
        return string.Equals(run?.GetValue("CrossDesk") as string, Command, StringComparison.OrdinalIgnoreCase);
    }

    static string Command => $"\"{Environment.ProcessPath ?? throw new IOException("CrossDesk executable path is unavailable.")}\"";

    public static void SetEnabled(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunPath);
        if (enabled) run.SetValue("CrossDesk", Command);
        else run.DeleteValue("CrossDesk", false);
        using var settings = Registry.CurrentUser.CreateSubKey(SettingsPath);
        settings.SetValue("StartupConfigured", 1);
    }
}
