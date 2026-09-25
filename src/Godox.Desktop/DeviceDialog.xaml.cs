using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Godox.Desktop;

public partial class DeviceDialog : Window
{
    private readonly DeviceProfile original;
    private readonly SettingsStore store;
    public DeviceProfile? Result { get; private set; }
    public DeviceDialog(SettingsStore store, DeviceProfile? profile, IReadOnlyList<ScanResult>? found)
    {
        this.store = store;
        original = profile ?? new DeviceProfile();
        InitializeComponent();
        NameBox.Text = original.Name; MacBox.Text = original.Address; HotkeyBox.Text = original.Hotkey;
        StateBox.Text = string.IsNullOrWhiteSpace(original.StatePath) ? Path.Combine(store.BindingsDirectory, original.Id + "_mesh_state.json") : original.StatePath;
        ModelBox.SelectedIndex = original.Model == "P260C Pro" ? 1 : 0;
        Found.ItemsSource = found;
        if (found is { Count: > 0 }) Found.SelectedIndex = 0;
        else { Found.Visibility = Visibility.Collapsed; FoundLabel.Visibility = Visibility.Collapsed; }
    }
    private void FoundChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Found.SelectedItem is not ScanResult item) return;
        MacBox.Text = item.Address; NameBox.Text = item.Model ?? item.Name;
        ModelBox.SelectedIndex = item.Model == "P260C Pro" ? 1 : 0;
    }
    private void Browse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Файл привязки (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) StateBox.Text = dialog.FileName;
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            Result = original with { Name = NameBox.Text.Trim(), Address = MacBox.Text.Trim().ToUpperInvariant(),
                Model = ((ComboBoxItem)ModelBox.SelectedItem).Content.ToString()!,
                Hotkey = HotkeyBox.Text.Trim(), StatePath = Path.GetFullPath(StateBox.Text.Trim()) };
            Result = Result with { StatePath = store.ResolveBinding(Result.Address, Result.StatePath) };
            SettingsStore.Validate(Result);
            DialogResult = true;
        }
        catch (Exception exc) { ErrorText.Text = exc.Message; }
    }
}
