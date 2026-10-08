using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CampusNetLogin.Views.Pages;

public sealed partial class LogsPage : Page
{
    public LogsPage()
    {
        InitializeComponent();

        LogList.ItemsSource = App.Log.Entries;

        // 新增日志时自动滚到底部
        App.Log.EntryAdded += OnEntryAdded;
        Unloaded += (_, _) => App.Log.EntryAdded -= OnEntryAdded;

        LogList.Items.VectorChanged += (_, _) => UpdateEmptyHint();
        UpdateEmptyHint();
    }

    private void OnEntryAdded(Services.LogEntry entry)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateEmptyHint();
            if (AutoScrollSwitch.IsOn && LogList.Items.Count > 0)
            {
                LogList.ScrollIntoView(LogList.Items[^1]);
            }
        });
    }

    private void UpdateEmptyHint()
    {
        EmptyHint.Visibility = LogList.Items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = App.Log.LogDirectory;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            App.Log.Warn($"打开日志目录失败：{ex.Message}");
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(App.Log.ExportText());
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            App.Log.Info("日志已复制到剪贴板");
        }
        catch (Exception ex)
        {
            App.Log.Warn($"复制失败：{ex.Message}");
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        App.Log.Clear();
        App.Log.Info("日志已清空");
        UpdateEmptyHint();
    }
}
