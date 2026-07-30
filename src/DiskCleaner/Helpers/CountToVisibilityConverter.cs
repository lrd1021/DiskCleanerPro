using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DiskCleaner.Helpers
{
    /// <summary>
    /// 将集合元素数量或整数转换为 Visibility：大于 0 时可见，否则折叠。
    /// ConverterParameter="Invert" 时逻辑取反（等于 0 时可见）。
    /// </summary>
    [ValueConversion(typeof(int), typeof(Visibility))]
    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int count = 0;
            if (value is int i)
                count = i;
            else if (value is ICollection col)
                count = col.Count;

            bool invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
            bool visible = invert ? count == 0 : count > 0;
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
