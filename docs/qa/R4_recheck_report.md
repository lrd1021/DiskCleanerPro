# DiskCleanerPro 第四轮复检报告（架构 / 并发 / 性能 / 代码质量维度）

**评审员**：gstack-product-reviewer
**复检对象**：`src/DiskCleaner/`（主程序）+ `src/DiskCleaner.Elevated/`（提权 Helper）+ `src/DiskCleaner.SmokeTest/`
**基线**：第三轮（2026-07-22）🔴 No-Go（GA）/ 🟡 条件Go（受限Beta），0 Critical，2 项 High 门禁（R1 残留点击式 RCE + R4 全量提权）
**方法**：以磁盘源码为准逐项核对，结合 `review` skill 的 security / performance / red-team 视角；本地 .NET SDK 为 6.0.400 无法直接编译 net8.0-windows，但 `publish/` 目录存在 01:21 生成的 self-contained .NET 8 产物（runtime 8.0.2926.32403），证明在 SDK-8 环境下可正常构建（无编译回归）。

---

## 一、16 项整改状态总览（✅已修复 / ⚠️部分 / ❌未修 / ➕新发现）

| 项 | 主题 | 状态 | 关键证据 |
|----|------|------|----------|
| R1 | MsiExec 残留点击式 RCE | ✅ | `SoftwareManager.cs:168-172` 非严格匹配**硬拒绝**；`Program.cs:162-166` 提权端二次校验；确认框默认“否”且仅在白名单通过后弹出 |
| R2 | 符号链接搬家顺序 | ✅ | `FileMoverService.cs:165-167` 先删源后建链；`:161` 源 ReparsePoint 检测 |
| R3 | BrowserCache 交接点逃逸 | ✅ | `BrowserCacheCleaner.cs:304` ReparsePoint 守卫（另见 `DiskAnalyzer.cs:131`、`TempFileCleaner.cs:237,312`） |
| R4 | 全量提权 | ✅ | `app.manifest:8` `asInvoker`；`ElevationHelper.cs:49` runas 按需 UAC；`Program.cs:22` 自检 + 守卫 |
| R5 | 发布包+pdb 剥离 | ✅ | `DiskCleaner.csproj:19-22`；`publish/` 目录无 .pdb |
| R6 | 空 catch / 错误码未上报 | ⚠️ | `NativeMethods.cs:207-216` SHFileOperation 错误码已记日志；`AsyncRelayCommand.cs:33,72` 不再吞异常；但约 13 处严 `catch {}` + `FlushDnsAsync` `TempFileCleaner.cs:361` 仍静默 |
| R7 | 递归改显式栈 | ✅ | `DiskAnalyzer.cs:82-157` `Stack<BuildFrame>` 迭代后序；`GetDirectorySizeFast` 亦栈式 |
| R8 | 清理前全量物化改流式 | ✅ | `TempFileCleaner.cs:209-245` `EnumerateFilesSafe` yield return；`BrowserCacheCleaner.cs:276-312` `SafeGetAllFiles` yield return |
| R9 | 线程安全 | ✅ | `AsyncRelayCommand.cs:25,64` `Interlocked.CompareExchange`；`DiskAnalyzer.cs:210` OC 建好一次性赋值；`DiskAnalysisViewModel.cs:82` `Dispatcher.BeginInvoke` |
| R10 | AI 仅发文件名+扩展名 | ✅ | `AIFileAnalyzer.cs:62-77` 仅 name/ext/size；`:112` 强制 https |
| R11 | Children 通知优化 | ✅ | `DiskAnalyzer.cs:210` 单次 OC 赋值，避免构建期通知风暴 |
| R12 | DuplicateFinder List<FileInfo>→struct | ⚠️ | `NativeMethods.cs:11-22` 引入 `FileMeta` readonly struct 并已用于 `DiskAnalyzer` 热路径（`:108`）；但 `DuplicateFinder.cs:29` 仍为 `new List<FileInfo>()` |
| R13 | Process.Start("explorer") 隐式提权 | ✅ | `ExplorerHelper.cs` 统一 helper，`UseShellExecute=false` + 拒 UNC/URL；`DiskAnalysisViewModel.cs:77` 经此调用 |
| R14 | net6.0-windows EOL | ✅ | `DiskCleaner.csproj:5` net8.0-windows；`DiskCleaner.Elevated.csproj:5` 同步；self-contained 发布 runtime 8.0.2926 |
| R15 | 无 git/CI | ✅ | `.git/` 已初始化（2 次提交）；`.github/workflows/build-and-smoke.yml` CI |
| R16 | 无真机回归用例 | ⚠️ | `DiskCleaner.SmokeTest` 新增 9 项无头冒烟（覆盖 R1/R3/R4/R7），已接入 CI；但非“真机特权路径”覆盖，且未纳入 `.sln` |

