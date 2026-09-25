using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Godox.Desktop;

public sealed class Hotkeys(Window window, DeviceManager manager) : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private readonly Dictionary<int, string> ids = [];
    private HwndSource? source;
    private IntPtr handle;
    public int Count => ids.Count;
    internal void ProbeMessageDelivery()
    {
        if (ids.Count > 0) PostMessage(handle, 0x0312, new IntPtr(ids.Keys.First()), IntPtr.Zero);
    }
    public static (uint Modifiers, uint Key)? Parse(string shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) return null;
        uint modifiers = 0;
        Key key = Key.None;
        foreach (var part in shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": modifiers |= 2; continue;
                case "ALT": modifiers |= 1; continue;
                case "SHIFT": modifiers |= 4; continue;
                case "WIN": modifiers |= 8; continue;
            }
            if (key != Key.None) throw new ArgumentException("В сочетании должна быть одна основная клавиша.");
            var name = part.Length == 1 && char.IsDigit(part[0]) ? "D" + part : part;
            if (!Enum.TryParse(name, true, out key) || key is Key.None or Key.F12 || KeyInterop.VirtualKeyFromKey(key) == 0)
                throw new ArgumentException("Неверная клавиша. Например: Ctrl+Alt+1 или Ctrl+Shift+F5. F12 зарезервирована.");
        }
        if (modifiers == 0 || key == Key.None) throw new ArgumentException("Укажите Ctrl/Alt/Shift/Win и основную клавишу.");
        return (modifiers | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(key));
    }
    public void Apply()
    {
        Dispose();
        if (!manager.Settings.HotkeysEnabled) return;
        handle = new WindowInteropHelper(window).Handle;
        source = HwndSource.FromHwnd(handle);
        source?.AddHook(Hook);
        int id = 100;
        foreach (var device in manager.Snapshot()) Register(++id, device.Profile.Hotkey, device.Profile.Id);
        Register(++id, "Ctrl+Alt+0", "__off");
    }
    private void Register(int id, string shortcut, string device)
    {
        var parsed = Parse(shortcut);
        if (parsed is null) return;
        if (RegisterHotKey(handle, id, parsed.Value.Modifiers, parsed.Value.Key)) ids[id] = device;
        else manager.Log($"Не удалось зарегистрировать {shortcut}: сочетание занято другим приложением.");
    }
    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && ids.TryGetValue(wParam.ToInt32(), out var id))
        {
            handled = true;
            _ = Execute(id);
        }
        return IntPtr.Zero;
    }
    private async Task Execute(string id)
    {
        try { if (id == "__off") await manager.AllOff(); else await manager.Act(id, "toggle"); }
        catch (Exception exc) { manager.Log($"Хоткей: {exc.Message}"); }
    }
    public void Dispose()
    {
        foreach (var id in ids.Keys) UnregisterHotKey(handle, id);
        ids.Clear();
        source?.RemoveHook(Hook);
        source = null;
    }
}
