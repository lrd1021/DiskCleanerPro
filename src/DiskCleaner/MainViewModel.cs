using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using DiskCleaner.Helpers;
using DiskCleaner.ViewModels;
using DiskCleaner.Views;

namespace DiskCleaner
{
    public class MainViewModel : ViewModelBase
    {
        private object _currentPage;
        private string _selectedNav;
        private string _statusBarText;
        private bool _isBusy;

        private readonly Dictionary<string, object> _pages = new();

        public ObservableCollection<string> NavItems { get; } = new ObservableCollection<string>
        {
            "临时文件清理",
            "磁盘空间分析",
            "重复文件检测",
            "浏览器缓存",
            "回收站清空",
            "文件搬家",
            "软件管理"
        };

        public object CurrentPage
        {
            get => _currentPage;
            set => Set(ref _currentPage, value);
        }

        public string SelectedNav
        {
            get => _selectedNav;
            set
            {
                if (Set(ref _selectedNav, value))
                    NavigateTo(value);
            }
        }

        public string StatusBarText
        {
            get => _statusBarText;
            set => Set(ref _statusBarText, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set => Set(ref _isBusy, value);
        }

        public ICommand NavigateCommand { get; }

        public MainViewModel()
        {
            NavigateCommand = new RelayCommand<string>(nav => SelectedNav = nav);

            try
            {
                BuildPages();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"页面初始化失败：{ex.Message}\n\n{ex.StackTrace}", "初始化错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }

            StatusBarText = "就绪";
        }

        private void BuildPages()
        {
            _pages["临时文件清理"] = new TempCleanView { DataContext = new TempCleanViewModel() };
            _pages["磁盘空间分析"] = new DiskAnalysisView { DataContext = new DiskAnalysisViewModel() };
            _pages["重复文件检测"] = new DuplicateView { DataContext = new DuplicateViewModel() };
            _pages["浏览器缓存"] = new BrowserCacheView { DataContext = new BrowserCacheViewModel() };
            _pages["回收站清空"] = new RecycleBinView { DataContext = new RecycleBinViewModel() };
            _pages["文件搬家"] = new FileMoveView { DataContext = new FileMoveViewModel() };
            _pages["软件管理"] = new SoftwareView { DataContext = new SoftwareViewModel() };
        }

        private void NavigateTo(string nav)
        {
            try
            {
                if (_pages.TryGetValue(nav, out var page))
                {
                    CurrentPage = page;
                    StatusBarText = $"当前页面：{nav}";
                }
            }
            catch (Exception ex)
            {
                StatusBarText = $"导航失败：{ex.Message}";
                MessageBox.Show($"页面切换失败：{ex.Message}\n\n{ex.StackTrace}", "导航错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