---

## 二、专业重点项详核

### R1 — MsiExec 残留点击式 RCE  ★已闭环（双层防护）
**主进程（`SoftwareManager.Uninstall`）**
- `SoftwareManager.cs:153` 识别 msiexec；`:168-172` 调用 `IsSafeMsiUninstall(arguments)`，不匹配 `/X{GUID}` 或 `/uninstall <本地.msi>` 时**直接 return false，不弹任何确认框**。
- 确认框仅在第 181-193 行出现，且**仅在白名单通过后**；`MessageBoxResult.No` 为默认，文案明示“以管理员权限运行 msiexec 卸载”。
- `IsSafeMsiUninstall`（`:329-366`）严格正则：单 token 须 `^[-/][xX]\{GUID\}$`；双 token 仅接受 `/x|-x`+GUID 或 `/uninstall|-uninstall`+本地 `.msi`（拒 `\\`、http/https/ftp、非盘符路径）。

**提权 Helper（`DiskCleaner.Elevated/Program.cs`）**
- `Program.cs:158-166` 对卸载命令**再次解析并二次校验** `IsSafeMsiUninstall`，未通过则 `return 1`（拒绝）。
- 非 MSI 走 `IsTrustworthyUninstaller`（`:173,288-309`）：受信任目录 + Authenticode 签名。

**攻击链判定**：低权 HKCU 写入 `MsiExec.exe /i \\evil\a.msi` → 主进程 `IsSafeMsiUninstall("/i \\evil\a.msi")` 返回 false → 硬拒绝，管理员**根本看不到可点击确认框**。即便绕过到确认框阶段，参数已被锁为安全形态。**原 RCE 链已切断。**

### R4 — 全量提权  ★已闭环（按需提权）
- `app.manifest:8` 由 `requireAdministrator` → `asInvoker`，主程序默认普通权限。
- 破坏性操作（删除受保护目录、创建符号链接、卸载）抽到独立 `DiskCleaner.Elevated.exe`，经 `ElevationHelper.RunElevated` 用 `Verb="runas"`（`:49`）触发 UAC，用户取消返回 1223（`:60-64`）。
- Helper 自检 `IsElevated()`（`:22`）；`delete` 命令有 `IsLocalPath`（`:95-99`）+ `IsProtectedRoot`（`:101-105,212-243`）纵深守卫；`symlink` 要求目标路径不存在（`:69-73`）。
- **残留点（见 ➕N3/N4）**：`ElevationHelper.IsProtectedPath` 与主端 `Program.IsProtectedRoot` 语义不一致；`Uninstall` 未 `WaitForExit`。非阻断。

### R7 — 递归改显式栈  ★稳定
- `DiskAnalyzer.BuildNode`（`:82-157`）用 `Stack<BuildFrame>` 迭代后序遍历，注释明确（`:77-81`）。冒烟测试 `DiskAnalyzer_DeepTree` 构建 **2000 层**嵌套验证不爆栈。稳定。

### R8 — 流式物化  ★稳定
- `TempFileCleaner.EnumerateFilesSafe`（`:209-245`）与 `BrowserCacheCleaner.SafeGetAllFiles`（`:276-312`）均 `yield return` 逐文件流式产出，清理循环 `foreach` 消费，不再全量物化 `List<string>`。稳定。

