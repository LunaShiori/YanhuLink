using CampusNetLogin.Models;
using CampusNetLogin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CampusNetLogin.Views;

/// <summary>
/// 首次启动向导。
/// 四步：欢迎 → 账号 → 自动化 → 完成。
/// 采用「不写盘、只改内存」的策略，最后一次性提交，
/// 这样用户中途关掉也不会留下半截配置。
/// </summary>
public sealed partial class OnboardingDialog : ContentDialog
{
    private readonly MainViewModel _vm;
    private int _step; // 0..3

    private readonly List<StackPanel> _steps = new();
    private readonly List<Border> _dots = new();

    public OnboardingDialog(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        _steps.AddRange([StepWelcome, StepAccount, StepAutomation, StepDone]);
        _dots.AddRange([Dot1, Dot2, Dot3, Dot4]);

        IspList.ItemsSource = _vm.Isps;

        // 用当前配置预填，避免老用户重新走向导时丢失已有信息
        UsernameBox.Text = _vm.Username;
        PwdBox.Password = _vm.Password;
        AutoLoginSwitch.IsOn = _vm.AutoLoginOnStart;
        WatchdogSwitch.IsOn = _vm.WatchdogEnabled;
        AutoStartSwitch.IsOn = _vm.AutoStart;

        Opened += OnOpened;
        PrimaryButtonClick += OnPrimaryClick;
        SecondaryButtonClick += OnSecondaryClick;
        CloseButtonClick += OnCloseClick;

        ShowStep(0);
    }

    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        // 容器可能尚未生成，延迟一帧再勾选运营商
        DispatcherQueue.TryEnqueue(SelectCurrentIsp);
    }

    private void SelectCurrentIsp()
    {
        for (int i = 0; i < IspList.Items.Count; i++)
        {
            if (IspList.ContainerFromIndex(i) is RadioButton rb &&
                rb.Tag is string name && name == _vm.Isp)
            {
                rb.IsChecked = true;
                break;
            }
        }
    }

    private void OnIspChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string name)
            _vm.Isp = name;
    }

    // ------------------------------------------------------------------
    // 步骤切换
    // ------------------------------------------------------------------
    private void ShowStep(int index)
    {
        _step = Math.Clamp(index, 0, _steps.Count - 1);

        for (int i = 0; i < _steps.Count; i++)
            _steps[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;

        RefreshDots();
        RefreshButtons();
    }

    private void RefreshDots()
    {
        var active = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
        var idle = (Brush)Application.Current.Resources["StatusUnknownBrush"];

        for (int i = 0; i < _dots.Count; i++)
        {
            _dots[i].Background = i <= _step ? active : idle;
            _dots[i].Opacity = i <= _step ? 1.0 : 0.45;
        }
    }

    private void RefreshButtons()
    {
        bool last = _step == _steps.Count - 1;

        PrimaryButtonText = _step switch
        {
            0 => "开始设置",
            3 => "完成",
            _ => "下一步",
        };
        SecondaryButtonText = _step == 0 ? string.Empty : "上一步";
        CloseButtonText = last ? string.Empty : "跳过引导";

        if (_step == 3) RefreshSummary();
    }

    private void RefreshSummary()
    {
        var isp = string.IsNullOrWhiteSpace(_vm.Username)
            ? "（未填写账号）"
            : $"{_vm.Username} · {_vm.Isp}";
        SummaryText.Text = $"账号：{isp}。配置已保存在你本机，随时可以在设置里修改。";
    }

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_step < _steps.Count - 1)
        {
            if (_step == 1 && !ValidateAccount())
            {
                args.Cancel = true; // 留在当前步骤
                return;
            }
            if (_step == 1) PushAccountToVm();

            args.Cancel = true;  // 阻止对话框关闭
            ShowStep(_step + 1);
            return;
        }

        // 最后一步：提交
        PushAccountToVm();
        _vm.AutoLoginOnStart = AutoLoginSwitch.IsOn;
        _vm.WatchdogEnabled = WatchdogSwitch.IsOn;
        _vm.AutoStart = AutoStartSwitch.IsOn;
        _vm.CommitSetup();
    }

    private void OnSecondaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        ShowStep(_step - 1);
    }

    private void OnCloseClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 「跳过引导」：不写入账号信息，但标记已完成，避免每次启动都弹
        _vm.FirstRunDone = true;
        _vm.SaveConfigQuiet();
    }

    // ------------------------------------------------------------------
    private bool ValidateAccount()
    {
        if (string.IsNullOrWhiteSpace(UsernameBox.Text))
        {
            UsernameBox.Focus(FocusState.Programmatic);
            return false;
        }

        // 密码允许为空（有些人只用验证码或后续再填），这里不强制
        return true;
    }

    private void PushAccountToVm()
    {
        _vm.Username = UsernameBox.Text.Trim();
        _vm.Password = PwdBox.Password;
    }
}
