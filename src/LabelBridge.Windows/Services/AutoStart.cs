using Microsoft.Win32;

namespace LabelBridge.Services;

internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LabelBridge";

    public static bool IsSupported =>
        Application.ExecutablePath.EndsWith("LabelBridge.exe", StringComparison.OrdinalIgnoreCase);

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch { return false; }
    }

    public static void Set(bool enabled)
    {
        if (!IsSupported) return;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key == null) return;

            if (enabled) key.SetValue(ValueName, $"\"{Application.ExecutablePath}\" --tray");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { }
    }
}
