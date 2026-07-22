using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DiskCleaner.Helpers;
using DiskCleaner.Models;
using DiskCleaner.Services;

namespace DiskCleaner.ViewModels
{
    public class DiskAnalysisViewModel : ViewModelBase
    {
        private readonly DiskAnalyzer _analyzer = new DiskAnalyzer();
        private ObservableCollection<FileNode> _rootFolders;
        private FileNode _selectedFolder;
        private bool _isAnalyzing;
        private int _progress;
        private string _progressText;
        private long _totalSize;
        private CancellationTokenSource _cts;

        public ObservableCollection<FileNode> RootFolders
        {
            get => _rootFolders;
            set => Set(ref _rootFolders, value);
        }

        public FileNode SelectedFolder
        {
            get => _selectedFolder;
            set => Set(ref _selectedFolder, value);
        }

        public bool IsAnalyzing
        {
            get => _isAnalyzing;
            set => Set(ref _isAnalyzing, value);
        }

        public int Progress
        {
            get => _progress;
            set => Set(ref _progress, value);
        }

        public string ProgressText
        {
            get => _progressText;
            set => Set(ref _progressText, value);
        }

        public long TotalSize
        {
            get => _totalSize;
            set
            {
                Set(ref _totalSize, value);
                OnPropertyChanged(nameof(TotalSizeDisplay));
            }
        }

        public string TotalSizeDisplay => FileSizeFormatter.Format(TotalSize);

        public ICommand AnalyzeCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand OpenInExplorerCommand { get; }

        public DiskAnalysisViewModel()
        {
            AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(), () => !IsAnalyzing);
            CancelCommand = new RelayCommand(() => Cancel());
            OpenInExplorerCommand = new RelayCommand<FileNode>(node =>
            {
                if (node != null)
                    ExplorerHelper.OpenFolder(node.FullPath);
            });

            _analyzer.OnProgress = (pct, msg) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    Progress = pct < 0 ? Progress : pct;
                    ProgressText = msg;
                });
            };
        }

        private async Task AnalyzeAsync()
        {
            IsAnalyzing = true;
            Progress = 0;
            ProgressText = "正在分析 C 盘...";
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            RootFolders = new ObservableCollection<FileNode>();

            try
            {
                var root = System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                var folders = await _analyzer.AnalyzeDriveAsync(root, _cts.Token);
                TotalSize = 0;
                foreach (var f in folders)
                {
                    TotalSize += f.SizeBytes;
                    RootFolders.Add(f);
                }
                ProgressText = $"分析完成，C盘根目录共占用 {TotalSizeDisplay}";
            }
            catch (System.OperationCanceledException) { /* 用户取消 — 正常流程 */ }
            catch (System.Exception ex)
            {
                ProgressText = $"分析失败：{ex.Message}";
            }
            finally
            {
                IsAnalyzing = false;
                Progress = 100;
            }
        }

        private void Cancel()
        {
            _cts?.Cancel();
            ProgressText = "正在取消...";
        }
    }
}
