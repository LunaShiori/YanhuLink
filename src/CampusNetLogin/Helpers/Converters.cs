using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace CampusNetLogin.Helpers;

/// <summary>
/// bool → Visibility 转换器。true 变 Visible，false 变 Collapsed。
/// 支持 ConverterParameter="Invert" 反转。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool flag = value is bool b && b;

        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        bool visible = value is Visibility v && v == Visibility.Visible;
        bool invert = parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
        return invert ? !visible : visible;
    }
}

/// <summary>
/// 集合是否为空 → Visibility。空集合为 Visible（用于「空状态提示」）。
/// ConverterParameter="Invert" 时相反。
/// </summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool empty = value switch
        {
            System.Collections.ICollection c => c.Count == 0,
            System.Collections.IEnumerable e => !e.GetEnumerator().MoveNext(),
            null => true,
            _ => false,
        };

        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            empty = !empty;

        return empty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
