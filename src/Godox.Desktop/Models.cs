using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Godox.Desktop;

public record DeviceProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; init; } = "Светильник";
    public string Address { get; init; } = "";
    public string Model { get; init; } = "SL60IIBi";
    public string StatePath { get; init; } = "";
    public string Hotkey { get; init; } = "";
    public int ResumeBrightness { get; init; } = 10;
    public int PreferredCct { get; init; } = 6500;
    public int? MixerLevel { get; init; }
    public bool Muted { get; init; }
    public bool AutoConnect { get; init; } = true;
}

public class Settings
{
    public int ApiPort { get; set; } = 8765;
    public bool ApiEnabled { get; set; } = true;
    public bool HotkeysEnabled { get; set; } = true;
    public bool AutoConnectEnabled { get; set; }
    public int MasterLevel { get; set; } = 100;
    public bool MasterMuted { get; set; }
    public string PythonPath { get; set; } = "test/.venv/Scripts/python.exe";
    public string ApiToken { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public List<DeviceProfile> Devices { get; set; } = [];
}

public record DeviceStatus(bool Connected = false, int? Brightness = null, int? Cct = null, string? Warning = null);
public record DeviceView(DeviceProfile Profile, DeviceStatus Status, int EffectiveBrightness = 0, bool AutoPaused = false)
{
    public string Name => Profile.Name;
    public string Details => $"{Profile.Model} · {Profile.Address}";
    public string Connection => Status.Connected ? "Подключён" : "Отключён";
    public string Values => Status.Brightness is { } b ? $"{b}%  ·  {Status.Cct?.ToString() ?? "—"} K" : "Ожидает подключения";
}
public record ScanResult(string Address, string Name, string? Model, int Rssi, bool NeedsProvisioning)
{
    public override string ToString() => $"{Model ?? Name}  ·  {Address}  ·  {Rssi} dBm";
}
public record SetRequest(int? Brightness, int? Cct);
public record MixerRequest(int? Level, bool? Muted);

public static class MixerMath
{
    public static int Effective(int master, int level, bool masterMuted = false, bool muted = false) =>
        masterMuted || muted ? 0 : (int)Math.Round(master * level / 100.0, MidpointRounding.AwayFromZero);
}

public sealed class SettingsStore(string root, string? dataDirectory = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string Root { get; } = root;
    public static string DefaultDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Godox");
    public string DataDirectory { get; } = Path.GetFullPath(dataDirectory ?? DefaultDataDirectory);
    public string BindingsDirectory => Path.Combine(DataDirectory, "bindings");
    public string FilePath => Path.Combine(DataDirectory, "settings.json");
    public string LegacyFilePath => Path.Combine(Root, "data", "settings.json");
    public string NewBindingPath(string address)
    {
        if (!Regex.IsMatch(address, "^(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$")) throw new ArgumentException("Неверный MAC-адрес.");
        return Path.Combine(BindingsDirectory, address.Replace(":", "").ToUpperInvariant() + "_mesh_state.json");
    }
    public string ResolveBinding(string address, string preferredPath)
    {
        if (File.Exists(preferredPath)) return preferredPath;
        var canonical = NewBindingPath(address);
        // Recovers an interrupted migration after File.Move but before saving settings.
        if (File.Exists(canonical)) { BindingFiles.ValidateFile(canonical, address); return canonical; }
        var resolved = BindingFiles.ResolveIn([BindingsDirectory], address, canonical);
        if (File.Exists(resolved)) return resolved;
        return BindingFiles.Resolve(Root, address, canonical);
    }
    public string ManageBinding(string address, string preferredPath)
    {
        var source = ResolveBinding(address, preferredPath);
        var target = NewBindingPath(address);
        if (string.Equals(Path.GetFullPath(source), target, StringComparison.OrdinalIgnoreCase)) return target;
        if (!File.Exists(source)) return target;
        BindingFiles.ValidateFile(source, address);
        Directory.CreateDirectory(BindingsDirectory);
        if (File.Exists(target)) throw new IOException("В AppData уже есть другая привязка этого MAC. Файлы не заменены.");
        // Keep one live sequence counter, with the same lock convention as Python.
        using (BindingMoveLock.Acquire(source)) File.Move(source, target);
        return target;
    }
    public Settings Load()
    {
        bool migrating = !File.Exists(FilePath) && File.Exists(LegacyFilePath);
        var sourceFile = migrating ? LegacyFilePath : FilePath;
        if (File.Exists(sourceFile))
        {
            var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(sourceFile), Json)
                ?? throw new InvalidDataException("Не удалось прочитать settings.json.");
            if (loaded.MasterLevel is < 0 or > 100 || loaded.Devices.Any(d => d.MixerLevel is < 0 or > 100))
                throw new InvalidDataException("Уровни микшера в settings.json должны быть от 0 до 100.");
            bool changed = migrating;
            for (int i = 0; i < loaded.Devices.Count; i++)
            {
                var device = loaded.Devices[i];
                var path = migrating ? ManageBinding(device.Address, device.StatePath) : ResolveBinding(device.Address, device.StatePath);
                if (path != device.StatePath) { loaded.Devices[i] = device with { StatePath = path }; changed = true; }
            }
            if (changed) Save(loaded);
            if (migrating)
            {
                var backup = Path.Combine(DataDirectory, "migration");
                Directory.CreateDirectory(backup);
                File.Move(LegacyFilePath, Path.Combine(backup, "settings-" + Guid.NewGuid().ToString("N") + ".json"));
            }
            return loaded;
        }
        var settings = new Settings();
        Save(settings);
        return settings;
    }
    public void Save(Settings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(settings, Json));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
    public static void Validate(DeviceProfile device)
    {
        if (string.IsNullOrWhiteSpace(device.Name)) throw new ArgumentException("Укажите название.");
        if (!Regex.IsMatch(device.Address, "^(?:[0-9A-F]{2}:){5}[0-9A-F]{2}$"))
            throw new ArgumentException("Неверный MAC-адрес. Пример: 02:00:00:00:00:01.");
        if (device.Model is not ("SL60IIBi" or "P260C Pro")) throw new ArgumentException("Выберите поддерживаемую модель.");
        if (!Path.IsPathFullyQualified(device.StatePath)) throw new ArgumentException("Укажите полный путь к файлу привязки.");
        if (device.ResumeBrightness is < 1 or > 100 || device.PreferredCct is < 2800 or > 6500)
            throw new ArgumentException("Некорректные параметры света.");
        Hotkeys.Parse(device.Hotkey);
    }
}
