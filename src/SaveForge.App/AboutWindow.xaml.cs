using System.Reflection;
using System.Windows;

namespace SaveForge.App;

/// <summary>
/// 关于窗口：版本、功能、使用步骤、支持的格式、快捷键与安全提示。
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        TxtVersion.Text = version is null
            ? "版本 0.1.0"
            : $"版本 {version.Major}.{version.Minor}.{version.Build}";
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
