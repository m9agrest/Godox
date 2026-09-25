using System.IO;
using Microsoft.Win32;

namespace Godox.Desktop;

// Windows owns this setting: reading Run avoids recreating a startup entry the user removed.
public sealed class WindowsStartup(string executable, string root, string dataDirectory,
    string registryPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
{
    public const string ValueName = "GodoxDesktop";
    public bool Enabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(registryPath); return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value); }
    }
    public static string QuotePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains('"')) throw new ArgumentException("Нужен полный путь без кавычек.");
        // CommandLineToArgvW requires doubled trailing backslashes before the closing quote.
        int trailing = path.Length - path.TrimEnd('\\').Length;
        return '"' + path + new string('\\', trailing) + '"';
    }
    public string Command => $"{QuotePath(executable)} --root {QuotePath(root)} --data-dir {QuotePath(dataDirectory)} --startup";
    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(registryPath, writable: true)
            ?? throw new IOException("Не удалось открыть настройки автозапуска Windows.");
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
