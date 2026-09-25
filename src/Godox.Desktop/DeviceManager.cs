using System.IO;
using System.Text.Json;

namespace Godox.Desktop;

public sealed class DeviceManager(SettingsStore store, Settings settings, IBackend bridge)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private readonly Dictionary<string, DeviceStatus> states = [];
    private readonly HashSet<string> pending = [], paused = [], connecting = [];
    private readonly Dictionary<string, DateTime> retryAfter = [];
    private bool settingsDirty, stopping;
    public event Action? Changed;
    public event Action<string>? Logged;
    public Settings Settings => settings;
    private int Effective(DeviceProfile p) => MixerMath.Effective(settings.MasterLevel,
        p.MixerLevel ?? p.ResumeBrightness, settings.MasterMuted, p.Muted);
    public IReadOnlyList<DeviceView> Snapshot()
    {
        lock (sync) return settings.Devices.Select(d => new DeviceView(d,
            states.GetValueOrDefault(d.Id, new()), Effective(d), paused.Contains(d.Id), connecting.Contains(d.Id))).ToArray();
    }
    public DeviceProfile Profile(string id) => Snapshot().FirstOrDefault(d => d.Profile.Id == id)?.Profile
        ?? throw new KeyNotFoundException("Устройство не найдено.");
    public void Log(string message) => Logged?.Invoke($"{DateTime.Now:HH:mm:ss}  {message}");
    private void Queue(string id)
    {
        if (!stopping && states.GetValueOrDefault(id)?.Connected == true) pending.Add(id);
        settingsDirty = true;
    }
    public void SetMixer(string? id, int? level = null, bool? muted = null, int? cct = null)
    {
        if (level is < 0 or > 100 || cct is not null && (cct < 2800 || cct > 6500 || cct % 100 != 0))
            throw new ArgumentException("Уровень: 0–100%; температура: 2800–6500 K с шагом 100 K.");
        lock (sync)
        {
            if (id is null)
            {
                if (level.HasValue) settings.MasterLevel = level.Value;
                if (muted.HasValue) settings.MasterMuted = muted.Value;
                foreach (var p in settings.Devices) Queue(p.Id);
                settingsDirty = true;
            }
            else
            {
                var p = Profile(id);
                settings.Devices[settings.Devices.FindIndex(d => d.Id == id)] = p with {
                    MixerLevel = level ?? p.MixerLevel ?? p.ResumeBrightness,
                    Muted = muted ?? p.Muted, PreferredCct = cct ?? p.PreferredCct };
                Queue(id);
            }
        }
        Changed?.Invoke();
    }
    public void SetAutoConnect(string? id, bool enabled)
    {
        lock (sync)
        {
            if (id is null) { settings.AutoConnectEnabled = enabled; if (enabled) { paused.Clear(); retryAfter.Clear(); } }
            else
            {
                var p = Profile(id);
                settings.Devices[settings.Devices.FindIndex(d => d.Id == id)] = p with { AutoConnect = enabled };
                if (enabled) { paused.Remove(id); retryAfter.Remove(id); }
            }
            store.Save(settings);
        }
        Changed?.Invoke();
    }
    public void Move(string id, int delta)
    {
        lock (sync)
        {
            var index = settings.Devices.FindIndex(d => d.Id == id);
            if (index < 0) throw new KeyNotFoundException("Устройство не найдено.");
            var next = Math.Clamp(index + delta, 0, settings.Devices.Count - 1);
            var item = settings.Devices[index]; settings.Devices.RemoveAt(index); settings.Devices.Insert(next, item);
            store.Save(settings);
        }
        Changed?.Invoke();
    }
    public async Task Pump(CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(75));
        while (await timer.WaitForNextTickAsync(cancellation))
        {
            try { await FlushMixer(); }
            catch (Exception exc) { Log(exc.Message); }
        }
    }
    // One pending value per device; fetch it after acquiring the BLE gate so
    // dragging during another operation cannot replay old slider positions.
    public async Task FlushMixer()
    {
        // Wait for a write already claimed by the timer, so API completion cannot
        // report an old cached value while that write is still in flight.
        await gate.WaitAsync();
        try
        {
            string[] ids;
            lock (sync) { ids = pending.ToArray(); if (settingsDirty) { store.Save(settings); settingsDirty = false; } }
            Exception? failure = null;
            foreach (var id in ids)
                try { await SendMixer(id); } catch (Exception exc) { failure ??= exc; }
            if (failure is not null) throw failure;
        }
        finally { gate.Release(); }
    }
    private async Task SendMixer(string id)
    {
        DeviceProfile p; int output;
        lock (sync)
        {
            if (!pending.Remove(id) || stopping || states.GetValueOrDefault(id)?.Connected != true) return;
            p = Profile(id); output = Effective(p);
        }
        try
        {
            var result = await bridge.SendAsync("set_fast", p, output, p.PreferredCct);
            lock (sync) states[id] = result.Deserialize<DeviceStatus>(SettingsStore.Json) ?? new();
        }
        catch (Exception exc)
        {
            lock (sync) states[id] = states.GetValueOrDefault(id, new()) with { Warning = "Не отправлено: " + exc.Message };
            Log($"{p.Name}: {exc.Message}"); throw;
        }
        finally { Changed?.Invoke(); }
    }
    private bool CanAutoConnect(DeviceProfile p, bool ownAttempt = false) => !stopping && settings.AutoConnectEnabled && p.AutoConnect &&
        (ownAttempt || !connecting.Contains(p.Id)) &&
        !paused.Contains(p.Id) && states.GetValueOrDefault(p.Id)?.Connected != true && File.Exists(p.StatePath) &&
        retryAfter.GetValueOrDefault(p.Id) <= DateTime.UtcNow;
    public async Task AutoConnect()
    {
        foreach (var d in Snapshot())
        {
            lock (sync)
            {
                var current = settings.Devices.FirstOrDefault(p => p.Id == d.Profile.Id);
                if (current is null || !CanAutoConnect(current)) continue;
            }
            try { await ActCore(d.Profile.Id, "connect", automatic: true); }
            catch (Exception exc) { Log($"Автоподключение {d.Name}: {exc.Message}. Повтор через 30 секунд."); }
        }
    }
    public Task<DeviceView> Act(string id, string command, int? brightness = null, int? cct = null) => ActCore(id, command, brightness, cct);
    private async Task<DeviceView> ActCore(string id, string command, int? brightness = null, int? cct = null, bool automatic = false)
    {
        bool connectionAttempt = command is "connect" or "provision" or "rebind";
        if (connectionAttempt)
        {
            lock (sync)
            {
                var profile = Profile(id);
                if (automatic && !CanAutoConnect(profile)) return Snapshot().First(d => d.Profile.Id == id);
                if (command == "connect" && states.GetValueOrDefault(id)?.Connected == true)
                    return Snapshot().First(d => d.Profile.Id == id);
                if (!connecting.Add(id)) throw new InvalidOperationException("Подключение этого светильника уже выполняется.");
            }
            Changed?.Invoke();
        }
        if (command == "disconnect") lock (sync) { paused.Add(id); pending.Remove(id); }
        await gate.WaitAsync();
        try
        {
            var profile = Profile(id);
            if (stopping) throw new InvalidOperationException("Приложение закрывается.");
            if (command == "connect") lock (sync)
            {
                if (automatic && !CanAutoConnect(profile, ownAttempt: true)) return Snapshot().First(d => d.Profile.Id == id);
                if (!automatic) paused.Remove(id);
                retryAfter[id] = DateTime.UtcNow.AddSeconds(30);
            }
            if (command is "set" or "toggle" or "off")
            {
                if (!Snapshot().First(d => d.Profile.Id == id).Status.Connected) throw new InvalidOperationException("Устройство отключено. Сначала подключите его.");
                if (command == "set")
                {
                    if (brightness is null || cct is null) throw new ArgumentException("Нужны brightness и cct.");
                    SetMixer(id, brightness, false, cct);
                }
                else SetMixer(id, muted: command == "off" || !profile.Muted);
                await SendMixer(id);
                return Snapshot().First(d => d.Profile.Id == id);
            }
            var result = await bridge.SendAsync(command, profile, brightness, cct);
            var status = result.Deserialize<DeviceStatus>(SettingsStore.Json) ?? new();
            lock (sync)
            {
                states[id] = status;
                var latest = Profile(id);
                if (command == "connect")
                {
                    retryAfter.Remove(id);
                    // Adopt an old profile's first real status once. Never replace
                    // saved proportions with scaled output or subsequent readback.
                    if (latest.MixerLevel is null && status.Warning is null && status.Brightness is >= 0 and <= 100)
                        settings.Devices[settings.Devices.FindIndex(d => d.Id == id)] = latest with {
                            MixerLevel = status.Brightness.Value, PreferredCct = status.Cct is >= 2800 and <= 6500 ? status.Cct.Value : latest.PreferredCct };
                    if (Profile(id).MixerLevel is not null) Queue(id);
                }
                store.Save(settings);
            }
            if (command == "connect") await SendMixer(id);
            Changed?.Invoke();
            Log($"{profile.Name}: {command}{(status.Warning is null ? "" : " — " + status.Warning)}");
            var completed = Snapshot().First(d => d.Profile.Id == id);
            return connectionAttempt ? completed with { Connecting = false } : completed;
        }
        catch (Exception exc)
        {
            lock (sync)
            {
                if (command == "connect") retryAfter[id] = DateTime.UtcNow.AddSeconds(30);
                states[id] = states.GetValueOrDefault(id, new()) with { Warning = exc.Message };
            }
            Changed?.Invoke(); Log(exc.Message); throw;
        }
        finally
        {
            if (connectionAttempt) lock (sync) connecting.Remove(id);
            gate.Release();
            if (connectionAttempt) Changed?.Invoke();
        }
    }
    public async Task ConnectAll()
    {
        foreach (var d in Snapshot().Where(d => !d.Status.Connected))
            try { await Act(d.Profile.Id, "connect"); } catch (Exception exc) { Log($"{d.Name}: {exc.Message}"); }
    }
    public async Task DisconnectAll()
    {
        lock (sync) { foreach (var p in settings.Devices) paused.Add(p.Id); pending.Clear(); }
        foreach (var d in Snapshot())
            try { await Act(d.Profile.Id, "disconnect"); } catch (Exception exc) { Log($"{d.Name}: {exc.Message}"); }
    }
    public async Task AllOff() { SetMixer(null, muted: true); await FlushMixer(); }
    public async Task<IReadOnlyList<ScanResult>> Scan()
    {
        await gate.WaitAsync();
        try { return (await bridge.SendAsync("scan")).Deserialize<List<ScanResult>>(SettingsStore.Json) ?? []; }
        finally { gate.Release(); }
    }
    public async Task RefreshConnections()
    {
        if (!await gate.WaitAsync(0)) return;
        try
        {
            var actual = (await bridge.SendAsync("sessions")).Deserialize<Dictionary<string, DeviceStatus>>(SettingsStore.Json) ?? [];
            bool changed = false;
            lock (sync) foreach (var p in settings.Devices)
            {
                var previous = states.GetValueOrDefault(p.Id, new());
                var current = actual.GetValueOrDefault(p.Id, previous with { Connected = false });
                if (current != previous) { states[p.Id] = current; changed = true; }
            }
            if (changed) Changed?.Invoke();
        }
        catch (Exception exc)
        {
            lock (sync) foreach (var id in states.Keys.ToArray()) states[id] = states[id] with { Connected = false, Warning = exc.Message };
            Changed?.Invoke();
        }
        finally { gate.Release(); }
    }
    public async Task SaveProfile(DeviceProfile profile)
    {
        profile = profile with { StatePath = store.ResolveBinding(profile.Address, profile.StatePath) };
        SettingsStore.Validate(profile);
        await gate.WaitAsync();
        try
        {
            lock (sync)
            {
                if (states.GetValueOrDefault(profile.Id)?.Connected == true) throw new InvalidOperationException("Отключите прибор перед изменением профиля.");
                if (settings.Devices.Any(d => d.Id != profile.Id && (d.Address == profile.Address || string.Equals(d.StatePath, profile.StatePath, StringComparison.OrdinalIgnoreCase))))
                    throw new ArgumentException("Этот MAC-адрес или файл привязки уже добавлен.");
                if (Hotkeys.Parse(profile.Hotkey) == Hotkeys.Parse("Ctrl+Alt+0") || settings.Devices.Any(d => d.Id != profile.Id && profile.Hotkey.Length > 0 && Hotkeys.Parse(d.Hotkey) == Hotkeys.Parse(profile.Hotkey)))
                    throw new ArgumentException("Эта горячая клавиша уже используется.");
                var index = settings.Devices.FindIndex(d => d.Id == profile.Id);
                profile = profile with { StatePath = store.ManageBinding(profile.Address, profile.StatePath) };
                if (index < 0) settings.Devices.Add(profile); else settings.Devices[index] = profile;
                states[profile.Id] = new(); store.Save(settings);
            }
            Changed?.Invoke();
        }
        finally { gate.Release(); }
    }
    public async Task Remove(string id)
    {
        await Act(id, "disconnect");
        await gate.WaitAsync();
        try { lock (sync) { settings.Devices.RemoveAll(d => d.Id == id); states.Remove(id); pending.Remove(id); store.Save(settings); } }
        finally { gate.Release(); }
        Changed?.Invoke();
    }
    public async Task SaveOptions(int port, bool api, bool hotkeys)
    {
        if (port is < 1024 or > 65535) throw new ArgumentException("Порт: 1024–65535.");
        await gate.WaitAsync();
        try { lock (sync) { settings.ApiPort = port; settings.ApiEnabled = api; settings.HotkeysEnabled = hotkeys; store.Save(settings); } }
        finally { gate.Release(); }
    }
    public void SetCloseToTray(bool enabled)
    {
        lock (sync)
        {
            bool previous = settings.CloseToTray;
            settings.CloseToTray = enabled;
            try { store.Save(settings); }
            catch { settings.CloseToTray = previous; throw; }
        }
    }
    public void Stop()
    {
        lock (sync) { stopping = true; pending.Clear(); store.Save(settings); settingsDirty = false; }
    }
}
