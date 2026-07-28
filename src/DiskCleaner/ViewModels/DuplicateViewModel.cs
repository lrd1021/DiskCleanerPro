using System;
using System.Collections.Generic;
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
        private int _liveGroupCount;
        private long _totalWaste;
        private long _selectedWaste;
        private string _scanPath;
        private long _minFileSizeMB = 1;
        private string _resultMessage;
        private bool _permanentDelete;
        private CancellationTokenSource _cts;
        private CancellationTokenSource _aiCts;

        // 实时回传节流：后台线程只把发现的重复组累积到 _pendingGroups，
        // 由 _liveTimer（UI 线程驱动）每 200ms 批量 Add 到 Groups，
        // 把 Dispatcher 调度次数从“每组一次”降到“每 200ms 一次”，
        // 避免重复组极多（上千组）时 UI 线程被海量回调积压而表现为“越往后越卡”。
        private readonly object _pendingLock = new object();
        private readonly List<DuplicateGroup> _pendingGroups = new List<DuplicateGroup>();
        private System.Windows.Threading.DispatcherTimer _liveTimer;

        // 实时目录滚动列表：后台每枚举一个文件都把其所在目录（取 Path.GetDirectoryName）写入 _latestScanDir
        // （仅存最新，无锁开销），由 _scanLogTimer（UI 线程,150ms）节流把“最近切换到的目录”插入 ScanLog
        // （最新在顶部、上限 60 条、目录级去重），进度条下方以“正在扫描的目录”列表实时滚动展示。
        // 用目录而非单文件名的原因：①目录数远少于文件数，更能反映“扫描进行到哪”；②避免海量小文件聚集在单一
        // 测试目录（如 DiskCleanerSmoke_xxx\deep\L0\…）时整屏只闪同一个文件名；③去重后不会因单目录刷屏。
        private string _latestScanDir;
        private readonly ObservableCollection<string> _scanLog = new ObservableCollection<string>();
        private System.Windows.Threading.DispatcherTimer _scanLogTimer;

        public System.Collections.ObjectModel.ObservableCollection<string> ScanLog => _scanLog;

        public ObservableCollection<DuplicateGroup> Groups
        {
            get => _groups;
            set => Set(ref _groups, value);
        }

        public bool IsScanning
        {
            get => _isScanning;
            set { Set(ref _isScanning, value); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
        }

        public bool IsCleaning
        {
            get => _isCleaning;
            set { Set(ref _isCleaning, value); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
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

        public int LiveGroupCount
        {
            get => _liveGroupCount;
            set => Set(ref _liveGroupCount, value);
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
        public ICommand DeselectAllCommand { get; }
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
                UpdateTotalAndSelectedWaste();
            });
            DeselectAllCommand = new RelayCommand(() =>
            {
                if (Groups == null) return;
                foreach (var g in Groups) g.IsSelected = false;
                UpdateTotalAndSelectedWaste();
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
                    // 进度条始终确定（不再用“来回滚动”的不确定动画）：收集阶段由 DuplicateFinder 传入软曲线进度值，
                    // 阶段A/B 传真实百分比，全程单调不回退。
                    Progress = pct < 0 ? 0 : pct;
                    ProgressText = msg;
                });
            };

            // 实时目录滚动：后台每枚举一个文件，取其所在目录（Path.GetDirectoryName）写到 _latestScanDir
            // （仅存最新，无锁），UI 节流定时器再把“最近切换到的目录”写入 ScanLog 供下方列表展示。
            _finder.OnFileScanned = path =>
            {
                if (string.IsNullOrEmpty(path)) return;
                _latestScanDir = System.IO.Path.GetDirectoryName(path);
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
            LiveGroupCount = 0;
            ResultMessage = "";
            // 解除上一次扫描的订阅，防止内存泄漏
            if (Groups != null)
                foreach (var g in Groups)
                    g.PropertyChanged -= OnGroupSelectedChanged;
            Groups = new ObservableCollection<DuplicateGroup>();
            TotalWaste = 0;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _finder.MinFileSize = MinFileSizeMB * 1024 * 1024;

            // 实时回传：每发现一组重复只在后台累积，由 DispatcherTimer 在 UI 线程批量 flush，
            // 既保留“结果随扫描逐步出现”的直观反馈，又避免每组一次 Dispatcher 调用在重复组极多时压垮 UI 线程。
            _pendingGroups.Clear();
            _finder.OnGroupFound = g =>
            {
                lock (_pendingLock) _pendingGroups.Add(g);
            };

            _liveTimer?.Stop();
            _liveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _liveTimer.Tick += OnLiveTick;
            _liveTimer.Start();

            // 目录滚动列表节流：150ms 把“最近切换到的目录”写入 ScanLog（最新在顶部、上限 60 条、目录级去重）
            _scanLogTimer?.Stop();
            _scanLog.Clear();
            _latestScanDir = null;
            _scanLogTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _scanLogTimer.Tick += OnScanLogTick;
            _scanLogTimer.Start();

            try
            {
                var result = await _finder.FindDuplicatesAsync(ScanPath, _cts.Token);

                // 停止实时回传定时器（UI 线程，stop 后无遗留 Tick），避免其后异步 flush 与整体替换冲突。
                _liveTimer?.Stop();
                _pendingGroups.Clear();

                // 扫描结束：用完整结果列表一次性替换。
                // 实时回传的组已在扫描过程中逐组显示（用户已看到“在进行”），
                // 此处用完整桶重建，保证成员齐全、WasteBytes 与排序准确；用户此刻尚未勾选，替换安全。
                if (Groups != null)
                    foreach (var g in Groups)
                        g.PropertyChanged -= OnGroupSelectedChanged;
                Groups = new ObservableCollection<DuplicateGroup>(result);
                foreach (var g in Groups)
                    g.PropertyChanged += OnGroupSelectedChanged;
                UpdateTotalAndSelectedWaste();
                LiveGroupCount = Groups.Count;

                ResultMessage = $"检测完成，共 {Groups.Count} 组重复文件，可释放 {TotalWasteDisplay}";
            }
            catch (System.OperationCanceledException) { /* 用户取消 */ }
            catch (System.Exception ex)
            {
                ResultMessage = $"检测中断：{ex.Message}";
            }
            finally
            {
                _liveTimer?.Stop();
                _scanLogTimer?.Stop();
                // 扫描结束保留 ScanLog 末态，让用户能看到刚扫过的目录；不再追加新项。
                IsScanning = false;
                Progress = 100;
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        /// <summary>
        /// 由 DispatcherTimer 在 UI 线程触发：把后台累积的重复组批量加入结果列表。
        /// 每 200ms 执行一次，把 Dispatcher 调度次数从“每组一次”降到约“每 200ms 一次”，
        /// 避免重复组极多时 UI 线程被海量回调积压而卡顿。
        /// </summary>
        private void OnLiveTick(object sender, EventArgs e)
        {
            List<DuplicateGroup> batch;
            lock (_pendingLock)
            {
                if (_pendingGroups.Count == 0) return;
                batch = new List<DuplicateGroup>(_pendingGroups);
                _pendingGroups.Clear();
            }
            foreach (var g in batch)
            {
                g.PropertyChanged += OnGroupSelectedChanged;
                Groups.Add(g);
                TotalWaste += g.WasteBytes;
            }
            LiveGroupCount = Groups.Count;
        }

        /// <summary>
        /// 由 _scanLogTimer 在 UI 线程触发（每 150ms）：把后台最新扫到的目录写入 ScanLog。
        /// 仅当目录相对上一条发生变化时才插入到顶部（目录级去重，避免单目录海量文件刷屏），
        /// 并把列表裁剪到 60 条——最新在顶部，旧的向下滚动消失，形成“实时滚动”的目录扫描日志。
        /// 显示相对 ScanPath 的目录路径，更短更易读。
        /// </summary>
        private void OnScanLogTick(object sender, EventArgs e)
        {
            var dir = _latestScanDir;
            if (string.IsNullOrEmpty(dir)) return;
            // 相对 ScanPath 显示，更短（如 \AppData\Local\Temp\...）
            var rel = GetRelativeDir(dir);
            if (_scanLog.Count > 0 && _scanLog[0] == rel) return;   // 目录未切换则不重复插入
            _scanLog.Insert(0, rel);
            while (_scanLog.Count > 60) _scanLog.RemoveAt(_scanLog.Count - 1);
        }

        private string GetRelativeDir(string dir)
        {
            var root = ScanPath?.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            if (!string.IsNullOrEmpty(root) && dir.Length > root.Length &&
                dir.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                (dir[root.Length] == System.IO.Path.DirectorySeparatorChar || dir[root.Length] == System.IO.Path.AltDirectorySeparatorChar))
            {
                return dir.Substring(root.Length).TrimStart(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            }
            return dir;
        }

        private void UpdateTotalAndSelectedWaste()
        {
            long total = 0, selected = 0;
            foreach (var g in Groups)
            {
                total += g.WasteBytes;
                if (g.IsSelected) selected += g.WasteBytes;
            }
            TotalWaste = total;
            SelectedWaste = selected;
        }

        private void OnGroupSelectedChanged(object s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DuplicateGroup.IsSelected) ||
                e.PropertyName == nameof(DuplicateGroup.WasteBytes))
            {
                UpdateTotalAndSelectedWaste();
            }
        }

        private async Task CleanAsync()
        {
            var selected = new System.Collections.Generic.List<DuplicateGroup>();
            foreach (var g in Groups)
            {
                if (g.IsSelected)
                {
                    // 组内勾选「保留」的文件留下，其余（未勾选）副本删除；允许同组保留多个或全部保留
                    var deleteFiles = g.Files.Where(f => !f.KeepThis).ToList();
                    if (deleteFiles.Count > 0)
                        selected.Add(g);
                }
            }

            if (selected.Count == 0)
            {
                ResultMessage = "请先勾选要清理的重复文件组";
                return;
            }

            long totalDelete = 0;
            int criticalSkipped = 0;
            foreach (var g in selected)
            {
                foreach (var f in g.Files.Where(f => !f.KeepThis))
                {
                    if (f.IsCritical)
                    {
                        criticalSkipped++;          // 关键文件强制保留，永不删除
                        continue;
                    }
                    totalDelete += g.FileSize;
                }
            }

            var warnNote = criticalSkipped > 0
                ? $"\n\n已自动跳过 {criticalSkipped} 个系统/程序必需文件（锁定保留，不可删除）。"
                : "";

            var confirm = MessageBox.Show(
                $"即将删除 {selected.Count} 组重复文件中的冗余副本，共 {FileSizeFormatter.Format(totalDelete)}。\n\n" +
                (PermanentDelete ? "永久删除不可恢复！" : "文件将移至回收站。") +
                warnNote +
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
                // 1) 收集待删除文件（仅做 File.Exists 检查，不触碰 Shell，可在后台线程安全执行）
                //    关键文件（IsCritical）即使被标为删除也强制跳过，作为最后一道兜底（UI 已禁用其复选框）。
                var recycled = new List<(string path, long size)>();
                var permanent = new List<(string path, long size)>();
                foreach (var g in selected)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    foreach (var f in g.Files.Where(x => !x.KeepThis && !x.IsCritical))
                    {
                        if (System.IO.File.Exists(f.FilePath))
                        {
                            var entry = (f.FilePath, g.FileSize);
                            if (PermanentDelete) permanent.Add(entry);
                            else recycled.Add(entry);
                        }
                    }
                }

                // 2) 永久删除：逐文件 File.Delete（不依赖 STA，直接在后台线程执行，安全）
                if (permanent.Count > 0)
                {
                    await System.Threading.Tasks.Task.Run(() =>
                    {
                        foreach (var (path, size) in permanent)
                        {
                            _cts.Token.ThrowIfCancellationRequested();
                            try
                            {
                                System.IO.File.Delete(path);
                                freed += size;
                                deleted++;
                            }
                            catch { /* 文件可能被占用 */ }
                        }
                    }, _cts.Token);
                }

                // 3) 回收站：SHFileOperation 必须在 STA + 消息泵线程执行。
                //    SendToRecycleBinBatch 内部自建 STA 线程 + 消息泵，可从任意线程调用，
                //    避免旧写法在 MTA 线程池上调用 SHFileOperation 静默失败（文件既没进回收站也没删）。
                if (recycled.Count > 0)
                {
                    var paths = recycled.Select(r => r.path).ToList();
                    var failed = await System.Threading.Tasks.Task.Run(() =>
                        NativeMethods.SendToRecycleBinOnUIThread(paths,
                            onProgress: (p, t) => ProgressText = $"正在移入回收站：{p}/{t}",
                            onBatch: (b, t) => ProgressText = $"正在移入回收站（第 {b}/{t} 批）"),
                        _cts.Token);

                    // 失败候选需 File.Exists 复核：仍存在的才算真的没删成功
                    var stillExist = new System.Collections.Generic.HashSet<string>();
                    foreach (var p in failed)
                        if (System.IO.File.Exists(p)) stillExist.Add(p);

                    var okPaths = new List<string>();
                    foreach (var (path, size) in recycled)
                    {
                        if (!stillExist.Contains(path))
                        {
                            freed += size;
                            deleted++;
                            okPaths.Add(path);
                        }
                    }
                    if (okPaths.Count > 0)
                        RecycleBinManager.SourceTracker.Record(okPaths, "重复文件清理");
                }

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
                UpdateTotalAndSelectedWaste();
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
                // 用 MessageBoxHelper（带 owner）避免弹窗被主窗口遮挡而“看不到反应”
                MessageBoxHelper.Show(
                    "请先在左侧底部「AI 设置」中配置 API Key 和模型。",
                    "未配置 AI", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 仅分析“用户勾选的重复组”中的文件（符合“勾选后分析”的心智）；
            // 且只把本地无法判断（Unknown）或需谨慎（Caution）的文件交给 AI，
            // 本地已明确 Safe/Danger 的不必消耗 API。
            var candidates = new System.Collections.Generic.List<string>();
            int checkedGroups = 0;
            foreach (var g in Groups)
            {
                if (!g.IsSelected) continue;
                checkedGroups++;
                foreach (var f in g.Files)
                {
                    var si = FileSafetyAnalyzer.Analyze(f.FilePath);
                    if (si.Level == FileSafetyLevel.Unknown || si.Level == FileSafetyLevel.Caution)
                        candidates.Add(f.FilePath);
                }
            }

            if (checkedGroups == 0)
            {
                MessageBoxHelper.Show(
                    "请先勾选要分析的重复组（左侧复选框），再点击「AI 分析」。",
                    "未选择重复组", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (candidates.Count == 0)
            {
                MessageBoxHelper.Show(
                    "勾选的重复文件都已由本地规则识别（临时/缓存目录中的文件判为可安全删除，系统关键文件判为不建议删除），AI 无需进一步分析。\n\n如需让 AI 重新评估，请选择其它类型的重复文件。",
                    "无需 AI 分析", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            IsCleaning = true;
            ProgressText = $"正在用 AI 分析 {candidates.Count} 个文件...";

            _aiCts?.Dispose();
            _aiCts = new CancellationTokenSource();

            try
            {
                var results = await AIFileAnalyzer.AnalyzeBatchAsync(candidates, settings, _aiCts.Token);
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
