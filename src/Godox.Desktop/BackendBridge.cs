using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Godox.Desktop;

public sealed class BackendException(string message, string? code) : InvalidOperationException(message)
{
    public string? Code { get; } = code;
}

public interface IBackend
{
    Task<JsonElement> SendAsync(string command, DeviceProfile? device = null, int? brightness = null, int? cct = null);
}

public sealed class BackendBridge(string root, string python) : IAsyncDisposable, IBackend
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    private int nextId;
    public event Action<string>? Diagnostic;

    private void Start()
    {
        if (process is { HasExited: false }) return;
        process?.Dispose();
        var bundled = Path.Combine(root, "runtime", "python", "python.exe");
        var exe = Directory.Exists(Path.GetDirectoryName(bundled)) ? bundled
            : Path.IsPathRooted(python) ? python : Path.Combine(root, python);
        if (!File.Exists(exe)) throw new FileNotFoundException("Python-окружение не найдено. Запустите setup.cmd.", exe);
        var info = new ProcessStartInfo(exe) { WorkingDirectory = root, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        info.ArgumentList.Add("-u");
        info.ArgumentList.Add("-B"); // Installed program files stay read-only; no __pycache__.
        info.ArgumentList.Add(Path.Combine(root, "backend", "worker.py"));
        process = new Process { StartInfo = info };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Diagnostic?.Invoke(e.Data); };
        if (!process.Start()) throw new IOException("Не удалось запустить Bluetooth-модуль.");
        process.BeginErrorReadLine();
    }

    public async Task<JsonElement> SendAsync(string command, DeviceProfile? device = null, int? brightness = null, int? cct = null)
    {
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Очередь занята. Повторите позже.");
        try
        {
            Start();
            var id = ++nextId;
            var line = JsonSerializer.Serialize(new { id, command, device, brightness, cct }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await process!.StandardInput.WriteLineAsync(line);
            await process.StandardInput.FlushAsync();
            string? answer;
            try { answer = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(105)); }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("Bluetooth-модуль не ответил. Соединения закрыты; подключитесь заново.");
            }
            if (answer is null) throw new IOException("Bluetooth-модуль завершился. Повторите подключение.");
            using var doc = JsonDocument.Parse(answer);
            var value = doc.RootElement;
            if (value.GetProperty("id").GetInt32() != id) throw new IOException("Нарушен порядок ответов Bluetooth-модуля.");
            if (!value.GetProperty("ok").GetBoolean()) throw new BackendException(value.GetProperty("error").GetString() ?? "Ошибка Bluetooth",
                value.TryGetProperty("errorCode", out var errorCode) ? errorCode.GetString() : null);
            return value.GetProperty("result").Clone();
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (process is { HasExited: false })
        {
            try { await SendAsync("shutdown").WaitAsync(TimeSpan.FromSeconds(12)); } catch { }
            try
            {
                process.StandardInput.Close();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
        process?.Dispose();
    }
}
