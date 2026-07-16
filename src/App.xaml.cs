using System.Windows;

namespace HardDuck;

public partial class App : Application
{
    public App()
    {
        // Force load Material Design assemblies into AppDomain for single-file publish
        _ = typeof(MaterialDesignThemes.Wpf.Card);
        _ = typeof(MaterialDesignColors.Swatch);
    }
}
