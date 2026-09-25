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
    private EventWaitHandle? activation;
    private RegisteredWaitHandle? activationWait;
    public bool IsSessionEnding { get; private set; }
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
            activation = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\GodoxDesktop-Show-" + key);
            mutex = new Mutex(true, "Local\\GodoxDesktop-" + key, out var created);
            if (!created) { if (!e.Args.Contains("--startup")) activation.Set(); Shutdown(); return; }
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
            var startup = new WindowsStartup(Environment.ProcessPath ?? throw new IOException("Путь приложения неизвестен."), root, dataDirectory);
            var window = new MainWindow(store, manager, api, startup);
            MainWindow = window;
            activationWait = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => Dispatcher.BeginInvoke(window.RestoreFromTray), null, Timeout.Infinite, false);
            int visibleTransitions = 0;
            window.IsVisibleChanged += (_, _) => { if (window.IsVisible) visibleTransitions++; };
            window.Start(e.Args.Contains("--startup"));
            if (e.Args.Contains("--startup-smoke"))
            {
                if (string.Equals(store.DataDirectory, SettingsStore.DefaultDataDirectory, StringComparison.OrdinalIgnoreCase) || settings.AutoConnectEnabled)
                    throw new InvalidOperationException("Startup smoke requires isolated data and auto-connect off.");
                await Task.Delay(300);
                var folder = Path.Combine(dataDirectory, "artifacts"); Directory.CreateDirectory(folder);
                bool listening;
                using (var client = new System.Net.Sockets.TcpClient())
                {
                    try { await client.ConnectAsync("127.0.0.1", settings.ApiPort).WaitAsync(TimeSpan.FromSeconds(2)); listening = true; }
                    catch (System.Net.Sockets.SocketException) { listening = false; }
                    catch (TimeoutException) { listening = false; }
                }
                File.WriteAllText(Path.Combine(folder, "startup-smoke.json"), System.Text.Json.JsonSerializer.Serialize(new {
                    visible = window.IsVisible, visibleTransitions, listening, hotkeys = window.HotkeyCount,
                    startInTray = settings.StartInTray, closeToTray = settings.CloseToTray, apiEnabled = settings.ApiEnabled }));
                window.RestoreFromTray();
            }
            if (apiError is not null) manager.Log(apiError);
            if (e.Args.Contains("--smoke") || e.Args.Contains("--lifecycle-smoke") || e.Args.Contains("--mixer-smoke") || e.Args.Contains("--desktop-smoke") || e.Args.Contains("--startup-smoke"))
            {
                await Task.Delay(700);
                if (e.Args.Contains("--mixer-smoke")) await window.ProbeMixer();
                if (e.Args.Contains("--desktop-smoke")) await window.ProbeDesktop();
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
                window.MainTabs.SelectedItem = window.SettingsTab;
                await Task.Delay(100);
                Capture(window, Path.Combine(folder, "ui-settings.png"));
                var editor = new DeviceDialog(store, manager.Snapshot().FirstOrDefault()?.Profile, null) { Owner = window };
                editor.Show();
                await Task.Delay(100);
                Capture(editor, Path.Combine(folder, "ui-editor.png"));
                editor.Close();
                File.WriteAllText(Path.Combine(folder, "ui-smoke.json"), System.Text.Json.JsonSerializer.Serialize(new {
                    devices = manager.Snapshot().Count, connected = manager.Snapshot().Count(d => d.Status.Connected),
                    hotkeys = window.HotkeyCount, hotkeyDelivered,
                    api = api.Status, width = window.ActualWidth, height = window.ActualHeight }));
                window.ExitApplication();
            }
        }
        catch (Exception exc)
        {
            try
            { Directory.CreateDirectory(Path.Combine(dataDirectory, "logs")); File.AppendAllText(Path.Combine(dataDirectory, "logs", "errors.log"), exc + Environment.NewLine); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (!e.Args.Contains("--headless") && !e.Args.Any(a => a == "--smoke" || a.EndsWith("-smoke", StringComparison.Ordinal)))
                MessageBox.Show(exc.Message, "Godox — ошибка запуска");
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
        (MainWindow as MainWindow)?.DisposeTray();
        activationWait?.Unregister(null);
        activation?.Dispose();
        mutex?.Dispose();
        base.OnExit(e);
    }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsSessionEnding = true;
        deviceManager?.Stop();
        (MainWindow as MainWindow)?.DisposeTray();
        base.OnSessionEnding(e);
    }
}
