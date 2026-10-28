using System.Windows;
using System.Windows.Threading;

namespace SaveForge.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandledException;

        var window = new MainWindow();

        // 支持命令行直接打开：SaveForge.exe <存档路径>
        if (e.Args.Length > 0 && File.Exists(e.Args[0]))
            window.LoadFile(e.Args[0]);

        window.Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"发生未处理的错误：\n\n{e.Exception.Message}",
            "SaveForge",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }
}
