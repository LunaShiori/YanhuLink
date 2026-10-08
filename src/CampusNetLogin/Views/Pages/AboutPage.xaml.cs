using CampusNetLogin.Helpers;
using CampusNetLogin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CampusNetLogin.Views.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppNameText.Text = UpdateService.ProductName;
        VersionBadge.Text = $"v{UpdateService.CurrentVersion}";
        BuildText.Text = $"构建于 {BuildDate()}";
        ConfigPathText.Text = App.ViewModel.ConfigDirectory;
    }

    private static string BuildDate()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                return File.GetLastWriteTime(exe).ToString("yyyy-MM-dd");
        }
        catch { /* ignore */ }
        return "未知";
    }

    private UpdateInfo? _lastInfo;

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateBar.IsOpen = true;
        UpdateBar.Severity = InfoBarSeverity.Informational;
        UpdateBar.Title = "正在检查更新…";
        UpdateBar.Message = string.Empty;
        UpdateBar.ActionButton = null;
        _lastInfo = null;

        try
        {
            var info = await App.ViewModel.CheckUpdateAsync(manual: true);

            if (!info.CheckSucceeded)
            {
                UpdateBar.Severity = InfoBarSeverity.Warning;
                UpdateBar.Title = "检查更新失败";
                UpdateBar.Message = info.ErrorMessage;
                return;
            }

            if (!info.HasUpdate)
            {
                UpdateBar.Severity = InfoBarSeverity.Success;
                UpdateBar.Title = "已是最新版本";
                UpdateBar.Message = $"当前版本 {info.CurrentVersion}，没有可用的更新。";
                return;
            }

            _lastInfo = info;

            UpdateBar.Severity = InfoBarSeverity.Informational;
            UpdateBar.Title = $"发现新版本 {info.LatestVersion}" +
                              (info.IsMandatory ? "（强烈建议更新）" : string.Empty);
            UpdateBar.Message = string.IsNullOrWhiteSpace(info.ReleaseNotes)
                ? $"当前版本 {info.CurrentVersion}，建议更新到 {info.LatestVersion}。"
                : Truncate(info.ReleaseNotes, 220);

            // InfoBar.ActionButton 的类型是 ButtonBase（只放得下一个按钮）。
            // 因此：
            //   · 有可识别的安装包 → 主按钮「一键更新」，发布页放到下面的链接按钮
            //   · 没有            → 主按钮「打开发布页」
            // 这样两种情况下用户都有明确的下一步，不会卡住。
            if (info.CanAutoInstall)
            {
                var installBtn = new Button
                {
                    Content = "一键更新",
                    Padding = new Thickness(12, 5, 12, 5),
                    Style = (Style)Application.Current.Resources["HeroButtonStyle"],
                    HorizontalAlignment = HorizontalAlignment.Center,
                    MinHeight = 32,
                };
                installBtn.Click += OnInstallUpdate;
                UpdateBar.ActionButton = installBtn;

                UpdateBar.Message += "\n\n点「一键更新」将自动下载安装；" +
                                     "想手动下载可前往发布页。";
            }
            else
            {
                var pageBtn = new Button
                {
                    Content = "打开发布页",
                    Padding = new Thickness(12, 5, 12, 5),
                };
                pageBtn.Click += (_, _) => App.ViewModel.OpenReleasePage();
                UpdateBar.ActionButton = pageBtn;
                UpdateBar.Message += "\n\n未在 Release 中找到可自动安装的包，请手动下载。";
            }

            UpdateBar.IsClosable = true;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_lastInfo is null) return;

        var confirmed = await Dialogs.ConfirmAsync(
            "开始更新",
            $"将下载 {_lastInfo.LatestVersion} 版本并自动安装。\n\n" +
            "安装过程中本程序会自动退出，完成后重新打开即可。\n" +
            "（不会重启电脑，可以放心继续手头的事。）",
            "开始更新", "取消");

        if (!confirmed) return;

        if (sender is Button b) b.IsEnabled = false;

        try
        {
            var ok = await App.ViewModel.DownloadAndInstallUpdateAsync(_lastInfo);

            if (!ok)
            {
                UpdateBar.Severity = InfoBarSeverity.Warning;
                UpdateBar.Title = "自动更新失败";
                UpdateBar.Message = "可能是网络中断或临时目录不可写。请点「打开发布页」手动下载。";
                if (sender is Button b2) b2.IsEnabled = true;
                return;
            }

            UpdateBar.Severity = InfoBarSeverity.Success;
            UpdateBar.Title = "正在安装，程序即将退出";
            UpdateBar.Message = "更新程序已启动，本窗口会在几秒内自动关闭。";

            // 给安装程序一点时间把文件句柄抢过去，再退出本程序
            await Task.Delay(1500);

            try { App.InstanceGuard?.ReleaseForRestart(); } catch { /* ignore */ }
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            UpdateBar.Severity = InfoBarSeverity.Warning;
            UpdateBar.Title = "自动更新出错";
            UpdateBar.Message = ex.Message;
            if (sender is Button b3) b3.IsEnabled = true;
        }
    }

    private static string Truncate(string s, int max)
    {
        s = s.Replace("\r\n", "\n").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    private async void OnOpenConfigDir(object sender, RoutedEventArgs e)
    {
        if (!App.ViewModel.OpenConfigDirectory())
            await Dialogs.InfoAsync("打开失败", "无法打开配置目录，请检查系统权限。");
    }

    private void OnOpenRepo(object sender, RoutedEventArgs e)
    {
        UpdateService.OpenInBrowser(UpdateService.ReleasesPageUrl);
    }

    private async void OnShowLicense(object sender, RoutedEventArgs e)
    {
        await Dialogs.InfoAsync("开源许可",
            "MIT License\n\n" +
            "Copyright (c) 2026\n\n" +
            "Permission is hereby granted, free of charge, to any person obtaining a copy " +
            "of this software and associated documentation files (the \"Software\"), to deal " +
            "in the Software without restriction, including without limitation the rights " +
            "to use, copy, modify, merge, publish, distribute, sublicense, and/or sell " +
            "copies of the Software, and to permit persons to whom the Software is " +
            "furnished to do so, subject to the following conditions:\n\n" +
            "The above copyright notice and this permission notice shall be included in " +
            "all copies or substantial portions of the Software.\n\n" +
            "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND.");
    }
}
