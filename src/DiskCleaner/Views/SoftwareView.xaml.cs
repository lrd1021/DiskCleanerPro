using System.Windows.Controls;

namespace DiskCleaner.Views
{
    public partial class SoftwareView : UserControl
    {
        public SoftwareView() => InitializeComponent();

        private void ListViewColumnHeader_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (e.OriginalSource is GridViewColumnHeader header && header.Column != null)
            {
                var vm = DataContext as ViewModels.SoftwareViewModel;
                vm?.Sort(header.Column.Header as string);
            }
        }
    }
}
