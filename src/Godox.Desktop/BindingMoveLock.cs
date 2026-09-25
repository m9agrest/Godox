using System.Diagnostics;
using System.IO;

namespace Godox.Desktop;

// Matches worker.StateLock and the original Python controller, including stale PIDs.
internal sealed class BindingMoveLock(string path, FileStream stream) : IDisposable
{
    public static BindingMoveLock Acquire(string statePath)
    {
        var stem = Path.GetFileNameWithoutExtension(statePath);
        if (stem.EndsWith("_mesh_state", StringComparison.Ordinal)) stem = stem[..^11];
        var path = Path.Combine(Path.GetDirectoryName(statePath)!, stem + ".lock");
        if (File.Exists(path))
        {
            if (!int.TryParse(File.ReadAllText(path).Trim(), out var pid)) throw new IOException("Закройте контроллер перед переносом привязки: найден файл блокировки.");
            bool alive;
            try { using var process = Process.GetProcessById(pid); alive = !process.HasExited; }
            catch (ArgumentException) { alive = false; }
            if (alive) throw new IOException("Привязка используется. Закройте Godox Desktop и тестовый контроллер перед переносом.");
            File.Delete(path);
        }
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(Environment.ProcessId.ToString());
            stream.Write(bytes); stream.Flush(true);
            return new BindingMoveLock(path, stream);
        }
        catch { stream.Dispose(); File.Delete(path); throw; }
    }
    public void Dispose() { stream.Dispose(); File.Delete(path); }
}
