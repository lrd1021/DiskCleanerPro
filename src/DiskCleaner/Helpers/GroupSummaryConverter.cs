using System;
using System.Globalization;
using System.Windows.Data;
using DiskCleaner.Services;

namespace DiskCleaner.Helpers
{
    /// <summary>回收站分组标题用的合计转换器：输入 CollectionViewGroup，输出组内总大小文本。</summary>
    public class GroupSummaryConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is CollectionViewGroup group)
            {
                long total = 0;
                foreach (var o in group.Items)
                    if (o is RecycleBinItem it) total += it.SizeBytes;
                return "· 共 " + FileSizeFormatter.Format(total);
            }
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
