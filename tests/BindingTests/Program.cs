using System.Text.Json;
using Godox.Desktop;

var root = Path.Combine(Path.GetTempPath(), "godox-binding-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "test"));
Directory.CreateDirectory(Path.Combine(root, "data"));
var address = "02:00:00:00:00:01";
var old = Path.Combine(root, "test", "sl60_mesh_state.json");
var missing = Path.Combine(root, "data", "new_mesh_state.json");
var payload = JsonSerializer.Serialize(new { device_address = address, network_key = new string('0', 32),
    app_key = new string('1', 32), device_key = new string('2', 32) });
void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }
try
{
    Check(BindingFiles.Resolve(root, address, missing) == missing, "Unknown light keeps its planned path; no fake keys created");
    File.WriteAllText(old, payload);
    Check(BindingFiles.Resolve(root, address.ToLowerInvariant(), missing) == old, "Re-added MAC reuses the existing file without copying");
    Check(BindingFiles.Resolve(root, "02:00:00:00:00:02", missing) == missing, "Another MAC cannot inherit these keys");
    File.WriteAllText(Path.Combine(root, "data", "broken_mesh_state.json"), "not json");
    Check(BindingFiles.Resolve(root, address, missing) == old, "Malformed unrelated file does not prevent recovery");
    var store = new SettingsStore(root, Path.Combine(root, "data"));
    var profile = new DeviceProfile { Id = "readded", Name = "My light", Address = address, StatePath = missing };
    store.Save(new Settings { Devices = [profile] });
    var loaded = store.Load();
    Check(loaded.Devices.Single().StatePath == old && loaded.Devices.Single().Id == "readded", "Load repairs a broken profile without restoring removed devices");
    Check(store.Load().Devices.Single().StatePath == old, "Repaired path survives another restart");
    var duplicate = Path.Combine(root, "data", "duplicate_mesh_state.json");
    File.WriteAllText(duplicate, payload);
    Check(BindingFiles.Resolve(root, address, missing) == missing, "Multiple candidate key files are not guessed");
    Check(BindingFiles.Resolve(root, address, old) == old, "Explicit existing selection stays selected");
}
finally
{
    if (Path.GetDirectoryName(Path.GetFullPath(root)) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
        Directory.Delete(root, recursive: true);
}