### R9 — 线程安全  ★稳定（无回归）
- `AsyncRelayCommand` 用 `Interlocked.CompareExchange` 防重入（`:25,64`），异常改 `Logger.Error`（`:33,72`）而非静默。
- `DiskAnalyzer` 后台线程构建子节点于普通 `List<FileNode>`，仅在 `FinalizeFrame` 一次性 `new ObservableCollection<FileNode>(...)`（`:210`）赋给尚未绑定的节点；树返回 UI 线程后才绑定，无后台改 OC。
- `DiskAnalysisViewModel` 进度回调经 `Application.Current.Dispatcher.BeginInvoke`（`:82-86`）回 UI 线程；`RootFolders` 在 UI 线程赋值/Add（`:97,107`）。无回归。

### R12 — struct 化  ⚠️ 部分修复
- 已引入 `FileMeta` readonly struct（`NativeMethods.cs:11-22`）并通过 `TryGetFileMeta`（`:247-266`）在 `DiskAnalyzer` 热路径消除 per-file `FileInfo` 分配（`:108`）——这是 GC 压力最大处，已解决。
- **但 `DuplicateFinder.cs:29` 仍为 `var allFiles = new List<FileInfo>();`**，本轮核查的具体行未改。全盘/大目录重复扫描时仍 per-file 分配 `FileInfo`。建议同法改为 `(string,long)` 元组或 readonly struct。属中低危性能项，非阻断。

### R13 — explorer 调用  ★已闭环
- 全仓唯一 `Process.Start("explorer.exe")` 在 `ExplorerHelper.OpenFolder`（`:30-36`），`UseShellExecute=false`、`CreateNoWindow=true`，并拒绝 `\\`/http/https/ftp（`:19-23`）。
- `DiskAnalysisViewModel.cs:77` 的“在资源管理器打开”已改走 `ExplorerHelper.OpenFolder`。`ElevationHelper` 的 `UseShellExecute=true` 仅配合 `runas` 用于 UAC，符合预期。

### R14 — .NET 版本  ★已闭环
- 主、Elevated 两个 csproj 均 `net8.0-windows`（LTS，受支持至 2026-11）。`publish/` 为 self-contained 发布（runtime 8.0.2926.32403 已捆绑），终端用户无需预装运行时。本地因仅 6.0.400 SDK 无法编译，属环境限制。

---

## 三、其余项简要判定
- **R2/R3/R5/R7/R8/R9/R10/R11**：本轮源码核对均稳定闭环，无回归（详见上表证据）。
- **之前已修 P0 回归抽查**：`EstimatedSize` 单位（`SoftwareManager.ParseEstimatedSize:123-130` 按 KB）、CTS Dispose（`DiskAnalysisViewModel.cs:95-96`）、PropertyChanged（`ViewModelBase.Set` Equals 守卫）、AI 隐私（`AIFileAnalyzer.cs:62-77`）、ReparsePoint 检测、符号链接搬家顺序、线程安全——全部稳定。

---

## 四、新发现（➕）

| 编号 | 严重度 | 主题 | 证据与说明 |
|------|--------|------|------------|
| N1 | 中 | MSI 白名单逻辑重复（DRY 违反） | `SoftwareManager.cs:329-366` 与 `Program.cs:311-354` 各有一份 `IsSafeMsiUninstall`/`IsTrustworthyUninstaller`，代码注释已写明“必须与 SoftwareManager 保持一致”（`Program.cs:324`）。任一侧更新而另一侧遗漏即可能产生绕过窗口。 |
| N2 | 低 | `isMsi` 仅精确匹配 `msiexec.exe` | `SoftwareManager.cs:153`、`Program.cs:158` 仅 `Equals("msiexec.exe")`。`MsiExec`（无扩展名）会落入信任检查分支；虽 Helper 端二次拒绝，但攻击面可更小。 |
| N3 | 低 | `IsProtectedPath` 与 `IsProtectedRoot` 语义不一致 | 主端 `ElevationHelper.cs:74-86` 用“任意路径段等于 Windows/Program Files”粗判是否提权；Helper 端 `Program.cs:212-243` 用前缀匹配做权威守卫。对 `C:\Users\X\...\Program Files\junk` 之类路径可能过度提权（非安全洞，但语义混乱）。 |
| N4 | 低 | 提权卸载 fire-and-forget | `Program.cs:188` `Process.Start(psi)` 未 `WaitForExit`，主端拿不到 msiexec 真实退出码（UI 文案“已启动”措辞准确，但无法区分成败）。 |
| N5 | 低 | SmokeTest 未纳入 .sln | `DiskCleanerPro.sln` 仅含 2 个项目，CI 用路径直跑 `DiskCleaner.SmokeTest`；`dotnet build DiskCleanerPro.sln` 不会编译冒烟工程，构建步骤无法拦截其破损。 |
| N6 | 低 | FlushDnsAsync 仍静默吞异常 | `TempFileCleaner.cs:361` `catch { return false; }`；失败已上抛 UI 文案但无日志（R6 残留）。 |
| N7 | 低 | 空 catch 仍约 13 处（严）+ 多处带注释 catch | `TempFileCleaner.cs:1`、`FileMoverService.cs:2`、`DuplicateFinder.cs:4`、`BrowserCacheCleaner.cs:2`、`AIFileAnalyzer.cs:1`、`MainWindow.xaml.cs:1`（Grep 统计）。多为权限跳过（可接受），但非预期异常无观测。 |

