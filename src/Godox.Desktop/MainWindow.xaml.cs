using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Godox.Desktop;

public partial class MainWindow : Window
{
    private readonly SettingsStore store;
    private readonly DeviceManager manager;
    private readonly LocalApi api;
    private readonly WindowsStartup startup;
    private TrayIcon? tray;
    private bool exitRequested;
    private readonly ObservableCollection<MixerItem> items = [];
    private Hotkeys? hotkeys;
    private bool closing, closed, syncing = true;
    private int operations;
    private readonly HashSet<string> active = [];
    public int HotkeyCount => hotkeys?.Count ?? 0;
    internal void ProbeHotkey() => hotkeys?.ProbeMessageDelivery();
    internal async Task ProbeMixer()
    {
        // Used only with --mixer-smoke and an isolated fixture, never live sessions.
        if (manager.Snapshot().Any(d => d.Status.Connected) || manager.Settings.AutoConnectEnabled)
            throw new InvalidOperationException("Mixer UI smoke requires disconnected fixtures and auto-connect off.");
        static IEnumerable<Slider> Sliders(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Slider slider) yield return slider;
                foreach (var nested in Sliders(child)) yield return nested;
            }
        }
        UpdateLayout();
        var first = items[0]; var originalLevel = first.Level; var originalMaster = manager.Settings.MasterLevel;
        var slider = Sliders(Devices).First(s => s.Orientation == Orientation.Vertical && (string)s.Tag == first.Id);
        slider.SetCurrentValue(Slider.ValueProperty, 73d);
        await Task.Delay(100);
        MasterSlider.SetCurrentValue(Slider.ValueProperty, 50d);
        await Task.Delay(100);
        if (manager.Profile(first.Id).MixerLevel != 73 || slider.Value != 73 || first.View.EffectiveBrightness != 37)
            throw new InvalidOperationException("UI slider lost its value during master update.");
        var sameSlider = Sliders(Devices).First(s => s.Orientation == Orientation.Vertical && (string)s.Tag == first.Id);
        if (!ReferenceEquals(slider, sameSlider)) throw new InvalidOperationException("UI slider was recreated during update.");
        manager.Move(first.Id, 1); await Task.Delay(100);
        if (items.Last().Id != first.Id) throw new InvalidOperationException("UI reorder failed.");
        manager.Move(first.Id, -1); manager.SetMixer(first.Id, originalLevel); manager.SetMixer(null, originalMaster);
        await Task.Delay(100);
        var visualValues = Sliders(Devices).Where(s => s.Orientation == Orientation.Vertical)
            .Select(s => new { id = s.Tag, value = s.Value, level = ((MixerItem)s.DataContext).Level }).ToArray();
        Directory.CreateDirectory(Path.Combine(store.DataDirectory, "artifacts"));
        File.WriteAllText(Path.Combine(store.DataDirectory, "artifacts", "mixer-values.json"), System.Text.Json.JsonSerializer.Serialize(visualValues));
        if (visualValues.Any(v => v.value != v.level)) throw new InvalidOperationException("Visual slider does not match its saved level after reorder.");
        static IEnumerable<Button> Buttons(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Button button) yield return button;
                foreach (var nested in Buttons(child)) yield return nested;
            }
        }
        var original = first.View;
        first.Update(original with { Connecting = true });
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        var connectionButton = Buttons(Devices).First(b => ReferenceEquals(b.DataContext, first) && (string?)b.Content == "Подключается…");
        if (connectionButton.IsEnabled) throw new InvalidOperationException("Connecting button is still enabled.");
        first.Update(original);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (!connectionButton.IsEnabled || (string?)connectionButton.Content != "Подключить")
            throw new InvalidOperationException("Connection button did not recover after pending state.");
        manager.Log("PASS: UI slider events, master proportions, stable controls, and reorder.");
    }
    public MainWindow(SettingsStore store, DeviceManager manager, LocalApi api, WindowsStartup startup)
    {
        this.store = store; this.manager = manager; this.api = api; this.startup = startup;
        InitializeComponent(); Devices.ItemsSource = items;
        StartupEnabled.IsChecked = startup.Enabled;
        CloseToTrayEnabled.IsChecked = manager.Settings.CloseToTray;
        StartInTrayEnabled.IsChecked = manager.Settings.StartInTray;
        HotkeysEnabled.IsChecked = manager.Settings.HotkeysEnabled;
        ApiEnabled.IsChecked = manager.Settings.ApiEnabled;
        ApiPort.Text = manager.Settings.ApiPort.ToString(); ApiStatus.Text = api.Status;
        ApiExamples.Text = "GET  /api/devices\nGET  /api/mixer\nPOST /api/connect-all\nPOST /api/disconnect-all\nPOST /api/mixer  {\"level\": 50, \"muted\": false}\nPOST /api/devices/{id}/mixer  {\"level\": 100}\nPOST /api/devices/{id}/set  {\"brightness\": 100, \"cct\": 4000}\nPOST /api/devices/{id}/connect (или disconnect, status, toggle, off)\n\nID берётся из GET /api/devices. brightness в set — уровень лампы до общего множителя. mixer принимает уровень и/или muted. status читает фактические значения с прибора.";
        manager.Changed += () => Dispatcher.BeginInvoke(RefreshDevices);
        manager.Logged += line => Dispatcher.BeginInvoke(() => {
            if (LogBox.Text.Length > 50000) LogBox.Text = LogBox.Text[^30000..];
            LogBox.AppendText(line + Environment.NewLine); LogBox.ScrollToEnd();
        });
        RefreshDevices();
    }
    public void Start(bool automatic)
    {
        // Create the message handle without showing the window, so startup in the
        // tray has working hotkeys and no visible window or taskbar flash.
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        hotkeys = new Hotkeys(this, manager); ApplyHotkeys();
        try { tray = new TrayIcon(this); }
        catch (Exception exc) { manager.Log("Значок трея недоступен: " + exc.Message); }
        if (!automatic || !manager.Settings.StartInTray || tray is null) Show();
    }
    public void RestoreFromTray()
    {
        if (closing || closed) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
    public void ExitApplication() { exitRequested = true; Close(); }
    public void DisposeTray() { tray?.Dispose(); tray = null; }
    private void ExitClicked(object sender, RoutedEventArgs e) => ExitApplication();
    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        Change(() => startup.SetEnabled(StartupEnabled.IsChecked == true));
        StartupEnabled.IsChecked = startup.Enabled;
    }
    private void CloseToTrayChanged(object sender, RoutedEventArgs e)
    {
        Change(() => manager.SetCloseToTray(CloseToTrayEnabled.IsChecked == true));
        CloseToTrayEnabled.IsChecked = manager.Settings.CloseToTray;
    }
    private void StartInTrayChanged(object sender, RoutedEventArgs e)
    {
        Change(() => manager.SetLaunchOptions(startInTray: StartInTrayEnabled.IsChecked == true));
        StartInTrayEnabled.IsChecked = manager.Settings.StartInTray;
    }
    private void ApiStartupChanged(object sender, RoutedEventArgs e)
    {
        Change(() => manager.SetLaunchOptions(apiAtStartup: ApiEnabled.IsChecked == true));
        ApiEnabled.IsChecked = manager.Settings.ApiEnabled;
    }
    internal async Task ProbeDesktop()
    {
        if (store.DataDirectory == SettingsStore.DefaultDataDirectory || manager.Settings.AutoConnectEnabled)
            throw new InvalidOperationException("Desktop smoke requires isolated data and auto-connect off.");
        var previous = manager.Settings.CloseToTray;
        manager.SetCloseToTray(true);
        Close();
        await Task.Delay(200);
        if (IsVisible || closing || tray is null) throw new InvalidOperationException("Closing did not keep the application in the tray.");
        bool hiddenHotkey = false;
        void ObserveHotkey(string line) { if (line.Contains("Хоткей:")) hiddenHotkey = true; }
        manager.Logged += ObserveHotkey;
        try
        {
            ProbeHotkey();
            for (int i = 0; i < 20 && !hiddenHotkey; i++) await Task.Delay(50);
            if (manager.Settings.HotkeysEnabled && items.Count > 0 && !hiddenHotkey)
                throw new InvalidOperationException("Hidden window no longer receives hotkey messages.");
        }
        finally { manager.Logged -= ObserveHotkey; }
        if (manager.Settings.ApiEnabled)
        {
            using var http = new System.Net.Http.HttpClient();
            using var response = await http.GetAsync(api.Status + "/health");
            response.EnsureSuccessStatusCode();
        }
        var activation = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "--root", store.Root, "--data-dir", store.DataDirectory }) activation.ArgumentList.Add(argument);
        using var second = System.Diagnostics.Process.Start(activation)!;
        await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);
        if (!IsVisible || closing) throw new InvalidOperationException("Tray restore failed.");
        manager.SetCloseToTray(previous);
        CloseToTrayEnabled.IsChecked = previous;
        manager.Log("PASS: close-to-tray keeps HTTP alive, second launch restores the window, explicit exit supported.");
    }
    private void RefreshDevices()
    {
        if (closing) return;
        syncing = true;
        try
        {
            var snapshot = manager.Snapshot();
            foreach (var old in items.Where(i => snapshot.All(d => d.Profile.Id != i.Id)).ToArray()) items.Remove(old);
            for (int index = 0; index < snapshot.Count; index++)
            {
                var view = snapshot[index]; var item = items.FirstOrDefault(i => i.Id == view.Profile.Id);
                if (item is null) { item = new MixerItem(view); items.Insert(index, item); }
                else { if (items.IndexOf(item) != index) items.Move(items.IndexOf(item), index); item.Update(view); }
            }
            MasterSlider.Value = manager.Settings.MasterLevel;
            MasterValue.Text = manager.Settings.MasterLevel + "%";
            MasterMute.Content = manager.Settings.MasterMuted ? "Вернуть свет" : "Погасить всё";
            AutoConnectEnabled.IsChecked = manager.Settings.AutoConnectEnabled;
        }
        finally { syncing = false; }
    }
    private static string Id(object sender) => ((FrameworkElement)sender).Tag as string ??
        (((FrameworkElement)sender).DataContext as MixerItem)?.Id ?? throw new InvalidOperationException("Устройство не найдено.");
    private void Change(Action action)
    {
        if (closing) return;
        try { action(); } catch (Exception exc) { StatusBar.Text = exc.Message; manager.Log(exc.Message); }
    }
    private void MasterChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (!syncing && IsLoaded) Change(() => manager.SetMixer(null, (int)e.NewValue)); }
    private void LevelChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (!syncing && ((FrameworkElement)sender).IsLoaded) Change(() => manager.SetMixer(Id(sender), (int)e.NewValue)); }
    private void TemperatureChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (!syncing && ((FrameworkElement)sender).IsLoaded) Change(() => manager.SetMixer(Id(sender), cct: (int)e.NewValue)); }
    private void ToggleMaster(object sender, RoutedEventArgs e) => Change(() => manager.SetMixer(null, muted: !manager.Settings.MasterMuted));
    private void DeviceMute(object sender, RoutedEventArgs e) => Change(() => manager.SetMixer(Id(sender), muted: !manager.Profile(Id(sender)).Muted));
    private void GlobalAutoChanged(object sender, RoutedEventArgs e) => Change(() => manager.SetAutoConnect(null, AutoConnectEnabled.IsChecked == true));
    private void DeviceAutoChanged(object sender, RoutedEventArgs e) => Change(() => manager.SetAutoConnect(Id(sender), ((CheckBox)sender).IsChecked == true));
    private void MoveLeft(object sender, RoutedEventArgs e) => Change(() => manager.Move(Id(sender), -1));
    private void MoveRight(object sender, RoutedEventArgs e) => Change(() => manager.Move(Id(sender), 1));
    private async Task Run(string key, string message, Func<Task> action)
    {
        if (closing || !active.Add(key)) return;
        operations++; Progress.Visibility = Visibility.Visible; StatusBar.Text = message;
        try { await action(); StatusBar.Text = "Готово. Подробности — в журнале."; }
        catch (Exception exc) { StatusBar.Text = exc.Message; manager.Log(exc.Message); }
        finally { active.Remove(key); if (--operations == 0) Progress.Visibility = Visibility.Collapsed; RefreshDevices(); }
    }
    private async Task ConnectDevice(string id)
    {
        var profile = manager.Profile(id);
        if (!File.Exists(profile.StatePath))
        {
            var resolved = store.ResolveBinding(profile.Address, profile.StatePath);
            if (File.Exists(resolved)) await manager.SaveProfile(profile with { StatePath = resolved });
            else
            {
                if (MessageBox.Show(this, $"{profile.Name}: создать привязку?\n\nСбросьте Bluetooth на светильнике и закройте Godox Light, затем нажмите OK. Если ключи уже есть, выберите файл в «Настроить».",
                    "Подключение нового прибора", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                await manager.Act(id, "provision");
            }
        }
        try { await manager.Act(id, "connect"); }
        catch (BackendException exc) when (exc.Code == "needs_provisioning")
        {
            if (MessageBox.Show(this, "Bluetooth светильника сброшен. Создать новую привязку к ПК? Старые ключи сохранятся в резервной копии.",
                "Восстановление подключения", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            await manager.Act(id, "provision"); await manager.Act(id, "connect");
        }
    }
    private async void DeviceConnection(object sender, RoutedEventArgs e)
    {
        var id = Id(sender); var view = manager.Snapshot().First(d => d.Profile.Id == id);
        if (view.Connecting) return;
        bool connected = view.Status.Connected;
        await Run(id, connected ? "Освобождение Bluetooth…" : "Подключение…", () => connected ? manager.Act(id, "disconnect") : ConnectDevice(id));
    }
    private async void ConnectAll(object sender, RoutedEventArgs e) => await Run("all", "Подключение светильников…", manager.ConnectAll);
    private async void DisconnectAll(object sender, RoutedEventArgs e) => await Run("all", "Отключение светильников…", manager.DisconnectAll);
    private async Task Edit(DeviceProfile? profile, IReadOnlyList<ScanResult>? scan = null)
    {
        if (profile is not null && manager.Snapshot().First(d => d.Profile.Id == profile.Id).Status.Connected)
            throw new InvalidOperationException("Отключите прибор перед изменением профиля.");
        var dialog = new DeviceDialog(store, profile, scan) { Owner = this };
        if (dialog.ShowDialog() == true) { await manager.SaveProfile(dialog.Result!); ApplyHotkeys(); }
    }
    private async void AddDevice(object sender, RoutedEventArgs e) => await Run("edit", "Добавление устройства…", () => Edit(null));
    private async void EditDevice(object sender, RoutedEventArgs e) => await Run("edit", "Настройка устройства…", () => Edit(manager.Profile(Id(sender))));
    private async void ScanDevices(object sender, RoutedEventArgs e) => await Run("scan", "Поиск Godox поблизости — около 8 секунд…", async () => {
        var results = await manager.Scan();
        if (results.Count == 0) { manager.Log("Godox не найден. Проверьте питание и Bluetooth."); return; }
        await Edit(null, results);
    });
    private void MoreActions(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender; button.ContextMenu.DataContext = button.DataContext;
        button.ContextMenu.PlacementTarget = button; button.ContextMenu.IsOpen = true;
    }
    private async void ExtraAction(object sender, RoutedEventArgs e)
    {
        var menu = (MenuItem)sender; var id = ((MixerItem)menu.DataContext).Id; var command = (string)menu.Tag;
        await Run(id, "Выполнение…", async () => {
            if (command == "remove")
            {
                if (MessageBox.Show(this, "Убрать прибор из списка? Файл ключей останется на диске.", "Godox", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                await manager.Remove(id); ApplyHotkeys(); return;
            }
            if (command == "provision" && MessageBox.Show(this, "Создать новую привязку? Сначала сбросьте Bluetooth прибора и закройте Godox Light. Старый файл ключей сохранится в резервной копии.",
                "Новая привязка", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            await manager.Act(id, command);
        });
    }
    private void ApplyHotkeys()
    {
        try { hotkeys?.Apply(); HotkeyStatus.Text = $"Зарегистрировано сочетаний: {hotkeys?.Count ?? 0}. Конфликты показаны в журнале."; }
        catch (Exception exc) { manager.Log(exc.Message); }
    }
    private async void SaveOptions(object sender, RoutedEventArgs e) => await Run("options", "Сохранение настроек…", async () => {
        if (!int.TryParse(ApiPort.Text, out var port)) throw new ArgumentException("Укажите числовой порт.");
        await manager.SaveOptions(port, HotkeysEnabled.IsChecked == true);
        ApplyHotkeys(); manager.Log("Настройки сохранены. HTTP API обновится после перезапуска.");
    });
    private void CopyToken(object sender, RoutedEventArgs e) => Change(() => { Clipboard.SetText(manager.Settings.ApiToken); StatusBar.Text = "Токен API скопирован."; });
    private void CopyCurl(object sender, RoutedEventArgs e) => Change(() => {
        Clipboard.SetText($"curl.exe -H \"X-Godox-Token: {manager.Settings.ApiToken}\" {api.Status}/api/devices"); StatusBar.Text = "Команда CMD скопирована.";
    });
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closed) return;
        if (((App)Application.Current).IsSessionEnding) { hotkeys?.Dispose(); DisposeTray(); return; }
        if (!exitRequested && manager.Settings.CloseToTray && tray is not null && !closing)
        { e.Cancel = true; Hide(); return; }
        e.Cancel = true; if (closing) return;
        closing = true; hotkeys?.Dispose(); DisposeTray(); IsEnabled = false; StatusBar.Text = "Закрытие соединений…";
        try { await ((App)Application.Current).StopServices(); }
        finally { closed = true; Close(); Application.Current.Shutdown(); }
    }
}
