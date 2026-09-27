using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EchoBridge.Audio;
using EchoBridge.Models;
using EchoBridge.Services;

namespace EchoBridge;

public partial class MainWindow : Window
{
    private readonly AudioDeviceService _deviceService = new();
    private readonly AudioRepeaterService _repeater = new();
    private readonly SettingsService _settingsService = new();
    private readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private AppSettings _settings;
    private bool _closingAfterCleanup;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsService.Load();
        if (_settings.WindowLeft is double left && _settings.WindowTop is double top &&
            SystemParameters.VirtualScreenLeft <= left && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            SystemParameters.VirtualScreenTop <= top && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        for (var i = 0; i < BufferChoices.Items.Count; i++)
            if ((BufferChoices.Items[i] as ComboBoxItem)?.Tag?.ToString() == _settings.BufferMilliseconds.ToString())
                BufferChoices.SelectedIndex = i;
        GainSlider.Value = _settings.GainPercent;
        _repeater.StatusChanged += OnStatusChanged;
        _meterTimer.Tick += (_, _) => LevelBar.Value = Math.Clamp(_repeater.Level * 100, 0, 100);
        _meterTimer.Start();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async Task RefreshDevicesAsync()
    {
        if (_busy || _repeater.IsRunning) return;
        _busy = true;
        RefreshButton.IsEnabled = false;
        try
        {
            var inputId = InputDevices.SelectedValue as string ?? _settings.InputEndpointId;
            var outputId = OutputDevices.SelectedValue as string ?? _settings.OutputEndpointId;
            var devices = await Task.Run(_deviceService.GetRenderDevices);
            InputDevices.ItemsSource = devices;
            OutputDevices.ItemsSource = devices;
            InputDevices.SelectedItem = devices.FirstOrDefault(d => d.Id == inputId);
            OutputDevices.SelectedItem = devices.FirstOrDefault(d => d.Id == outputId);
            if (devices.Count == 0) SetStatus("No active playback devices found");
            else if (StatusLabel.Text == "No active playback devices found") SetStatus("Ready");
        }
        catch (Exception ex)
        {
            LoggingService.Write("Device refresh failed", ex);
            SetStatus("Failed to list audio devices");
        }
        finally { _busy = false; RefreshButton.IsEnabled = true; }
    }

    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        StartStopButton.IsEnabled = false;
        try
        {
            if (_repeater.IsRunning)
            {
                await _repeater.StopAsync();
                LevelBar.Value = 0;
            }
            else
            {
                if (InputDevices.SelectedItem is not AudioDeviceInfo input || OutputDevices.SelectedItem is not AudioDeviceInfo output)
                    throw new ArgumentException("Select both devices.");
                if (input.Id == output.Id) throw new ArgumentException("Input and Output must be different devices.");
                _settings.InputEndpointId = input.Id;
                _settings.OutputEndpointId = output.Id;
                _settings.BufferMilliseconds = SelectedBuffer();
                _settings.GainPercent = (int)GainSlider.Value;
                _settingsService.Save(_settings);
                await _repeater.StartAsync(input.Id, output.Id, _settings.BufferMilliseconds, _settings.GainPercent);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Write("Start/Stop action failed", ex);
            SetStatus(ex switch
            {
                ArgumentException => ex.Message,
                AudioStartException => ex.Message,
                NAudio.CoreAudioApi.CoreAudioException => "Failed to open audio device or device disconnected",
                NotSupportedException => "Unsupported audio format",
                _ => "Failed to open input or output device"
            });
        }
        finally
        {
            _busy = false;
            UpdateControls();
        }
    }

    private int SelectedBuffer() => int.TryParse((BufferChoices.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var value) ? value : 50;

    private void Settings_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settings is null || _repeater.IsRunning) return;
        _settings.BufferMilliseconds = SelectedBuffer();
        _settingsService.Save(_settings);
    }

    private void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GainLabel is null || _settings is null) return;
        var gain = (int)Math.Round(e.NewValue);
        GainLabel.Text = $"{gain} %";
        _repeater.SetGainPercent(gain);
        _settings.GainPercent = gain;
        _settingsService.Save(_settings);
    }

    private void OnStatusChanged(string status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_closingAfterCleanup) return;
            if (status == "Running" && !_repeater.IsRunning) return;
            if (status != "Running" && _repeater.IsRunning) return;
            SetStatus(status);
            UpdateControls();
        });
    }

    private void SetStatus(string status)
    {
        StatusLabel.Text = status;
        var color = status switch
        {
            "Ready" => Color.FromRgb(85, 214, 194),
            "Running" => Color.FromRgb(117, 217, 119),
            _ when status.Contains("disconnected", StringComparison.OrdinalIgnoreCase) => Color.FromRgb(232, 196, 90),
            _ => Color.FromRgb(226, 106, 106)
        };
        StatusLabel.Foreground = new SolidColorBrush(color);
    }

    private void UpdateControls()
    {
        var running = _repeater.IsRunning;
        StartStopIcon.Text = running ? "■" : "▶";
        StartStopIcon.Foreground = new SolidColorBrush(running ? Color.FromRgb(238, 118, 110) : Color.FromRgb(118, 217, 132));
        StartStopText.Text = running ? "Stop" : "Start";
        StartStopButton.IsEnabled = !_busy;
        InputDevices.IsEnabled = !running && !_busy;
        OutputDevices.IsEnabled = !running && !_busy;
        BufferChoices.IsEnabled = !running && !_busy;
        RefreshButton.IsEnabled = !running && !_busy;
    }

    private void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, "EchoBridge\nAudio playback repeater for Windows\nVersion 1.0", "About EchoBridge", MessageBoxButton.OK, MessageBoxImage.Information);

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingAfterCleanup) return;
        e.Cancel = true;
        if (_busy) return;
        _busy = true;
        UpdateControls();
        _meterTimer.Stop();
        _settings.InputEndpointId = InputDevices.SelectedValue as string ?? _settings.InputEndpointId;
        _settings.OutputEndpointId = OutputDevices.SelectedValue as string ?? _settings.OutputEndpointId;
        _settings.BufferMilliseconds = SelectedBuffer();
        _settings.GainPercent = (int)GainSlider.Value;
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settingsService.Save(_settings);
        try { await _repeater.DisposeAsync(); }
        catch (Exception ex) { LoggingService.Write("Shutdown cleanup failed", ex); }
        _closingAfterCleanup = true;
        Close();
    }
}
