using System.Text.Json;
using Godox.Desktop;

var area = Path.Combine(Path.GetTempPath(), "godox-storage-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(area);
const string address = "02:00:00:00:00:01";
string Payload(string mac = address) => JsonSerializer.Serialize(new { device_address = mac, network_key = new string('0', 32), app_key = new string('1', 32), device_key = new string('2', 32), sequence_number = 789 });
void Check(bool condition, string text) { if (!condition) throw new Exception(text); Console.WriteLine("PASS: " + text); }
(SettingsStore Store, string Source, Settings Settings) Fixture(string name)
{
    var root = Path.Combine(area, name, "program"); Directory.CreateDirectory(Path.Combine(root, "test")); Directory.CreateDirectory(Path.Combine(root, "data"));
    var source = Path.Combine(root, "test", "sl60_mesh_state.json"); File.WriteAllText(source, Payload());
    var settings = new Settings { MasterLevel = 63, MasterMuted = true, AutoConnectEnabled = true, ApiPort = 9876,
        Devices = [new() { Id = "custom-id", Name = "My lamp", Address = address, StatePath = source, MixerLevel = 74, Muted = true, Hotkey = "Ctrl+Shift+1" }] };
    File.WriteAllText(Path.Combine(root, "data", "settings.json"), JsonSerializer.Serialize(settings, SettingsStore.Json));
    return (new SettingsStore(root, Path.Combine(area, name, "local", "Godox")), source, settings);
}
try
{
    Check(new SettingsStore(area).DataDirectory == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Godox"), "Default storage is LocalAppData/Godox");
    var fresh = new SettingsStore(Path.Combine(area, "fresh-program"), Path.Combine(area, "fresh-data"));
    Check(fresh.Load().Devices.Count == 0, "Fresh installation starts empty, without the developer's devices");
    var (store, source, old) = Fixture("normal"); var bytes = File.ReadAllBytes(source);
    var loaded = store.Load(); var target = loaded.Devices[0].StatePath;
    Check(target == store.NewBindingPath(address) && File.Exists(target) && !File.Exists(source), "Binding is moved to AppData without a second active copy");
    Check(File.ReadAllBytes(target).SequenceEqual(bytes), "Keys and sequence counter are byte-identical");
    Check(loaded.ApiToken == old.ApiToken && loaded.MasterLevel == 63 && loaded.MasterMuted && loaded.AutoConnectEnabled && loaded.ApiPort == 9876 && loaded.Devices[0].Id == "custom-id" && loaded.Devices[0].MixerLevel == 74 && loaded.Devices[0].Hotkey == "Ctrl+Shift+1", "Migration preserves token, device identity, levels, shortcuts and options");
    Check(!File.Exists(store.LegacyFilePath) && Directory.GetFiles(Path.Combine(store.DataDirectory, "migration")).Length == 1, "Old settings are archived under AppData after successful save");
    Check(store.Load().Devices[0].StatePath == target, "Second launch does not remigrate or change keys");
    var movedRoot = new SettingsStore(Path.Combine(area, "different-program-folder"), store.DataDirectory);
    Check(movedRoot.Load().Devices[0].StatePath == target, "Changing program directory keeps user data");
    File.WriteAllText(store.LegacyFilePath, JsonSerializer.Serialize(new Settings(), SettingsStore.Json));
    Check(store.Load().Devices[0].Id == "custom-id", "An old project settings file cannot replace AppData settings");
    var partial = Fixture("partial");
    Directory.CreateDirectory(partial.Store.BindingsDirectory);
    File.Move(partial.Source, partial.Store.NewBindingPath(address));
    Check(partial.Store.Load().Devices[0].StatePath == partial.Store.NewBindingPath(address), "Restart recovers migration interrupted between move and settings save");
    var busy = Fixture("busy"); var busyLock = Path.Combine(Path.GetDirectoryName(busy.Source)!, "sl60.lock");
    File.WriteAllText(busyLock, Environment.ProcessId.ToString());
    try { busy.Store.Load(); throw new Exception("Moved a live binding"); } catch (IOException) { }
    Check(File.Exists(busy.Source) && !File.Exists(busy.Store.FilePath), "Active BLE lock prevents moving a live sequence counter");
    File.Delete(busyLock); busy.Store.Load();
    Check(!File.Exists(busy.Source), "Migration succeeds once the controller releases its lock");
    var conflict = Fixture("conflict"); Directory.CreateDirectory(conflict.Store.BindingsDirectory);
    var occupied = conflict.Store.NewBindingPath(address); File.WriteAllText(occupied, Payload());
    try { conflict.Store.Load(); throw new Exception("Replaced existing binding"); } catch (IOException) { }
    Check(File.Exists(conflict.Source) && File.ReadAllText(occupied) == Payload(), "Existing destination is never overwritten during import");
    var wrong = Fixture("wrong-mac"); File.WriteAllText(wrong.Source, Payload("02:00:00:00:00:02"));
    try { wrong.Store.Load(); throw new Exception("Moved another light's keys"); } catch (InvalidDataException) { }
    Check(File.Exists(wrong.Source), "MAC mismatch is rejected without moving the source");
    var missing = Fixture("missing"); File.Delete(missing.Source);
    var noKey = missing.Store.Load();
    Check(noKey.Devices[0].StatePath == missing.Store.NewBindingPath(address) && !File.Exists(noKey.Devices[0].StatePath), "Missing keys receive an AppData path without inventing a binding");
    var isolated = new SettingsStore(store.Root, Path.Combine(area, "isolated-data"));
    isolated.Save(new Settings { Devices = [] });
    Check(isolated.Load().Devices.Count == 0 && store.Load().Devices.Count == 1, "Explicit data directory keeps test settings separate from real data");
}
finally
{
    if (Path.GetDirectoryName(Path.GetFullPath(area)) == Path.TrimEndingDirectorySeparator(Path.GetTempPath())) Directory.Delete(area, true);
}
