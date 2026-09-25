using System.IO;
using System.Text.Json;

namespace Godox.Desktop;

public static class BindingFiles
{
    // Reuse the actual file: copying keys would split the Mesh sequence counter.
    public static string Resolve(string root, string address, string preferredPath)
        => ResolveIn([Path.Combine(root, "data"), Path.Combine(root, "test")], address, preferredPath);

    public static string ResolveIn(IEnumerable<string> directories, string address, string preferredPath)
    {
        if (File.Exists(preferredPath)) return preferredPath;
        var matches = new List<string>();
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*_mesh_state.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    var value = doc.RootElement;
                    if (!value.TryGetProperty("device_address", out var mac) ||
                        !string.Equals(mac.GetString(), address, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!ValidKey(value, "network_key") || !ValidKey(value, "app_key") || !ValidKey(value, "device_key")) continue;
                    matches.Add(Path.GetFullPath(path));
                }
                catch (Exception exc) when (exc is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException) { }
            }
        }
        // Multiple files may contain old keys; never guess which network is current.
        return matches.Count == 1 ? matches[0] : preferredPath;
    }
    public static void ValidateFile(string path, string address)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var value = doc.RootElement;
        if (!value.TryGetProperty("device_address", out var mac) ||
            !string.Equals(mac.GetString(), address, StringComparison.OrdinalIgnoreCase) ||
            !ValidKey(value, "network_key") || !ValidKey(value, "app_key") || !ValidKey(value, "device_key"))
            throw new InvalidDataException("Файл привязки повреждён или принадлежит другому MAC-адресу.");
    }
    private static bool ValidKey(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String &&
        field.GetString() is { Length: 32 } text && text.All(Uri.IsHexDigit);
}
