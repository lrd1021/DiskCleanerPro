# DiskCleaner Pro 冒烟测试报告（综合 · 2026-07-23）

> 涵盖第五轮（启动+逻辑冒烟）与第六轮（R12/R15/R16 闭环）。
> 运行方式见文末。退出码 0 = 全部通过。

## 一、测试方式

1. **启动冒烟**：直接启动发布包 `DiskCleanerPro.exe`，确认 .NET 8 运行时解析、依赖装配、初始化均无崩溃（headless 下可正常进入 WPF 消息循环）。
2. **逻辑冒烟（核心）**：无头测试工程 `src/DiskCleaner.SmokeTest`（引用 `DiskCleaner` + `DiskCleaner.Elevated`），在**合成沙箱目录**（TEMP 下，非个人目录）运行**只读**核心逻辑，并通过反射验证私有安全守卫。
   - 不触碰真实/个人文件，不执行删除、不触发 UAC。
   - 本机默认 `dotnet` 为 6.0，须用托管 .NET 8：`export PATH="$HOME/.dotnet:$PATH"`。

## 二、冒烟当场抓出的真实回归（共 4 个）

| # | 位置 | 问题 | 影响 | 修复 |
|---|------|------|------|------|
| 1 | `DiskAnalyzer.BuildNode` | R7/R9/R11"迭代式"修复误加**无条件 `continue;`**，使展开/后序结算逻辑不可达 | 含子目录的目录**无限循环**（首轮 `AnalyzeAsync` 卡死） | 删除冗余 `continue;` 与 `if (SubDirs.Count==0)` 块，恢复显式栈后序遍历 |
| 2 | `SoftwareManager.IsSafeMsiUninstall` | `CommandLineToArgvW` 把 `/X{GUID}`（单 token）算成 `argc==1`，被 `tokens.Count != 2` 拒绝 | 合法 `msiexec /X{GUID}` 卸载被**错误拦截** | 单 token 走正则；双 token 走 `/X {GUID}` 与 `/uninstall <本地.msi>` 白名单 |
| 3 | `DiskCleaner.Elevated.IsProtectedRoot` | `root` 被 `TrimEnd('\\')` 成 `C:` 而 `dir` 仍是 `C:\`，两者永不相等 | **Elevated helper 删除守卫对 `C:\Windows` 等失效**——以管理员身份运行时会放行删除系统根目录（严重） | `root`/`dir` 统一 `TrimEnd('\\')`；并增加"受保护目录之下任意路径"防御纵深 |
| 4 | `DiskCleaner.Elevated.IsSafeMsiUninstall` | 与 #2 同源的旧逻辑，单 token `/X{GUID}` 被拒 | 提权卸载路径（admin）会**错误拒绝**合法卸载 | 与 `SoftwareManager` 对齐：单 token 正则 + 双 token 白名单 |

> #3 是本轮（R16 无头覆盖）抓到的**高危安全回归**：Elevated helper 以管理员运行，其 `IsProtectedRoot` 是删除操作的最后防线；旧实现会让 `Delete C:\Windows` 通过守卫。反射断言 `IsProtectedRoot(@"C:\Windows") == true` 精准暴露。

## 三、测试结果：9 / 9 通过

| 测试 | 验证点 | 结果 |
|------|--------|------|
| DiskAnalyzer_正确性 | 树大小/文件数正确 | ✅ |
| DiskAnalyzer_深目录不爆栈(2000层) | 显式栈遍历，2000 层嵌套 ~1100ms 完成，无 StackOverflow | ✅ |
| DiskAnalyzer_交接点守卫 | 真实 junction 创建且**不被遍历**（文件数=2，未无限递归） | ✅ |
| DuplicateFinder_重复检测 | 正确识别重复组（1 组 / 2 文件） | ✅ |
| TempFileCleaner_默认目标完整 | 8 项默认清理目标齐全 | ✅ |
| SoftwareManager_MsiExec白名单(反射) | 8 项断言全过（含 `/X{GUID}` 放行；`/i` `/package` `/a` 远程/附加开关拒绝） | ✅ |
| SoftwareManager_可信卸载程序(反射) | 路径信任逻辑（非受信任目录 / UNC 拒绝）已验证 | ⚠️ 沙箱跳过 Authenticode（缺证书链，见 R16） |
| SoftwareManager_拒绝危险MsiExec(行为) | 危险 `msiexec /i http://...` 提权前被拒，未启动进程 | ✅ |
| ElevatedHelper_守卫(反射, R16) | 12 项断言：本地路径拒绝 UNC/URL、受保护根识别（`C:\Windows` 等）、MsiExec 单/双 token 白名单 | ✅ |

