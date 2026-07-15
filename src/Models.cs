using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace HardDuck;

public enum StageStatus
{
    Pending,   // ще не виконувався
    Running,   // виконується зараз
    Ok,
    Warn,      // виконано, але є що доробити (наприклад, BIOS-пароль вручну)
    Skip,      // пропущено свідомо (вже налаштовано / вимкнено користувачем)
    Fail
}

/// <summary>Результат одного етапу: статус + короткий підсумок для CSV.</summary>
public sealed record StageOutcome(StageStatus Status, string Summary);

/// <summary>ViewModel рядка чекліста у вікні.</summary>
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

    public string StatusIcon => Status switch
    {
        StageStatus.Ok      => "CheckCircle",
        StageStatus.Warn    => "Alert",
        StageStatus.Fail    => "CloseCircle",
        StageStatus.Running => "Sync",
        StageStatus.Skip    => "MinusCircle",
        _                   => "CircleOutline"
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
