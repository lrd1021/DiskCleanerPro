# DiskCleaner Pro 安全整改状态总览（R1–R16）

> 最后更新：2026-07-23
> 构建：自包含 `publish/`（net8.0-windows, win-x64, 无 pdb）
> 冒烟：`src/DiskCleaner.SmokeTest`，`dotnet run` 退出码 0 = 全通过

## 闭环状态

| 项 | 描述 | 状态 | 关键改动 |
|---|---|---|---|
| R1 | MsiExec 提权卸载白名单 | ✅ 闭环 | 硬性白名单 + 单 token `/X{GUID}` 支持；**Elevated 与 SoftwareManager 两份逻辑已对齐** |
| R2 | 符号链接搬家安全 | ✅ 闭环 | 复制→校验→删源→建符号链接（建链失败退回纯移动） |
| R3 | 浏览器缓存交接点逃逸 | ✅ 闭环 | `SafeGetAllFiles` 跳过 ReparsePoint；空目录守卫 |
| R4 | 全量管理员提权 | ✅ 闭环 | `app.manifest` 改 `asInvoker`；独立 `DiskCleaner.Elevated.exe` 按需 runas |
| R5 | 发布包含 pdb（信息泄露） | ✅ 闭环 | Release `DebugType=none`；发布包无 pdb |
| R6 | FlushDns / 日志可观测性 | ✅ 闭环 | `FlushDnsAsync` 返 bool 读 ExitCode；受保护目录永久删走 Elevated |
| R7 | 目录树递归爆栈 | ✅ 闭环 | 迭代式显式栈后序遍历（修复中曾误加 `continue` 致死循环，已修） |
| R8 | TempFileCleaner 多余 .ToList() | ✅ 闭环 | 移除 |
| R9 | OC 后台线程竞态 | ✅ 闭环 | 子节点先收集到 List，返回 UI 线程后才一次性赋 OC |
| R10 | AI 读取文件头（隐私） | ✅ 闭环 | 仅发文件名/扩展名/大小，移除 16 字节文件头 |
| R11 | 树构建期通知风暴 | ✅ 闭环 | 同 R9 一次性赋值 |
| R12 | FileInfo 性能债 | ✅ 闭环（本轮） | 新增 `FileMeta` 只读 struct + `GetFileAttributesEx` P/Invoke；`DiskAnalyzer` 热循环去除 per-file `FileInfo` |
| R13 | ExplorerHelper 打开目录校验 | ✅ 闭环 | 校验本地目录、拒 UNC/URL、`UseShellExecute=false` |
| R14 | .NET 6 EOL | ✅ 闭环 | `TargetFramework` 升 `net8.0-windows` |
| R15 | 冒烟工程接入 git/CI | ✅ 闭环（本轮） | `git init` + `.gitignore` + `.github/workflows/build-and-smoke.yml` + 初始提交 |
| R16 | 真机交互回归 | 🟡 部分闭环（本轮） | **无头覆盖**：Elevated helper 守卫（本地路径/受保护根/MsiExec）已入冒烟；**交互执行需真机**（见 `R16-real-machine-regression.md`） |

---

## 上线前第四轮复检（pre-launch-recheck4 / N1–N7）整改状态

> 评审基线：第三轮 🔴 No-Go（GA）/🟡 条件Go（受限Beta）；本轮复检评级 **🟡 有条件通过（Conditional Pass）**，0 Critical / 0 High。
> 以下为复检报告（`R4_recheck_report.md`）第四轮新增发现的整改映射。

| 编号 | 严重度 | 主题 | 状态 | 关键改动 |
|------|--------|------|------|----------|
| N1 | 中 | MSI 白名单逻辑重复（DRY） | ✅ 闭环 | `SoftwareManager.IsSafeMsiUninstall` 与 `IsTrustworthyUninstaller` 均**委托** `DiskCleaner.Elevated.Program` 权威实现，单一来源，消除“需保持一致”的绕过窗口 |
| N2 | 低 | `isMsi` 仅精确匹配 `msiexec.exe` | ✅ 闭环 | 主端 `SoftwareManager` 与 Helper `Program` 均将无扩展名 `msiexec` 也识别为 MSI，避免误入信任检查分支 |
| N3 | 低 | `IsProtectedPath` 与 `IsProtectedRoot` 语义不一致 | ✅ 闭环 | `ElevationHelper.IsProtectedPath` 直接委托 `Program.IsProtectedRoot`（权威“根目录或受保护目录之下”语义） |
| N4 | 低 | 提权卸载 fire-and-forget（**原 GA 阻塞**） | ✅ 闭环 | `Program.Uninstall` 改为 `WaitForExit()` 并回传 msiexec 退出码（`0`/`3010`=成功，其余=失败），UI 可区分“已启动/成功” |
| N5 | 低 | SmokeTest 未纳入 `.sln` | ✅ 闭环 | `DiskCleanerPro.sln` 加入 `DiskCleaner.SmokeTest` 工程（GUID 固定），CI 编译其；`AssemblyInfo` 加 `InternalsVisibleTo` 暴露守卫方法 |
| N6 | 低 | `FlushDnsAsync` 静默吞异常 | ✅ 闭环 | `TempFileCleaner.FlushDnsAsync` 的 `catch` 改为 `Logger.Warning`（R6 观测性） |
| N7 | 低 | 空 `catch` 约 13 处（严）+ 多处带注 catch | ⚠️ 部分 | 提权子进程新增 **JSON Lines 审计**（`delete`/`uninstall`/`symlink`/异常），落盘 `%LocalAppData%/DiskCleanerPro/logs/elevated-*.log`；`RunElevated` 增加请求/结果/取消/失败结构化日志；其余多为预期内的权限跳过（按报告可接受），未逐一插桩 |