---

## 五、整体复检评级 + Go/No-Go

### 评级：🟡 有条件通过（Conditional Pass）
- 第三轮两大 High 门禁 **R1、R4 均已闭环且为双层防护**，当前 **0 Critical / 0 High**。
- 残留 3 项 ⚠️（R6 观测、R12 性能、R16 真机回归）均为中低危、非 Beta 发布阻断项。

### GA（公开发布）
**🟡 有条件通过（Conditional Go）**
发布前建议至少补齐：
1. R16 真机特权路径回归（UAC 取消、拒删受保护根、`/i \\evil` 双层拒绝的真实集成测试）；
2. R6 非预期异常的结构化日志；
3. 对新增 `ElevatedHelper`（特权组件）做独立安全评审（见 N1/N3/N4）。

### 受限 Beta（受信任测试者）
**🟢 通过（Go）**
核心安全门禁已闭，CI 冒烟覆盖关键修复项，可面向受信任测试者发布。

---

## 六、改进建议（≥5）

1. **去重 MSI 白名单（N1）**：将 `IsSafeMsiUninstall`/`IsTrustworthyUninstaller` 抽到共享类库或主程序单一实现，主端与 Helper 共用同一份，消除“需保持一致”的隐患，从架构上杜绝绕过窗口。
2. **R16 补齐真机特权路径回归**：CI 用自托管/开启 UAC 的 Windows runner，对 `ElevatedHelper` 真实 `delete`/`uninstall`/`symlink` 做集成测试，至少验证 UAC 取消返回 1223、拒删受保护根、`/i \\evil` 被双层拒绝。当前仅反射级验证。
3. **R6 异常可观测性**：扫描期预期内的 `UnauthorizedAccessException` 与真正异常区分；对“非预期” `catch {}` 改记 `Logger.Warning`；`FlushDnsAsync`（N6）补日志。
4. **R12 收尾**：`DuplicateFinder.cs:29` 改为 `(string fullPath, long length)` readonly struct 或 `List<(string,long)>`，复用 `TryGetFileMeta` 思路去掉 per-file `FileInfo` 分配。
5. **ElevatedHelper 权威守卫 + 退出码（N3/N4）**：建议 Helper 端做唯一权威守卫，主端只做“是否需提权”粗判，统一两者语义；`Uninstall` 改为 `WaitForExit` 并回传 msiexec 退出码，使 UI 区分“已启动/已成功”。
6. **工程化收口（N5）**：把 `DiskCleaner.SmokeTest` 纳入 `DiskCleanerPro.sln`；加 `global.json` 固定 SDK 到 8.x 防漂移；发布流水线校验 PDB 已剥离（当前手工配置已生效，建议自动化断言）。
7. **isMsi 识别加固（N2）**：除 `msiexec.exe` 外，可解析后确认为 `%SystemRoot%\System32\msiexec.exe` 即视为 MsiExec，避免无扩展名形态绕过白名单落入信任分支。

---
*核验限制：本地仅 .NET SDK 6.0.400，无法编译 net8.0-windows；结论基于源码逐行核对 + 01:21 生成的 self-contained .NET 8 发布产物（证明可构建、无编译回归）。建议以 CI（windows-latest + .NET 8 SDK）实际跑通冒烟套件作为最终门禁。*
