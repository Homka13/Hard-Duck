using System.Windows;

namespace HardenWorkstation;

public partial class UserNameDialog : Window
{
    public string UserName { get; private set; } = "";

    public UserNameDialog() => InitializeComponent();

    private void OnChanged(object sender, RoutedEventArgs e)
        => OkButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);

    private void OnOk(object sender, RoutedEventArgs e)
    {
        UserName = NameBox.Text.Trim();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
