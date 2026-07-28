using System.Windows;
using System.Windows.Controls;
using DiskCleaner.Helpers;
using DiskCleaner.ViewModels;

namespace DiskCleaner.Views
{
    public partial class QuarantineView : UserControl
    {
        public QuarantineView()
        {
            InitializeComponent();
        }

        private void OpenLocation_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is QuarantineItemVm item && !string.IsNullOrEmpty(item.QuarantinePath))
                ShellHelper.RevealInExplorer(item.QuarantinePath);
        }
    }
}
