using System.Windows;

namespace HardDuck;

public partial class PinDialog : Window
{
    public const int MinPinLength = 12;

    /// <summary>Результат — лише якщо DialogResult == true.</summary>
    public string Pin { get; private set; } = "";

    public PinDialog()
    {
        InitializeComponent();
        RulesText.Text = $"Мінімум {MinPinLength} цифр, без простих послідовностей і повторів. " +
                         "Цей PIN питатиметься при кожному ввімкненні комп'ютера — до входу у Windows.";
    }

    /// <summary>Та сама перевірка сили PIN, що була в Harden-Workstation.ps1.</summary>
    private static string? Validate(string pin)
    {
        if (pin.Length < MinPinLength)
            return $"Закороткий PIN — потрібно мінімум {MinPinLength} цифр.";
        if (!pin.All(char.IsAsciiDigit))
            return "PIN має містити лише цифри (0-9).";
        if (pin.All(c => c == pin[0]))
            return "PIN не може складатись з однієї цифри, що повторюється.";
        const string asc = "01234567890123456789";
        const string desc = "98765432109876543210";
        if (asc.Contains(pin) || desc.Contains(pin))
            return "PIN не може бути простою послідовністю цифр.";
        return null;
    }

    private void OnPinChanged(object sender, RoutedEventArgs e)
    {
        var p1 = Pin1.Password;
        var p2 = Pin2.Password;

        var problem = Validate(p1);
        if (problem is null && p2.Length > 0 && p1 != p2)
            problem = "PIN-и не збігаються.";

        ErrorText.Text = problem ?? "";
        ErrorText.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = problem is null && p1.Length > 0 && p1 == p2;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Pin = Pin1.Password;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
