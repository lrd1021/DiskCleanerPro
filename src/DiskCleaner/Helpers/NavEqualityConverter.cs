using System;
using System.Globalization;
using System.Windows.Data;

namespace DiskCleaner.Helpers
{
    /// <summary>
    /// 导航选中项比较转换器，用于 MultiBinding
    /// values[0] = 当前导航项文本, values[1] = SelectedNav
    /// 返回两者是否相等
    /// </summary>
    public class NavEqualityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2) return false;
            return string.Equals(values[0] as string, values[1] as string, StringComparison.Ordinal);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