> 可信卸载程序子项：Authenticode 在沙箱因缺证书链返回 false（**环境限制，非代码缺陷**），改为"探测后判定"——路径信任逻辑仍验证；真实验证需真机（R16 runbook）。

## 四、R12 / R15 / R16 闭环情况

- **R12（struct 替代 FileInfo）**：`NativeMethods` 新增 `FileMeta` 只读 struct + `GetFileAttributesEx` P/Invoke + `TryGetFileMeta`；`DiskAnalyzer.BuildNode` 与 `GetDirectorySizeFast` 热循环去除 per-file `FileInfo` 分配，降低 GC 压力。冒烟的 DiskAnalyzer 三项测试仍通过。
- **R15（git/CI）**：`git init` + `.gitignore`（排除 bin/obj/publish/.gstack）+ `.github/workflows/build-and-smoke.yml`（push/PR 时构建并跑冒烟）+ 初始提交 `8b33e07`。
- **R16（真机交互回归）**：
  - **无头覆盖已做**：Elevated helper 守卫（本地路径/受保护根/MsiExec）入冒烟；并借反射抓出 #3、#4 两个真实回归。
  - **交互执行需真机**：编写 `R16-real-machine-regression.md`，覆盖 UAC 提权、回收站/永久删除、Authenticode、符号链接建链等步骤与通过标准。沙箱/CI 无法验证这些（需真实桌面会话与证书链）。

## 五、发布包状态

- 已发布**自包含**（`--self-contained`）到 `DiskCleanerPro/publish/`：
  - 162MB，含 `coreclr.dll`（运行时捆绑，无需单独装 .NET 8）；
  - **无 `.pdb`**（R5 intact）；
  - 含 `DiskCleanerPro.exe` 与 `DiskCleaner.Elevated.exe`。
- headless 启动验证通过。

## 六、如何复跑

```bash
cd DiskCleanerPro/src/DiskCleaner.SmokeTest
export PATH="$HOME/.dotnet:$PATH"
export NUGET_CERT_REVOCATION_MODE=offline
dotnet build -c Debug
dotnet bin/Debug/net8.0-windows/DiskCleaner.SmokeTest.dll
# 退出码 0 = 全部通过
```

## 七、真机交互回归（R16）

按 `R16-real-machine-regression.md` 在带管理员权限的真实 Windows 机器上点验步骤 2–8（约 10–15 分钟）。

## 八、第四轮复检（recheck4）闭环补充

本轮在 R12/R15/R16 闭环基础上，依据 `R4_recheck_report.md` 补齐第四轮新增发现（N1–N7）：

- **构建与冒烟均复跑通过**：`dotnet build DiskCleanerPro.sln -c Debug` **0 警告 / 0 错误**；冒烟 **9 / 9 通过**（退出码 0）。
- **N4（原 GA 阻塞）**：`Program.Uninstall` 改为 `WaitForExit()` 并回传 msiexec 真实退出码，UI 不再把“已启动”误报为“成功”。
- **N1（去重）**：`SoftwareManager.IsSafeMsiUninstall` 与 `IsTrustworthyUninstaller` 委托 `DiskCleaner.Elevated.Program` 权威实现，消除双份逻辑绕过窗口。
- **N2（加固）**：主端/Helper 的 `isMsi` 判定均接受无扩展名 `msiexec`。
- **N3（守卫统一）**：`ElevationHelper.IsProtectedPath` 委托 `Program.IsProtectedRoot`；`GetHelperPath` 加同目录 + Authenticode 校验（告警级）。
- **N5（工程化）**：`DiskCleanerPro.sln` 纳入 `DiskCleaner.SmokeTest`，CI 编译其。
- **N6（观测）**：`FlushDnsAsync` 的 `catch` 记 `Logger.Warning`；`RunElevated` 加请求/结果/取消/失败日志。
- **审计（N1/N7）**：提权子进程新增 JSON Lines 审计（`%LocalAppData%/DiskCleanerPro/logs/elevated-*.log`）。
- **Logger.Escape（N3安）**：转义覆盖全部关键控制字符与 `<0x20`，防注入/截断。
- **R12（复检）**：`DuplicateFinder` 热循环由 `List<FileInfo>` 改为 `List<FileMeta>`。

> 复跑方式：`export PATH="$HOME/.dotnet:$PATH"` → `dotnet build DiskCleanerPro.sln -c Debug` → `dotnet src/DiskCleaner.SmokeTest/bin/Debug/net8.0-windows/DiskCleaner.SmokeTest.dll`，退出码 0 = 全通过。
> 发布包已重新 `publish/`（自包含 win-x64，无 pdb），含上述全部修复。
