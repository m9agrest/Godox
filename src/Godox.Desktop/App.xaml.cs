using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Godox.Desktop;

public partial class App : Application
{
    private Mutex? mutex;
    private BackendBridge? bridge;
    private LocalApi? api;
    private readonly CancellationTokenSource monitorCancellation = new();
    private Task? monitor;
    private Task? mixerPump;
    private DeviceManager? deviceManager;
    private bool stopping;
    public async Task StopServices()
    {
        if (stopping) return;
        stopping = true;
        deviceManager?.Stop();
        monitorCancellation.Cancel();
        if (mixerPump is not null) try { await mixerPump; } catch (OperationCanceledException) { }
        if (monitor is not null) try { await monitor.WaitAsync(TimeSpan.FromSeconds(3)); } catch (OperationCanceledException) { } catch (TimeoutException) { }
        try { if (api is not null) await api.DisposeAsync(); }
        finally { if (bridge is not null) await bridge.DisposeAsync(); }
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? root = null;
        string dataDirectory = SettingsStore.DefaultDataDirectory;
        try
        {
            root = FindRoot(e.Args);
            var dataIndex = Array.IndexOf(e.Args, "--data-dir");
            if (dataIndex >= 0)
            {
                if (dataIndex + 1 >= e.Args.Length) throw new ArgumentException("Для --data-dir нужна папка.");
                dataDirectory = Path.GetFullPath(e.Args[dataIndex + 1]);
            }
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(dataDirectory).ToUpperInvariant())))[..20];
            mutex = new Mutex(true, "Local\\GodoxDesktop-" + key, out var created);
            if (!created) { MessageBox.Show("Godox Desktop уже запущен.", "Godox"); Shutdown(); return; }
            var store = new SettingsStore(root, dataDirectory);
            var settings = store.Load();
            if (settings.ApiPort is < 1024 or > 65535 || string.IsNullOrWhiteSpace(settings.ApiToken))
                throw new InvalidDataException("Некорректный порт или токен в " + store.FilePath);
            bridge = new BackendBridge(root, settings.PythonPath);
            var manager = new DeviceManager(store, settings, bridge);
            deviceManager = manager;
            mixerPump = manager.Pump(monitorCancellation.Token);
            monitor = Monitor(manager, monitorCancellation.Token);
            bridge.Diagnostic += manager.Log;
            api = new LocalApi(manager);
            string? apiError = null;
            try { await api.Start(); }
            catch (Exception exc) { apiError = "HTTP API не запущен: " + exc.Message; }
            if (e.Args.Contains("--headless"))
            {
                if (apiError is not null) throw new IOException(apiError);
                return;
            }
            var window = new MainWindow(store, manager, api);
            MainWindow = window;
            window.Show();
            if (apiError is not null) manager.Log(apiError);
            if (e.Args.Contains("--smoke") || e.Args.Contains("--lifecycle-smoke") || e.Args.Contains("--mixer-smoke"))
            {
                await Task.Delay(700);
                if (e.Args.Contains("--mixer-smoke")) await window.ProbeMixer();
                bool hotkeyDelivered = false;
                manager.Logged += line => { if (line.Contains("Хоткей:")) hotkeyDelivered = true; };
                // Own-window WM_HOTKEY delivery only; devices remain disconnected.
                window.ProbeHotkey();
                for (int i = 0; i < 20 && !hotkeyDelivered; i++) await Task.Delay(100);
                if (e.Args.Contains("--lifecycle-smoke"))
                {
                    foreach (var device in manager.Snapshot()) await manager.Act(device.Profile.Id, "connect");
                    await Task.Delay(200);
                }
                var folder = Path.Combine(dataDirectory, "artifacts"); Directory.CreateDirectory(folder);
                Capture(window, Path.Combine(folder, "ui-smoke.png"));
                window.MainTabs.SelectedIndex = 1;
                await Task.Delay(100);
                Capture(window, Path.Combine(folder, "ui-integration.png"));
                var editor = new DeviceDialog(store, manager.Snapshot().FirstOrDefault()?.Profile, null) { Owner = window };
                editor.Show();
                await Task.Delay(100);
                Capture(editor, Path.Combine(folder, "ui-editor.png"));
                editor.Close();
                File.WriteAllText(Path.Combine(folder, "ui-smoke.json"), System.Text.Json.JsonSerializer.Serialize(new {
                    devices = manager.Snapshot().Count, connected = manager.Snapshot().Count(d => d.Status.Connected),
                    hotkeys = window.HotkeyCount, hotkeyDelivered,
                    api = api.Status, width = window.ActualWidth, height = window.ActualHeight }));
                window.Close();
            }
        }
        catch (Exception exc)
        {
            try
            { Directory.CreateDirectory(Path.Combine(dataDirectory, "logs")); File.AppendAllText(Path.Combine(dataDirectory, "logs", "errors.log"), exc + Environment.NewLine); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (!e.Args.Contains("--headless")) MessageBox.Show(exc.Message, "Godox — ошибка запуска");
            await StopServices();
            Shutdown(1);
        }
    }
    private static async Task Monitor(DeviceManager manager, CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(cancellation))
        {
            await manager.RefreshConnections();
            if (!cancellation.IsCancellationRequested) await manager.AutoConnect();
        }
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * dpi.DpiScaleX),
            (int)(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
    private static string FindRoot(string[] args)
    {
        var index = Array.IndexOf(args, "--root");
        if (index >= 0 && index + 1 < args.Length) return Path.GetFullPath(args[index + 1]);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "backend", "worker.py"))) return dir.FullName;
        throw new DirectoryNotFoundException("Папка backend не найдена. Запустите start.cmd из папки проекта.");
    }
    protected override void OnExit(ExitEventArgs e)
    {
        mutex?.Dispose();
        base.OnExit(e);
    }
}
