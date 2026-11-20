using System.Windows;
using Wpf.Ui.Appearance;

namespace WarpscoutGui;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            ApplicationAccentColorManager.ApplySystemAccent();
        }
        catch { }
    }
}
