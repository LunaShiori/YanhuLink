using CampusNetLogin.Helpers;
using CampusNetLogin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CampusNetLogin.Views.Pages;

public sealed partial class AccountPage : Page
{
    private readonly MainViewModel _vm = App.ViewModel;

    public AccountPage()
    {
        InitializeComponent();
        DataContext = _vm;

        PasswordInput.Password = _vm.Password;
        PlainPasswordBox.Text = _vm.Password;

        Loaded += (_, _) => SelectCurrentIsp();
    }

    private void SelectCurrentIsp()
    {
        DispatcherQueue.TryEnqueue(() =>
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
        });
    }

    // ------------------------------------------------------------------
    private void OnIspChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string name)
        {
            _vm.Isp = name;
            SuffixBox.Text = _vm.Suffix;
        }
    }

    private void OnSaveSuffix(object sender, RoutedEventArgs e)
    {
        _vm.Suffix = SuffixBox.Text;
        _vm.SaveSuffixOnly();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        _vm.Password = PasswordInput.Password;
        if (PlainPasswordBox.Text != PasswordInput.Password)
            PlainPasswordBox.Text = PasswordInput.Password;
    }

    private void OnPlainPasswordChanged(object sender, TextChangedEventArgs e)
    {
        _vm.Password = PlainPasswordBox.Text;
        if (PasswordInput.Password != PlainPasswordBox.Text)
            PasswordInput.Password = PlainPasswordBox.Text;
    }

    private void OnToggleShowPassword(object sender, RoutedEventArgs e)
    {
        bool show = ShowPasswordToggle.IsChecked == true;

        if (show)
        {
            PlainPasswordBox.Text = PasswordInput.Password;
            PlainPasswordBox.Visibility = Visibility.Visible;
            PasswordInput.Visibility = Visibility.Collapsed;
            EyeIcon.Glyph = "\uE8F4"; // Hide
        }
        else
        {
            PasswordInput.Password = PlainPasswordBox.Text;
            PasswordInput.Visibility = Visibility.Visible;
            PlainPasswordBox.Visibility = Visibility.Collapsed;
            EyeIcon.Glyph = "\uE7B3"; // RedEye
        }
    }

    // ------------------------------------------------------------------
    private async void OnSave(object sender, RoutedEventArgs e)
    {
        bool ok = _vm.SaveConfig();
        await Dialogs.InfoAsync(
            ok ? "已保存" : "保存失败",
            ok
                ? $"设置已保存，账号：{(_vm.Username.Length == 0 ? "(未填写)" : _vm.Username)}"
                : "无法写入配置文件，请检查磁盘权限或杀毒软件拦截。");
    }

    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_vm.Username) || string.IsNullOrEmpty(_vm.Password))
        {
            await Dialogs.InfoAsync("还差一点", "请先填写账号和密码。");
            return;
        }
        await _vm.LoginAsync();
    }

    private async void OnLogout(object sender, RoutedEventArgs e)
    {
        await _vm.LogoutAsync();
    }
}
