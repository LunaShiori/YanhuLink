using Microsoft.UI.Xaml.Controls;

namespace CampusNetLogin.Views;

/// <summary>
/// 更新进度面板（见 XAML 里的说明）。
///
/// 纯展示控件，不带任何逻辑 —— 状态全部来自 MainViewModel 的绑定，
/// 所以不需要在这里订阅 PropertyChanged，也不需要暴露任何 API。
/// </summary>
public sealed partial class UpdateProgressPanel : UserControl
{
    public UpdateProgressPanel()
    {
        InitializeComponent();
    }
}
