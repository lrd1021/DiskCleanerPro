using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DiskCleaner.Helpers;
using DiskCleaner.Models;
using DiskCleaner.Services;

namespace DiskCleaner.ViewModels
{
    public class FileMoveViewModel : ViewModelBase
    {
        private readonly FileMoverService _service = new FileMoverService();
        private ObservableCollection<DirectorySizeInfo> _directories;
        private ObservableCollection<MoveTask> _tasks;
        private bool _isScanning;
        private bool _isMoving;
        private int _progress;
        private string _progressText;
        private long _minDirSizeMB = 500;
        private string _scanPath;
        private string _targetDrive;
        private bool _createJunction = true;
        private string _resultMessage;
        private CancellationTokenSource _cts;

        // 排序状态
        private string _sortColumnHeader = "大小";
        private ListSortDirection _sortDirection = ListSortDirection.Descending;

        public ObservableCollection<DirectorySizeInfo> Directories
        {
            get => _directories;
            set => Set(ref _directories, value);
        }

        public ObservableCollection<MoveTask> Tasks
        {
            get => _tasks;
            set => Set(ref _tasks, value);
        }

        public bool IsScanning
        {
            get => _isScanning;
            set { Set(ref _isScanning, value); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
        }

        public bool IsMoving
        {
            get => _isMoving;
            set { Set(ref _isMoving, value); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
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

        /// <summary>阈值（MB）。默认 500MB。</summary>
        public long MinDirSizeMB
        {
            get => _minDirSizeMB;
            set => Set(ref _minDirSizeMB, value);
        }

        public string ScanPath
        {
            get => _scanPath ??= System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            set => Set(ref _scanPath, value);
        }

        public string TargetDrive
        {
            get => _targetDrive;
            set => Set(ref _targetDrive, value);
        }

        /// <summary>是否在建目录 junction 保持原位可用（默认 true）。</summary>
        public bool CreateJunction
        {
            get => _createJunction;
            set => Set(ref _createJunction, value);
        }

        public string ResultMessage
        {
            get => _resultMessage;
            set => Set(ref _resultMessage, value);
        }

        public ObservableCollection<string> AvailableDrives { get; } = new ObservableCollection<string>();

        public string CurrentSortColumnHeader
        {
            get => _sortColumnHeader;
            set => Set(ref _sortColumnHeader, value);
        }

        public ListSortDirection CurrentSortDirection
        {
            get => _sortDirection;
            set => Set(ref _sortDirection, value);
        }

        public ICommand ScanCommand { get; }
        public ICommand MoveCommand { get; }
        public ICommand MoveBackCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand BrowseCommand { get; }
        public ICommand SortCommand { get; }

        public FileMoveViewModel()
        {
            ScanCommand = new RelayCommand(async () => await ScanAsync(), () => !IsScanning && !IsMoving);
            MoveCommand = new RelayCommand(async () => await MoveAsync(), () => !IsScanning && !IsMoving);
            MoveBackCommand = new RelayCommand<DirectorySizeInfo>(async (d) => await MoveBackAsync(d),
                (d) => d != null && d.IsMoved && !IsScanning && !IsMoving);
            CancelCommand = new RelayCommand(() => Cancel());
            BrowseCommand = new RelayCommand(() =>
            {
                var dialog = new System.Windows.Forms.FolderBrowserDialog
                {
                    SelectedPath = ScanPath,
                    Description = "选择要扫描的目录（建议选择用户目录）"
                };
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    ScanPath = dialog.SelectedPath;
            });
            SortCommand = new RelayCommand<string>(header => Sort(header));

            _service.OnProgress = (pct, msg) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    Progress = pct < 0 ? Progress : pct;
                    ProgressText = msg;
                });
            };

            Tasks = new ObservableCollection<MoveTask>();
            LoadAvailableDrives();
        }

        private void LoadAvailableDrives()
        {
            AvailableDrives.Clear();
            foreach (var drive in _service.GetAvailableTargetDrives())
                AvailableDrives.Add(drive.Name);
            if (AvailableDrives.Count > 0)
                TargetDrive = AvailableDrives[0];
        }

        private async Task ScanAsync()
        {
            if (!System.IO.Directory.Exists(ScanPath))
            {
                ResultMessage = "目录不存在";
                return;
            }
            if (MinDirSizeMB <= 0)
            {
                ResultMessage = "阈值必须大于 0 MB";
                return;
            }

            IsScanning = true;
            Progress = 0;
            ResultMessage = "";
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            Directories = new ObservableCollection<DirectorySizeInfo>();

            try
            {
                var minBytes = MinDirSizeMB * 1024 * 1024;
                var list = await _service.ScanLargeDirectoriesAsync(ScanPath, minBytes, _cts.Token);

                // 标记已搬家的目录
                var manifest = _service.LoadMovedManifest();
                foreach (var d in list)
                {
                    if (manifest.TryGetValue(d.DirectoryPath, out var target))
                    {
                        d.IsMoved = true;
                        d.MovedToPath = target;
                    }
                }

                Directories = new ObservableCollection<DirectorySizeInfo>(list);
                ApplySort();
                ResultMessage = $"找到 {list.Count} 个大于 {MinDirSizeMB}MB 的目录" +
                                (AvailableDrives.Count == 0 ? "（当前无其它本地盘，无法进行搬家）" : "");
            }
            catch (System.OperationCanceledException) { /* 用户取消 */ }
            catch (System.Exception ex)
            {
                ResultMessage = $"扫描中断：{ex.Message}";
            }
            finally
            {
                IsScanning = false;
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                Progress = 100;
            }
        }

