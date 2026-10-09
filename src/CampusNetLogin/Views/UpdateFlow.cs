using CampusNetLogin.Helpers;
using CampusNetLogin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CampusNetLogin.Views;

/// <summary>一键更新流程的结果。</summary>
internal enum UpdateFlowResult
{
    /// <summary>用户在确认框里点了取消 —— 界面什么都不用做。</summary>
    Cancelled,

    /// <summary>下载或安装失败 —— 界面应提示失败并引导去发布页。</summary>
    Failed,

    /// <summary>安装程序已拉起，本进程即将退出。</summary>
    Started,
}

/// <summary>
/// 「一键更新」的共用流程：确认 → 下载（带进度）→ 退出让安装程序接手。
///
/// 抽出来是因为 AboutPage 与 SettingsPage 都要用，
/// 而退出时序（先放句柄、再 ReleaseForRestart、最后 Exit）写两遍迟早会写歪。
///
/// 进度本身不在这里显示 —— 页面上的 <see cref="UpdateProgressPanel"/> 直接绑定
/// MainViewModel 的进度属性，这里只负责跑流程。
/// </summary>
internal static class UpdateFlow
{
    /// <summary>
    /// 跑完整的一键更新流程。
    ///
    /// 返回 <see cref="UpdateFlowResult.Started"/> 时本进程已经
    /// <c>Application.Current.Exit()</c>，**调用方不要再碰任何 UI 元素**。
    /// </summary>
    public static async Task<UpdateFlowResult> RunAsync(UpdateInfo info, XamlRoot? root = null)
    {
        if (info is null || !info.CanAutoInstall) return UpdateFlowResult.Failed;

        var confirmed = await Dialogs.ConfirmAsync(
            "开始更新",
            $"将下载 {info.LatestVersion} 版本并自动安装。\n\n" +
            "安装过程中本程序会自动退出，完成后重新打开即可。\n" +
            "（不会重启电脑，可以放心继续手头的事。）",
            "开始更新", "取消", root);
        if (!confirmed) return UpdateFlowResult.Cancelled;

        var ok = await App.ViewModel.DownloadAndInstallUpdateAsync(info);
        if (!ok) return UpdateFlowResult.Failed;

        // 给安装程序一点时间把文件句柄抢过去，再退出本程序
        await Task.Delay(1500);

        try { App.InstanceGuard?.ReleaseForRestart(); } catch { /* ignore */ }
        Application.Current.Exit();
        return UpdateFlowResult.Started;
    }
}
