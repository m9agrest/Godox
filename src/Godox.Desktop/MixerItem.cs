using System.ComponentModel;

namespace Godox.Desktop;

// Stable objects keep the actual Slider controls (and mouse capture) alive on updates.
public sealed class MixerItem(DeviceView view) : INotifyPropertyChanged
{
    public DeviceView View { get; private set; } = view;
    public string Id => View.Profile.Id;
    public string Name => View.Name;
    public string Address => View.Profile.Address;
    public string Connection => View.Connecting ? "◌ Подключается…" : View.Status.Connected ? "● Подключён" : "○ Отключён";
    public string ConnectText => View.Connecting ? "Подключается…" : View.Status.Connected ? "Отключить" : "Подключить";
    public bool CanConnect => !View.Connecting;
    public int Level => View.Profile.MixerLevel ?? View.Profile.ResumeBrightness;
    public int Cct => View.Profile.PreferredCct;
    public string CctText => $"{Cct} K";
    public string Levels => $"{Level}% ({View.EffectiveBrightness}%)";
    public string MuteText => View.Profile.Muted ? "Включить свет" : "Погасить";
    public bool AutoConnect => View.Profile.AutoConnect;
    public string AutoStatus => View.AutoPaused ? "Авто: пауза после отключения" : "";
    public string Warning => View.Status.Warning ?? "";
    public string Output => !View.Status.Connected ? "Применится при подключении" : View.Status.Brightness is { } b ? $"Отправлено: {b}%" : "Состояние неизвестно";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(DeviceView value)
    {
        if (View == value) return;
        View = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }
}
