using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CampusNetLogin.Helpers;

/// <summary>
/// 对话框与轻提示的统一入口。
/// 所有页面都通过这里弹窗，保证样式与措辞一致，也避免每处都写 XamlRoot。
/// </summary>
public static class Dialogs
{
    private static XamlRoot? _xamlRoot;

    /// <summary>由 Shell 在窗口就绪后注入，供全局弹窗使用。</summary>
    public static void Register(XamlRoot root) => _xamlRoot = root;

    private static XamlRoot? Resolve(XamlRoot? explicitRoot) =>
        explicitRoot ?? _xamlRoot;

    /// <summary>单按钮提示。</summary>
    public static async Task InfoAsync(string title, string message,
        XamlRoot? root = null)
    {
        var r = Resolve(root);
        if (r is null) return;

        var dlg = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            },
            CloseButtonText = "知道了",
            XamlRoot = r,
        };

        try { await dlg.ShowAsync(); } catch { /* 窗口已关闭时忽略 */ }
    }

    /// <summary>确认框。用户点「确定」返回 true。</summary>
    public static async Task<bool> ConfirmAsync(
        string title, string message,
        string primaryText = "确定",
        string closeText = "取消",
        XamlRoot? root = null)
    {
        var r = Resolve(root);
        if (r is null) return false;

        var dlg = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            },
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = r,
        };

        try
        {
            return await dlg.ShowAsync() == ContentDialogResult.Primary;
        }
        catch
        {
            return false;
        }
    }
}
