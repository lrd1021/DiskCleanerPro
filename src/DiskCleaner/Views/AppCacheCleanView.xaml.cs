using System.Windows;
using System.Windows.Controls;
using DiskCleaner.Helpers;
using DiskCleaner.Models;

namespace DiskCleaner.Views
{
    public partial class AppCacheCleanView : UserControl
    {
        public AppCacheCleanView() => InitializeComponent();

        private void OpenLocation_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is CleanTarget target &&
                target.Paths.Count > 0 && !string.IsNullOrEmpty(target.Paths[0]))
            {
                ShellHelper.RevealInExplorer(target.Paths[0]);
            }
        }
    }
}