        private async Task MoveAsync()
        {
            if (Directories == null || Directories.Count == 0)
            {
                ResultMessage = "请先扫描目录";
                return;
            }
            if (string.IsNullOrEmpty(TargetDrive))
            {
                ResultMessage = "请选择目标盘（需要除 C 盘外的本地固定盘）";
                return;
            }

            var selected = new System.Collections.Generic.List<DirectorySizeInfo>();
            foreach (var d in Directories)
                if (d.IsSelected && !d.IsMoved) selected.Add(d);

            if (selected.Count == 0)
            {
                ResultMessage = "请勾选要搬移的目录";
                return;
            }

            long totalSize = 0;
            foreach (var d in selected) totalSize += d.SizeBytes;

            string targetDir = System.IO.Path.Combine(TargetDrive, "MovedFromC");
            var confirm = MessageBox.Show(
                $"即将搬移 {selected.Count} 个目录（共 {FileSizeFormatter.Format(totalSize)}）到 {targetDir}\n\n" +
                (CreateJunction
                    ? "将在原位创建 junction，依赖这些目录的应用仍可正常使用"
                    : "不创建 junction，原路径将失效，依赖这些目录的应用可能报错") +
                "\n\n确认继续？",
                "搬移确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            IsMoving = true;
            Progress = 0;
            Tasks.Clear();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            long totalMoved = 0;
            int successCount = 0;

            try
            {
                for (int i = 0; i < selected.Count; i++)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    Progress = (int)((float)i / selected.Count * 100);
                    ProgressText = $"搬移 {i + 1}/{selected.Count}：{selected[i].DirectoryName}";

                    var task = await _service.MoveDirectoryAsync(selected[i], TargetDrive, CreateJunction, _cts.Token);
                    Tasks.Add(task);
                    if (task.Status == MoveTask.MoveStatus.Completed)
                    {
                        totalMoved += task.FileSizeBytes;
                        successCount++;
                        selected[i].IsMoved = CreateJunction;   // 仅 junction 模式才算“已搬家可还原”
                        selected[i].MovedToPath = task.TargetPath;
                    }
                }

                ResultMessage = $"搬移完成！成功 {successCount}/{selected.Count} 个目录，共释放 {FileSizeFormatter.Format(totalMoved)}";
                LoadAvailableDrives();
            }
            catch (System.OperationCanceledException) { /* 用户取消 */ }
            catch (System.Exception ex)
            {
                ResultMessage = $"搬移中断：{ex.Message}";
            }
            finally
            {
                IsMoving = false;
                Progress = 100;
            }
        }

        private async Task MoveBackAsync(DirectorySizeInfo dir)
        {
            if (dir == null || !dir.IsMoved) return;
            IsMoving = true;
            try
            {
                var task = await _service.MoveBackAsync(dir, CancellationToken.None);
                Tasks.Add(task);
                if (task.Status == MoveTask.MoveStatus.Completed)
                {
                    dir.IsMoved = false;
                    dir.MovedToPath = null;
                    ResultMessage = $"已搬回：{dir.DirectoryName}";
                }
                else
                {
                    ResultMessage = task.StatusText ?? "搬回失败";
                }
            }
            finally
            {
                IsMoving = false;
            }
        }

        private void Cancel()
        {
            _cts?.Cancel();
            ProgressText = "正在取消...";
        }

        // ── 排序 ──

        public void Sort(string columnHeader)
        {
            if (CurrentSortColumnHeader == columnHeader)
                CurrentSortDirection = CurrentSortDirection == ListSortDirection.Ascending
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending;
            else
            {
                CurrentSortColumnHeader = columnHeader;
                CurrentSortDirection = ListSortDirection.Ascending;
            }
            ApplySort();
        }

        private void ApplySort()
        {
            if (Directories == null || string.IsNullOrEmpty(CurrentSortColumnHeader)) return;
            IOrderedEnumerable<DirectorySizeInfo> ordered;
            switch (CurrentSortColumnHeader)
            {
                case "目录":
                    ordered = CurrentSortDirection == ListSortDirection.Ascending
                        ? Directories.OrderBy(d => d.DirectoryName, StringComparer.OrdinalIgnoreCase)
                        : Directories.OrderByDescending(d => d.DirectoryName, StringComparer.OrdinalIgnoreCase);
                    break;
                case "大小":
                    ordered = CurrentSortDirection == ListSortDirection.Ascending
                        ? Directories.OrderBy(d => d.SizeBytes)
                        : Directories.OrderByDescending(d => d.SizeBytes);
                    break;
                case "文件数":
                    ordered = CurrentSortDirection == ListSortDirection.Ascending
                        ? Directories.OrderBy(d => d.FileCount)
                        : Directories.OrderByDescending(d => d.FileCount);
                    break;
                case "状态":
                    ordered = CurrentSortDirection == ListSortDirection.Ascending
                        ? Directories.OrderBy(d => d.StatusDisplay)
                        : Directories.OrderByDescending(d => d.StatusDisplay);
                    break;
                default:
                    return;
            }
            Directories = new ObservableCollection<DirectorySizeInfo>(ordered);
        }
    }
}
