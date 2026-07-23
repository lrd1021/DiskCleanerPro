using System;
using System.Windows;
using System.Windows.Media.Imaging;
using DiskCleaner.Views;

namespace DiskCleaner
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;

        public MainWindow()
        {
            InitializeComponent();
            SetIcon();
            _vm = new MainViewModel();
            DataContext = _vm;
            Loaded += OnLoaded;
        }

        private void SetIcon()
        {
            try
            {
                var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "logo.ico");
                if (System.IO.File.Exists(path))
                    Icon = BitmapFrame.Create(new Uri(path, UriKind.Absolute));
            }
            catch { }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            _vm.SelectedNav = _vm.NavItems[0];
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            new SettingsWindow { Owner = this }.ShowDialog();
        }

        // ── 自定义标题栏按钮（WindowChrome 接管后系统按钮隐藏，需自绘）──
        private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnMax_Click(object sender, RoutedEventArgs e) =>
            WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
