using Velopack;

namespace Counterpick.App;

/// <summary>
/// Explicit entry point in place of the one WPF generates from App.xaml, so the Velopack
/// hook runs before anything else in the process.
///
/// The installer launches the exe with hook arguments while installing, updating and
/// uninstalling; <c>Run()</c> handles those and exits without ever building a window.
/// On a normal launch it returns at once and WPF starts as usual.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
