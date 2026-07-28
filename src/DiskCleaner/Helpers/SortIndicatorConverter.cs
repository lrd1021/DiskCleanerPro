using System;
using System.Globalization;
using System.Windows.Data;
using System.ComponentModel;

namespace DiskCleaner.Helpers
{
    /// <summary>
    /// 多值转换器：判断某个列头的排序三角是否应高亮。
    /// values[0] = 当前列的表头文字(string)
    /// values[1] = 当前正在排序的列表头文字(string)
    /// values[2] = 当前排序方向(ListSortDirection)
    /// parameter = "Up" 或 "Down"
    /// 当 该列正是当前排序列，且方向与 parameter 匹配时返回 true。
    /// </summary>
    public class SortIndicatorConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 3) return false;
            string colHeader = values[0] as string;
            string curHeader = values[1] as string;
            if (colHeader == null || curHeader == null) return false;
            if (colHeader != curHeader) return false;

            var dir = values[2] as ListSortDirection?;
            if (dir == null) return false;

            string arrow = parameter as string;
            if (arrow == "Up") return dir == ListSortDirection.Ascending;
            if (arrow == "Down") return dir == ListSortDirection.Descending;
            return false;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
