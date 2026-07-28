using System;
using System.Collections.ObjectModel;
using System.IO;
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
        private ObservableCollection<DriveItem> _drives;
        private DriveItem _selectedDrive;
        private FileNode _selectedFolder;
        private bool _isAnalyzing;
        private int _progress;
        private string _progressText;
        private long _totalSize;
        private CancellationTokenSource _cts;

        /// <summary>可选盘符（仅已就绪的固定本地盘）。</summary>
        public ObservableCollection<DriveItem> Drives
        {
            get => _drives;
            set => Set(ref _drives, value);
        }

        /// <summary>当前选中的盘符；为 null 时 AnalyzeAsync 退化为系统盘。</summary>
        public DriveItem SelectedDrive
        {
            get => _selectedDrive;
            set => Set(ref _selectedDrive, value);
        }

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
            set { Set(ref _isAnalyzing, value); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
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

        private string _analyzeButtonText = "开始分析";
        public string AnalyzeButtonText
        {
            get => _analyzeButtonText;
            set => Set(ref _analyzeButtonText, value);
        }

        public ICommand AnalyzeCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand OpenInExplorerCommand { get; }

        public DiskAnalysisViewModel()
        {
            LoadDrives();
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

        /// <summary>枚举已就绪的固定本地盘，预选系统盘。任一异常降级为仅 C 盘（review_DiskCleanerPro.md：多盘分析）。</summary>
        private void LoadDrives()
        {
            try
            {
                var sysRoot = Path.GetPathRoot(Environment.SystemDirectory);
                var list = new ObservableCollection<DriveItem>();
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                    var root = d.RootDirectory.FullName;
                    var label = !string.IsNullOrWhiteSpace(d.VolumeLabel)
                        ? $"{d.Name.TrimEnd('\\')} ({d.VolumeLabel})"
                        : d.Name.TrimEnd('\\');
                    var item = new DriveItem { Root = root, Label = label };
                    list.Add(item);
                    if (string.Equals(root.TrimEnd('\\'), sysRoot?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        SelectedDrive = item;
                }
                Drives = list;
                if (SelectedDrive == null && list.Count > 0) SelectedDrive = list[0];
            }
            catch
            {
                Drives = new ObservableCollection<DriveItem> { new DriveItem { Root = "C:\\", Label = "C盘" } };
                SelectedDrive = Drives[0];
            }
        }

        private async Task AnalyzeAsync()
        {
            IsAnalyzing = true;
            // 立即让“开始分析”按钮进入禁用态（RelayCommand 依赖 CommandManager.RequerySuggested，
            // 仅属性变化不会自动刷新按钮可用性，需主动触发一次全局重查）
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            Progress = 0;
            var drive = SelectedDrive ?? new DriveItem { Root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", Label = "C盘" };
            ProgressText = $"正在分析 {drive.Label}...";
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            RootFolders = new ObservableCollection<FileNode>();

            try
            {
                var root = drive.Root;
                var folders = await _analyzer.AnalyzeDriveAsync(root, _cts.Token);
                TotalSize = 0;
                foreach (var f in folders)
                {
                    TotalSize += f.SizeBytes;
                    RootFolders.Add(f);
                }
                ProgressText = $"分析完成，{drive.Label}根目录共占用 {TotalSizeDisplay}";
                AnalyzeButtonText = "重新分析";
            }
            catch (System.OperationCanceledException)
            {
                ProgressText = "已取消分析";   // 用户取消 — 正常流程
            }
            catch (System.Exception ex)
            {
                ProgressText = $"分析失败：{ex.Message}";
            }
            finally
            {
                IsAnalyzing = false;
                Progress = 100;
                // 关键：扫描结束后（无论成功/失败/取消）主动刷新命令可用性，
                // 否则“开始分析”按钮要等用户点击/移动鼠标触发 RequerySuggested 才会恢复可点击
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        private void Cancel()
        {
            _cts?.Cancel();
            ProgressText = "正在取消...";
        }
    }

    /// <summary>磁盘分析页可选的盘符项（仅用于 UI 绑定，底层 DiskAnalyzer 支持任意盘根）。</summary>
    public class DriveItem
    {
        public string Root { get; set; }
        public string Label { get; set; }
        public override string ToString() => Label;
    }
}
