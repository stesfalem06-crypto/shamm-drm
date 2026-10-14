using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace XSeller.Models;

/// <summary>One phone in the side panel. Rows are kept stable by serial so ticks survive refreshes.</summary>
public sealed class DeviceRow : INotifyPropertyChanged
{
    public DeviceRow(string serial) { Serial = serial; }

    public string Serial { get; }

    private string _model = "Phone";
    public string Model { get => _model; set => Set(ref _model, value); }

    private string _state = "";
    public string State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            OnChanged(nameof(IsReady));
            OnChanged(nameof(StateLabel));
            OnChanged(nameof(Hint));
        }
    }

    public bool IsReady => State == "device";

    public string StateLabel => State switch
    {
        "device" => "Ready",
        "unauthorized" => "Waiting for permission",
        "offline" => "Offline",
        "disconnected" => "Disconnected",
        _ => State,
    };

    public string Hint => State switch
    {
        "unauthorized" => "Tap Allow on the phone",
        "offline" => "Reconnect the cable / unlock the phone",
        "disconnected" => "Unplugged",
        _ => "",
    };

    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    private bool _busy;
    public bool Busy { get => _busy; set => Set(ref _busy, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnChanged(n);
        return true;
    }
}
