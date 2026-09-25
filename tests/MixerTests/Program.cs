using System.Text.Json;
using Godox.Desktop;

var root = Path.Combine(Path.GetTempPath(), "godox-mixer-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var path = Path.Combine(root, "keys.json"); File.WriteAllText(path, "{}");
var store = new SettingsStore(root, Path.Combine(root, "data"));
var settings = new Settings { Devices = [
    new() { Id = "a", Name = "A", MixerLevel = 100, StatePath = path },
    new() { Id = "b", Name = "B", MixerLevel = 50, StatePath = path }] };
var backend = new FakeBackend(); var manager = new DeviceManager(store, settings, backend);
void Check(bool value, string description) { if (!value) throw new Exception(description); Console.WriteLine("PASS: " + description); }
try
{
    await manager.ConnectAll();
    manager.SetMixer(null, 50); await manager.FlushMixer();
    Check(backend.States["a"].Brightness == 50 && backend.States["b"].Brightness == 25, "Master scales both devices 100/50 -> 50/25");
    Check(manager.Profile("a").MixerLevel == 100 && manager.Profile("b").MixerLevel == 50, "Master preserves individual fader positions");
    manager.SetMixer(null, muted: true); await manager.FlushMixer();
    Check(backend.States.Values.All(s => s.Brightness == 0), "Master mute sends zero");
    manager.SetMixer("b", muted: true); manager.SetMixer(null, muted: false); await manager.FlushMixer();
    Check(backend.States["a"].Brightness == 50 && backend.States["b"].Brightness == 0, "Master unmute preserves individual mute");
    manager.SetMixer("b", muted: false); await manager.FlushMixer();
    Check(backend.States["b"].Brightness == 25, "Unmute restores saved proportion");
    backend.Writes.Clear();
    for (int value = 0; value <= 100; value++) manager.SetMixer("a", value);
    await manager.FlushMixer();
    Check(backend.Writes.Count == 1 && backend.Writes[0] == ("a", 50), "101 slider changes coalesce into one final BLE write");
    backend.BlockNextWrite = true;
    manager.SetMixer("a", 20); var flight = manager.FlushMixer(); await backend.Entered.Task;
    var barrier = manager.FlushMixer();
    Check(!barrier.IsCompleted, "API flush waits for a write already claimed by the timer");
    manager.SetMixer("a", 80); manager.SetMixer("a", 90); backend.Release.SetResult(); await flight;
    await barrier; await manager.FlushMixer();
    Check(backend.States["a"].Brightness == 45 && manager.Profile("a").MixerLevel == 90, "In-flight write cannot discard a newer slider value");
    await manager.Act("a", "status");
    Check(manager.Profile("a").MixerLevel == 90, "Readback does not feed scaled brightness back into mixer");
    manager.SetMixer("a", 30); await manager.Act("a", "disconnect"); backend.Writes.Clear(); await manager.FlushMixer();
    Check(backend.Writes.Count == 0, "Disconnect cancels queued output");
    manager.SetAutoConnect(null, true); manager.SetAutoConnect("b", false);
    await manager.AutoConnect();
    Check(backend.States["a"].Connected, "Global auto-connect connects eligible lamp");
    await manager.Act("a", "disconnect"); await manager.AutoConnect();
    Check(!backend.States["a"].Connected, "Manual disconnect pauses auto-connect");
    manager.SetAutoConnect("a", true); await manager.AutoConnect();
    Check(backend.States["a"].Connected, "Re-enabling device auto-connect resumes it");
    await manager.DisconnectAll(); await manager.AutoConnect();
    Check(backend.States.Values.All(s => !s.Connected), "Disconnect-all stays disconnected with auto-connect enabled");
    manager.SetAutoConnect(null, false); manager.SetAutoConnect("a", true); await manager.AutoConnect();
    Check(!backend.States["a"].Connected, "Global switch takes precedence over device switch");
    manager.SetAutoConnect(null, true); await manager.AutoConnect();
    Check(backend.States["a"].Connected && !backend.States["b"].Connected, "Per-device exclusion is respected");
    backend.States["a"] = new(false, 15, 6500);
    await manager.RefreshConnections(); await manager.AutoConnect();
    Check(backend.States["a"].Connected && backend.States["a"].Brightness == 15, "Unexpected loss reconnects and reapplies mixer without manual intervention");
    manager.Move("b", -1); await manager.FlushMixer();
    var saved = store.Load();
    Check(saved.Devices[0].Id == "b" && saved.Devices[1].MixerLevel == 30 && saved.MasterLevel == 50, "Order, master, and individual levels survive reload");
    Check(MixerMath.Effective(50, 1) == 1 && MixerMath.Effective(0, 100) == 0 && MixerMath.Effective(100, 100) == 100, "Rounding and boundaries");
    try { manager.SetMixer("a", 101); throw new Exception("Accepted invalid level"); } catch (ArgumentException) { }
    Check(manager.Profile("a").MixerLevel == 30, "Invalid level cannot alter saved settings");
    await manager.DisconnectAll();
    backend.BlockConnection();
    var connection = manager.Act("a", "connect"); await backend.ConnectEntered.Task;
    var item = new MixerItem(manager.Snapshot().First(d => d.Profile.Id == "a"));
    Check(!item.CanConnect && item.ConnectText == "Подключается…", "Manual connection disables the button before BLE completes");
    try { await manager.Act("a", "connect"); throw new Exception("Duplicate connection accepted"); }
    catch (InvalidOperationException) { }
    Check(backend.ConnectCalls == 1 && manager.Snapshot().First(d => d.Profile.Id == "a").Connecting, "Duplicate request cannot queue a second BLE connection or clear progress");
    var queued = manager.Act("b", "connect");
    Check(manager.Snapshot().First(d => d.Profile.Id == "b").Connecting, "Queued connection also disables its button while waiting for BLE");
    backend.ConnectRelease.SetResult(); var completed = await connection; await queued;
    item.Update(manager.Snapshot().First(d => d.Profile.Id == "a"));
    Check(item.CanConnect && item.ConnectText == "Отключить" && !completed.Connecting, "Successful connection restores enabled disconnect button");
    await manager.DisconnectAll(); manager.SetAutoConnect(null, true);
    backend.BlockConnection(fail: true);
    var automatic = manager.AutoConnect(); await backend.ConnectEntered.Task;
    Check(manager.Snapshot().First(d => d.Profile.Id == "a").Connecting, "Automatic connection exposes the same pending state");
    backend.ConnectRelease.SetResult(); await automatic;
    item.Update(manager.Snapshot().First(d => d.Profile.Id == "a"));
    Check(item.CanConnect && item.ConnectText == "Подключить" && item.Warning.Length > 0, "Failed connection clears progress and allows manual retry");
    await manager.Act("a", "connect");
    Check(manager.Snapshot().First(d => d.Profile.Id == "a").Status.Connected, "Manual retry succeeds after a failed automatic connection");
    manager.SetCloseToTray(true);
    Check(store.Load().CloseToTray, "Close-to-tray setting survives reload");
    manager.SetCloseToTray(false);
    Check(!store.Load().CloseToTray, "Close-to-tray can be disabled persistently");
    manager.Stop();
}
finally
{
    if (Path.GetDirectoryName(Path.GetFullPath(root)) == Path.TrimEndingDirectorySeparator(Path.GetTempPath())) Directory.Delete(root, true);
}

sealed class FakeBackend : IBackend
{
    public Dictionary<string, DeviceStatus> States { get; } = [];
    public List<(string, int)> Writes { get; } = [];
    public bool BlockNextWrite;
    public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ConnectEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ConnectRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int ConnectCalls;
    private bool blockConnection, failConnection;
    public void BlockConnection(bool fail = false)
    {
        blockConnection = true; failConnection = fail; ConnectCalls = 0;
        ConnectEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConnectRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public async Task<JsonElement> SendAsync(string command, DeviceProfile? device = null, int? brightness = null, int? cct = null)
    {
        if (command == "sessions") return JsonSerializer.SerializeToElement(States, SettingsStore.Json);
        var id = device!.Id;
        if (command == "connect")
        {
            ConnectCalls++;
            if (blockConnection)
            {
                blockConnection = false; ConnectEntered.SetResult(); await ConnectRelease.Task;
                if (failConnection) { failConnection = false; throw new IOException("Test connection failure"); }
            }
            States[id] = new(true, 0, 6500);
        }
        if (command == "disconnect") States[id] = new();
        if (command == "set_fast")
        {
            if (BlockNextWrite) { BlockNextWrite = false; Entered.SetResult(); await Release.Task; }
            Writes.Add((id, brightness!.Value)); States[id] = new(true, brightness, cct);
        }
        return JsonSerializer.SerializeToElement(States[id], SettingsStore.Json);
    }
}
