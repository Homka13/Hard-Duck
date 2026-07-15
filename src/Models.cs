using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace HardDuck;

public enum StageStatus
{
    Pending,
    Running,
    Ok,
    Warn,
    Skip,
    Fail
}

public sealed record StageOutcome(StageStatus Status, string Summary);

public sealed class StageVm : INotifyPropertyChanged
{
    public StageVm(string key, string title)
    {
        Key = key;
        Title = title;
    }

    public string Key { get; }
    public string Title { get; }

    private StageStatus _status = StageStatus.Pending;
    public StageStatus Status
    {
        get => _status;
        set { _status = value; Raise(); Raise(nameof(StatusText)); Raise(nameof(StatusBrush)); Raise(nameof(StatusIcon)); }
    }

    private string _summary = "очікує";
    public string Summary
    {
        get => _summary;
        set { _summary = value; Raise(); Raise(nameof(StatusText)); }
    }

    public PackIconKind StatusIcon => Status switch
    {
        StageStatus.Ok      => PackIconKind.CheckCircle,
        StageStatus.Warn    => PackIconKind.Alert,
        StageStatus.Fail    => PackIconKind.CloseCircle,
        StageStatus.Running => PackIconKind.Sync,
        StageStatus.Skip    => PackIconKind.MinusCircle,
        _                   => PackIconKind.CircleOutline
    };

    public string StatusText => Status switch
    {
        StageStatus.Pending => "очікує",
        StageStatus.Running => "виконується…",
        StageStatus.Ok      => "OK" + (string.IsNullOrWhiteSpace(_summary) || _summary == "OK" ? "" : $" — {_summary}"),
        StageStatus.Warn    => _summary,
        StageStatus.Skip    => _summary,
        StageStatus.Fail    => _summary,
        _ => _summary
    };

    public Brush StatusBrush => Status switch
    {
        StageStatus.Ok      => new SolidColorBrush(Color.FromRgb(0x3F, 0xB6, 0x6E)),
        StageStatus.Warn    => new SolidColorBrush(Color.FromRgb(0xE0, 0x9A, 0x2B)),
        StageStatus.Skip    => new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA6)),
        StageStatus.Fail    => new SolidColorBrush(Color.FromRgb(0xE0, 0x4F, 0x4F)),
        StageStatus.Running => new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xF6)),
        _                   => new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x84))
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
