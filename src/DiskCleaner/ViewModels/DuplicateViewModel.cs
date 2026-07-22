using System.Collections.ObjectModel;
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
    public class DuplicateViewModel : ViewModelBase
    {
        private readonly DuplicateFinder _finder = new DuplicateFinder();
        private ObservableCollection<DuplicateGroup> _groups;
        private bool _isScanning;
        private bool _isCleaning;
        private int _progress;
        private string _progressText;
        private long _totalWaste;
        private long _selectedWaste;
        private string _scanPath;
        private long _minFileSizeMB = 1;
        private string _resultMessage;
        private bool _permanentDelete;
        private CancellationTokenSource _cts;
        private CancellationTokenSource _aiCts;

        public ObservableCollection<DuplicateGroup> Groups
        {
            get => _groups;
            set => Set(ref _groups, value);
        }

        public bool IsScanning
        {
            get => _isScanning;
            set => Set(ref _isScanning, value);
        }

        public bool IsCleaning
        {
            get => _isCleaning;
            set => Set(ref _isCleaning, value);
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

        public long TotalWaste
        {
            get => _totalWaste;
            set
            {
                Set(ref _totalWaste, value);
                OnPropertyChanged(nameof(TotalWasteDisplay));
            }
        }

        public long SelectedWaste
        {
            get => _selectedWaste;
            set
            {
                Set(ref _selectedWaste, value);
                OnPropertyChanged(nameof(SelectedWasteDisplay));
            }
        }

        public string TotalWasteDisplay => FileSizeFormatter.Format(TotalWaste);
        public string SelectedWasteDisplay => FileSizeFormatter.Format(SelectedWaste);

        public string ScanPath
        {
            get => _scanPath ??= System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            set => Set(ref _scanPath, value);
        }

        public long MinFileSizeMB
        {
            get => _minFileSizeMB;
            set => Set(ref _minFileSizeMB, value);
        }

        public string ResultMessage
        {
            get => _resultMessage;
            set => Set(ref _resultMessage, value);
        }

        public bool PermanentDelete
        {
            get => _permanentDelete;
            set => Set(ref _permanentDelete, value);
        }

        public ICommand ScanCommand { get; }
        public ICommand CleanCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand SelectAllCommand { get; }
        public ICommand BrowseCommand { get; }
        public ICommand AIAnalyzeCommand { get; }

        public DuplicateViewModel()
        {
            ScanCommand = new RelayCommand(async () => await ScanAsync(), () => !IsScanning && !IsCleaning);
            CleanCommand = new RelayCommand(async () => await CleanAsync(), () => !IsScanning && !IsCleaning && Groups?.Count > 0);
            CancelCommand = new RelayCommand(() => Cancel());
            SelectAllCommand = new RelayCommand(() =>
            {
                if (Groups == null) return;
                foreach (var g in Groups) g.IsSelected = true;
                UpdateSelectedWaste();
            });
            AIAnalyzeCommand = new RelayCommand(async () => await AIAnalyzeAsync(),
                () => !IsScanning && !IsCleaning && Groups?.Count > 0);
            BrowseCommand = new RelayCommand(() =>
            {
                var dialog = new System.Windows.Forms.FolderBrowserDialog
                {
                    SelectedPath = ScanPath,
                    Description = "选择要检测重复文件的目录"
                };
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    ScanPath = dialog.SelectedPath;
            });

            _finder.OnProgress = (pct, msg) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    Progress = pct < 0 ? Progress : pct;
                    ProgressText = msg;
                });
            };
        }

        private async Task ScanAsync()
        {
            if (!System.IO.Directory.Exists(ScanPath))
            {
                ResultMessage = "目录不存在";
                return;
            }

            IsScanning = true;
            Progress = 0;
            ResultMessage = "";
            Groups = new ObservableCollection<DuplicateGroup>();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _finder.MinFileSize = MinFileSizeMB * 1024 * 1024;

            try
            {
                var result = await _finder.FindDuplicatesAsync(ScanPath, _cts.Token);

                // 解除旧订阅，防止内存泄漏
                if (Groups != null)
                    foreach (var g in Groups)
                        g.PropertyChanged -= OnGroupSelectedChanged;

                Groups = new ObservableCollection<DuplicateGroup>(result);
                TotalWaste = 0;
                foreach (var g in Groups)
                {
                    TotalWaste += g.WasteBytes;
                    g.PropertyChanged += OnGroupSelectedChanged;
                }
                ResultMessage = $"检测完成，共 {Groups.Count} 组重复文件，可释放 {TotalWasteDisplay}";
            }
            catch (System.OperationCanceledException) { /* 用户取消 */ }
            catch (System.Exception ex)
            {
                ResultMessage = $"检测中断：{ex.Message}";
            }
            finally
            {
                IsScanning = false;
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                Progress = 100;
            }
        }

        private void UpdateSelectedWaste()
        {
            long total = 0;
            foreach (var g in Groups)
                if (g.IsSelected) total += g.WasteBytes;
            SelectedWaste = total;
        }

        private void OnGroupSelectedChanged(object s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DuplicateGroup.IsSelected))
                UpdateSelectedWaste();
        }

        private async Task CleanAsync()
        {
            var selected = new System.Collections.Generic.List<DuplicateGroup>();
            foreach (var g in Groups)
            {
                if (g.IsSelected)
                {
                    // 确保至少有一个文件被保留
                    var deleteFiles = g.Files.Where(f => !f.KeepThis).ToList();
                    if (deleteFiles.Count > 0)
                        selected.Add(g);
                }
            }

            if (selected.Count == 0)
            {
                ResultMessage = "请先选择要清理的重复文件组，并确保每组保留至少一个文件";
                return;
            }

            long totalDelete = 0;
            foreach (var g in selected)
                totalDelete += g.Files.Where(f => !f.KeepThis).Sum(f => g.FileSize);

            var confirm = MessageBox.Show(
                $"即将删除 {selected.Count} 组重复文件中的冗余副本，共 {FileSizeFormatter.Format(totalDelete)}。\n\n" +
                (PermanentDelete ? "⚠️ 永久删除不可恢复！" : "文件将移至回收站。") +
                "\n\n确认继续？",
                "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            IsCleaning = true;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            long freed = 0;
            int deleted = 0;

            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                {
                    foreach (var g in selected)
                    {
                        _cts.Token.ThrowIfCancellationRequested();
                        var deleteFiles = g.Files.Where(f => !f.KeepThis).ToList();
                        foreach (var f in deleteFiles)
                        {
                            try
                            {
                                if (System.IO.File.Exists(f.FilePath))
                                {
                                    if (PermanentDelete)
                                    {
                                        System.IO.File.Delete(f.FilePath);
                                    }
                                    else
                                    {
                                        // 走回收站；失败则跳过，不静默永久删除
                                        if (!NativeMethods.SendToRecycleBin(f.FilePath, out var err))
                                        {
                                            Logger.Warning($"回收站删除失败 [{f.FilePath}]: 0x{err:X}");
                                            continue;
                                        }
                                    }
                                    freed += g.FileSize;
                                    deleted++;
                                }
                            }
                            catch { /* 文件可能被占用 */ }
                        }
                    }
                }, _cts.Token);

                // 刷新列表
                var remaining = new System.Collections.Generic.List<DuplicateGroup>();
                foreach (var g in Groups)
                {
                    var stillExist = g.Files.Where(f => System.IO.File.Exists(f.FilePath)).ToList();
                    if (stillExist.Count > 1)
                    {
                        g.Files.Clear();
                        foreach (var f in stillExist)
                            g.Files.Add(f);
                        remaining.Add(g);
                    }
                }
                Groups = new ObservableCollection<DuplicateGroup>(remaining);

                ResultMessage = $"清理完成！释放 {FileSizeFormatter.Format(freed)}，删除 {deleted} 个文件";
                TotalWaste = 0;
                foreach (var g in Groups) TotalWaste += g.WasteBytes;
                UpdateSelectedWaste();
            }
            catch (System.OperationCanceledException) { /* 用户取消 */ }
            catch (System.Exception ex)
            {
                ResultMessage = $"清理中断：{ex.Message}";
            }
            finally
            {
                IsCleaning = false;
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        private void Cancel()
        {
            _cts?.Cancel();
            _aiCts?.Cancel();
            ProgressText = "正在取消...";
        }

        private async Task AIAnalyzeAsync()
        {
            var settings = SettingsManager.Load();
            if (string.IsNullOrEmpty(settings.ApiKey))
            {
                System.Windows.MessageBox.Show(
                    "请先在左侧底部「⚙️ AI 设置」中配置 API Key 和模型。",
                    "未配置 AI", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            // 收集所有安全等级为 Unknown 的文件
            var unknownFiles = new System.Collections.Generic.List<string>();
            foreach (var g in Groups)
                foreach (var f in g.Files)
                {
                    var si = FileSafetyAnalyzer.Analyze(f.FilePath);
                    if (si.Level == FileSafetyLevel.Unknown)
                        unknownFiles.Add(f.FilePath);
                }

            if (unknownFiles.Count == 0)
            {
                ResultMessage = "所有文件已被本地规则识别，无需 AI 分析";
                return;
            }

            IsCleaning = true;
            ProgressText = $"正在用 AI 分析 {unknownFiles.Count} 个未知文件...";

            _aiCts?.Dispose();
            _aiCts = new CancellationTokenSource();

            try
            {
                var results = await AIFileAnalyzer.AnalyzeBatchAsync(unknownFiles, settings, _aiCts.Token);
                int updated = ApplyAIResults(results);
                ResultMessage = $"AI 分析完成：{updated}/{results.Count} 个文件已更新安全评级";
            }
            catch (System.Exception ex)
            {
                ResultMessage = $"AI 分析失败：{ex.Message}";
            }
            finally
            {
                IsCleaning = false;
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        private int ApplyAIResults(System.Collections.Generic.List<AIAnalysisResult> results)
        {
            int count = 0;
            foreach (var r in results)
            {
                if (!r.Success) continue;
                // 更新 DuplicateFile 的安全属性
                foreach (var g in Groups)
                    foreach (var f in g.Files)
                        if (f.FilePath == r.FilePath)
                        {
                            f.RaisePropertyChanged(nameof(f.SafetyIcon));
                            f.RaisePropertyChanged(nameof(f.SafetyTooltip));
                            count++;
                        }
            }
            return count;
        }
    }
}
