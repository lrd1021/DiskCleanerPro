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
