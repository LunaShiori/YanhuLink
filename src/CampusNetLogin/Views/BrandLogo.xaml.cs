using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CampusNetLogin.Views;

/// <summary>
/// 砚湖连 品牌标记（矢量版）。
///
/// 与 Assets/app.ico 同一套图形语言：圆角方形底 + 水波 + 水滴。
/// 独立成控件是为了「一处定义、处处一致」——
/// 早期版本标题栏 / 向导 / 关于页各自写死一个 FontIcon 字形，
/// 换图标时漏掉了标题栏，导致出现两套视觉。
///
/// 用法：&lt;views:BrandLogo Width="22" Height="22" /&gt;
/// </summary>
public sealed partial class BrandLogo : UserControl
{
    /// <summary>圆角半径。控件的视觉圆角应与自身边长成比例，
    /// 22px 的标题栏用 6，64px 的向导页用 18。</summary>
    public static readonly DependencyProperty CornerRadiusValueProperty =
        DependencyProperty.Register(
            nameof(CornerRadiusValue),
            typeof(CornerRadius),
            typeof(BrandLogo),
            new PropertyMetadata(new CornerRadius(6)));

    public CornerRadius CornerRadiusValue
    {
        get => (CornerRadius)GetValue(CornerRadiusValueProperty);
        set => SetValue(CornerRadiusValueProperty, value);
    }

    public BrandLogo()
    {
        InitializeComponent();
    }
}
