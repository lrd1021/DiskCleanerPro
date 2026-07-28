using System.Windows.Controls;
using System.Windows;
using DiskCleaner.Helpers;
using DiskCleaner.Models;

namespace DiskCleaner.Views
{
    public partial class DiskAnalysisView : UserControl
    {
        public DiskAnalysisView() => InitializeComponent();

        private void OpenLocation_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is FileNode node)
            {
                ShellHelper.RevealInExplorer(node.FullPath);
                e.Handled = true;
            }
        }
    }
}
