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

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateBar.IsOpen = true;
        UpdateBar.Severity = InfoBarSeverity.Informational;
        UpdateBar.Title = "正在检查更新…";
        UpdateBar.Message = string.Empty;
        UpdateBar.ActionButton = null;

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

            UpdateBar.Severity = InfoBarSeverity.Informational;
            UpdateBar.Title = $"发现新版本 {info.LatestVersion}";
            UpdateBar.Message = string.IsNullOrWhiteSpace(info.ReleaseNotes)
                ? $"当前版本 {info.CurrentVersion}，建议前往发布页下载最新版。"
                : Truncate(info.ReleaseNotes, 220);

            var btn = new Button { Content = "打开发布页", Padding = new Thickness(12, 5, 12, 5) };
            btn.Click += (_, _) => App.ViewModel.OpenReleasePage();
            UpdateBar.ActionButton = btn;
            UpdateBar.IsClosable = true;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
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
