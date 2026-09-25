using Godox.Desktop;
using Microsoft.Win32;
using System.Runtime.InteropServices;

var keyPath = @"Software\Godox-Desktop-Tests-" + Guid.NewGuid().ToString("N");
var executable = @"C:\Test App\Godox.Desktop.exe";
var root = @"C:\Test App\";
var data = @"C:\Test Data\Settings";
var startup = new WindowsStartup(executable, root, data, keyPath);
void Check(bool value, string text) { if (!value) throw new Exception(text); Console.WriteLine("PASS: " + text); }
try
{
    Check(!startup.Enabled, "Startup is off without a registered entry");
    using (var key = Registry.CurrentUser.CreateSubKey(keyPath)) key.SetValue("Unrelated", "untouched");
    startup.SetEnabled(true);
    Check(new WindowsStartup(executable, root, data, keyPath).Enabled, "Startup registration survives recreating the settings service");
    using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
    {
        var command = (string)key!.GetValue(WindowsStartup.ValueName)!;
        var argv = Native.CommandLineToArgvW(command, out int count);
        try
        {
            var arguments = Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))).ToArray();
            Check(arguments.SequenceEqual(new[] { executable, "--root", root, "--data-dir", data, "--startup" }), "Windows parses paths with spaces and trailing backslash correctly");
        }
        finally { Native.LocalFree(argv); }
    }
    startup.SetEnabled(false); startup.SetEnabled(false);
    Check(!startup.Enabled, "Disabling startup removes the entry and is idempotent");
    using (var key = Registry.CurrentUser.OpenSubKey(keyPath)) Check((string?)key!.GetValue("Unrelated") == "untouched", "Other startup entries are untouched");
    Check(!new Settings().CloseToTray, "Existing settings keep normal close behavior by default");
}
finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); }

static class Native
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr memory);
}
