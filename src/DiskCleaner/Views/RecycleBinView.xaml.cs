using System.Windows;
using System.Windows.Controls;
using DiskCleaner.ViewModels;

namespace DiskCleaner.Views
{
    public partial class RecycleBinView : UserControl
    {
        public RecycleBinView() => InitializeComponent();

        private void ListViewColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is GridViewColumnHeader header && header.Column != null && DataContext is RecycleBinViewModel vm)
            {
                var headerText = header.Column.Header as string;
                if (!string.IsNullOrEmpty(headerText))
                    vm.ToggleSort(headerText);
            }
        }
    }
}
