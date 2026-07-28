using System.Windows;
using System.Windows.Controls;
using DiskCleaner.Helpers;
using DiskCleaner.Models;

namespace DiskCleaner.Views
{
    public partial class DuplicateView : UserControl
    {
        public DuplicateView() => InitializeComponent();

        private void OpenLocation_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is DuplicateFile file && !string.IsNullOrEmpty(file.FilePath))
                ShellHelper.RevealInExplorer(file.FilePath);
        }
    }
}
