using System.Windows.Controls;

namespace DiskCleaner.Views
{
    public partial class FileMoveView : UserControl
    {
        public FileMoveView() => InitializeComponent();

        private void OnHeaderClick(object sender, System.Windows.RoutedEventArgs e)
        {
            if (sender is GridViewColumnHeader header && header.Column?.Header is string h)
                (DataContext as ViewModels.FileMoveViewModel)?.Sort(h);
        }
    }
}