> **N4 原列 GA 阻塞项**：修复后 UI 不再把“已启动”误报为“成功”——`ElevationHelper.UninstallElevated` 拿到的退出码由 Helper `WaitForExit` 真实回传。
> **N2 完整性校验**：`ElevationHelper.GetHelperPath` 现校验 helper 必须位于主程序同目录，并调用 `NativeMethods.IsAuthenticodeSigned` 校验（沙箱/调试环境无证书链仅告警、不阻断；**正式发版须对 helper 做 Authenticode 签名**，属 GA 发布门禁之一）。
> **Logger.Escape 加固（N3安）**：`Logger.Escape` 重写，对 `" \ \r \n \t \b \f` 及 `<0x20` 控制字符转义为 `\uXXXX`，消除审计/日志注入与截断风险。

## 本轮（2026-07-23 复检4 整改）改动

1. **N4**：`DiskCleaner.Elevated/Program.cs` 的 `Uninstall` 增加 `process.WaitForExit()` 并读取 `ExitCode`，成功判定 `0` 或 `3010`，失败回传真实退出码。
2. **N1（去重）**：`SoftwareManager.IsSafeMsiUninstall` 与 `IsTrustworthyUninstaller` 改为委托 `DiskCleaner.Elevated.Program` 的同名 `internal static` 实现；`Program` 中对应方法可见性升为 `internal`。
3. **N2（加固）**：`SoftwareManager` 与 `Program` 的 `isMsi` 判定均接受 `msiexec.exe` 与 `msiexec`（无扩展名）。
4. **N3（守卫统一）**：`ElevationHelper.IsProtectedPath` 委托 `Program.IsProtectedRoot`；`GetHelperPath` 加同目录校验 + Authenticode 校验（告警级）。
5. **N5（工程化）**：`DiskCleanerPro.sln` 纳入 `DiskCleaner.SmokeTest`；`AssemblyInfo` 暴露 `InternalsVisibleTo("DiskCleaner.SmokeTest")` 与 `("DiskCleanerPro")`。
6. **N6（观测）**：`TempFileCleaner.FlushDnsAsync` 的 `catch` 记 `Logger.Warning`；`RunElevated` 增加请求/结果/取消/失败日志。
7. **R12（复检）**：`DuplicateFinder` 热循环由 `List<FileInfo>` 改为 `List<FileMeta>`（`NativeMethods.TryGetFileMeta`），去除 per-file `FileInfo` 分配。
8. **审计（N1/N7 观测）**：`Program` 新增 `Audit(...)` 写 JSON Lines 审计日志（含 `EscapeJson`），覆盖 symlink/delete/uninstall/顶层异常。
9. **Logger.Escape（N3安）**：转义覆盖全部关键控制字符与 `<0x20`，防注入/截断。

## 本轮（2026-07-23）改动

1. **R12**：`NativeMethods` 增加 `FileMeta` 只读 struct、`GetFileAttributesEx` P/Invoke、`TryGetFileMeta`；`DiskAnalyzer.BuildNode` 与 `GetDirectorySizeFast` 改用 `TryGetFileMeta`，消除热循环中的 `FileInfo` 分配。
2. **R15**：`DiskCleanerPro` 接入 git（`8b33e07`），新增 CI 工作流在 push/PR 时构建并跑冒烟。
3. **R16 无头部分**：
   - 修复 **Elevated helper 中遗漏的 `IsSafeMsiUninstall` 单 token `/X{GUID}` 回归**（与 SoftwareManager 对齐，否则提权卸载路径会错误拒绝合法卸载）。
   - 通过 `InternalsVisibleTo` 暴露内部守卫方法，冒烟新增 `ElevatedHelper_守卫` 12 项断言。
   - 编写 `R16-real-machine-regression.md`：UAC 提权、回收站/永久删除、Authenticode、符号链接建链等需真机点验的步骤与通过标准。

## 仍需用户执行（R16 交互）

- 在**带管理员权限的真实 Windows 机器**上，按 `R16-real-machine-regression.md` 步骤 2–8 点一遍（约 10–15 分钟）。
- 沙箱/CI 无法验证：UAC 弹窗真实拉起、回收站真实入站、取消 UAC 后文件保留、Authenticode 真实证书链、符号链接建链特权。

## 冒烟测试清单（HEAD）

`DiskAnalyzer_正确性` / `DiskAnalyzer_深目录不爆栈(2000层)` / `DiskAnalyzer_交接点守卫` /
`DuplicateFinder_重复检测` / `TempFileCleaner_默认目标` / `SoftwareManager_MsiExec白名单` /
`SoftwareManager_可信卸载程序` / `SoftwareManager_拒绝危险MsiExec` / `ElevatedHelper_守卫(R16)`
