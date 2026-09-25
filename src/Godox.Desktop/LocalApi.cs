using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Godox.Desktop;

public sealed class LocalApi(DeviceManager manager) : IAsyncDisposable
{
    private WebApplication? app;
    public string Status { get; private set; } = "HTTP API выключен";
    public async Task Start()
    {
        if (!manager.Settings.ApiEnabled) return;
        var port = manager.Settings.ApiPort;
        var token = Encoding.UTF8.GetBytes(manager.Settings.ApiToken);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(LocalApi).Assembly.FullName });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => {
            options.Listen(IPAddress.Loopback, port);
            options.Limits.MaxRequestBodySize = 4096;
        });
        var server = builder.Build();
        server.Use(async (context, next) => {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Host.Host != "127.0.0.1" || context.Request.Host.Port != port)
            { context.Response.StatusCode = 400; return; }
            if (context.Request.Path != "/health")
            {
                var supplied = Encoding.UTF8.GetBytes(context.Request.Headers["X-Godox-Token"].ToString());
                if (!CryptographicOperations.FixedTimeEquals(token, supplied))
                { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { error = "Нужен заголовок X-Godox-Token." }); return; }
            }
            try { await next(context); }
            catch (Exception exc)
            {
                context.Response.StatusCode = exc switch {
                    KeyNotFoundException => 404, ArgumentException => 400,
                    BadHttpRequestException => 400, TimeoutException => 504,
                    InvalidOperationException => 409, _ => 503 };
                await context.Response.WriteAsJsonAsync(new { error = exc.Message, code = (exc as BackendException)?.Code });
            }
        });
        server.MapGet("/health", () => Results.Ok(new { status = "ok", app = "Godox Desktop" }));
        server.MapGet("/api/devices", () => manager.Snapshot().Select(View));
        server.MapGet("/api/mixer", () => new { level = manager.Settings.MasterLevel, muted = manager.Settings.MasterMuted });
        server.MapPost("/api/mixer", async (MixerRequest body) => {
            manager.SetMixer(null, body.Level, body.Muted); await manager.FlushMixer();
            return new { level = manager.Settings.MasterLevel, muted = manager.Settings.MasterMuted };
        });
        server.MapPost("/api/devices/{id}/mixer", async (string id, MixerRequest body) => {
            manager.SetMixer(id, body.Level, body.Muted); await manager.FlushMixer();
            return View(manager.Snapshot().First(d => d.Profile.Id == id));
        });
        server.MapPost("/api/connect-all", async () => { await manager.ConnectAll(); return manager.Snapshot().Select(View); });
        server.MapPost("/api/disconnect-all", async () => { await manager.DisconnectAll(); return manager.Snapshot().Select(View); });
        server.MapPost("/api/scan", async () => await manager.Scan());
        foreach (var command in new[] { "connect", "disconnect", "status", "toggle", "off" })
        {
            var action = command;
            server.MapPost($"/api/devices/{{id}}/{action}", async (string id) => View(await manager.Act(id, action)));
        }
        server.MapPost("/api/devices/{id}/set", async (string id, SetRequest body) =>
            View(await manager.Act(id, "set", body.Brightness, body.Cct)));
        // Device profile changes and provisioning are deliberately explicit operations in the local UI.
        try { await server.StartAsync(); app = server; Status = $"http://127.0.0.1:{port}"; }
        catch { await server.DisposeAsync(); throw; }
    }
    private static object View(DeviceView d) => new { id = d.Profile.Id, name = d.Name,
        address = d.Profile.Address, model = d.Profile.Model, connected = d.Status.Connected,
        brightness = d.Status.Brightness, cct = d.Status.Cct, warning = d.Status.Warning,
        level = d.Profile.MixerLevel ?? d.Profile.ResumeBrightness, effectiveBrightness = d.EffectiveBrightness,
        muted = d.Profile.Muted, autoConnect = d.Profile.AutoConnect, autoPaused = d.AutoPaused };
    public async ValueTask DisposeAsync()
    {
        if (app is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await app.StopAsync(timeout.Token); } finally { await app.DisposeAsync(); app = null; }
        }
        Status = "HTTP API выключен";
    }
}
