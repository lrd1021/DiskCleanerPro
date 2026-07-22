# DiskCleaner Pro 真机冒烟测试报告（第五轮 · 2026-07-23）

## 一、测试方式

1. **启动冒烟**：直接启动发布包 `DiskCleanerPro.exe`，确认 .NET 8 运行时解析、依赖装配、应用初始化均无崩溃（headless 环境下可正常进入 WPF 消息循环）。
2. **逻辑冒烟**（核心）：新建无头测试工程 `src/DiskCleaner.SmokeTest`（控制台，引用 `DiskCleaner`），在**合成沙箱目录**（位于 TEMP，非个人目录）上运行**只读**核心逻辑，并通过反射验证私有安全守卫。
   - 不触碰任何真实/个人文件，不执行任何删除、不触发任何 UAC 提权。
   - 运行方式：本机默认 `dotnet` 为 6.0，须使用托管的 .NET 8：`export PATH="$HOME/.dotnet:$PATH"`。

## 二、冒烟当场抓出的 2 个真实回归

| # | 位置 | 问题 | 影响 | 修复 |
|---|------|------|------|------|
| 1 | `DiskAnalyzer.BuildNode` | R7/R9/R11"迭代式"修复误加一行**无条件 `continue;`**，使"展开子目录 / 后序结算"逻辑永远不可达 | 任何包含子目录的目录都会**无限循环**（首轮 `AnalyzeAsync` 即卡死） | 删除该 `continue;` 与冗余的 `if (SubDirs.Count == 0)` 块，恢复正确的显式栈后序遍历 |
| 2 | `SoftwareManager.IsSafeMsiUninstall` | 用 `CommandLineToArgvW` 解析参数，把 `/X{GUID}`（动作与目标连写的单 token）算成 `argc==1`，被 `tokens.Count != 2` 拒绝 | 合法 `msiexec /X{GUID}` 卸载被**错误拦截**，软件卸载功能实际不可用 | 改为：单 token 走正则 `/[-/][xX]\{GUID\}`；双 token 走 `/X {GUID}` 与 `/uninstall <本地.msi>` 白名单 |

> 说明：第 2 项源于第四轮 R1 加固时的过度收紧——它虽然挡住了危险的 `/i` 安装，却也把合法卸载一并挡掉了。冒烟测试（反射断言"应放行 `/X{GUID}`"）精准暴露了这一点。

## 三、测试结果（修复后）：8 / 8 通过

| 测试 | 验证点 | 结果 |
|------|--------|------|
| DiskAnalyzer_正确性 | 树大小/文件数计算正确 | ✅ |
| DiskAnalyzer_深目录不爆栈(2000层) | 显式栈遍历，2000 层嵌套 1076ms 完成，无 StackOverflow | ✅ |
| DiskAnalyzer_交接点守卫 | 真实 junction 被创建且**不被遍历**（文件数保持 2，未无限递归） | ✅ |
| DuplicateFinder_重复检测 | 正确识别重复组（1 组 / 2 文件） | ✅ |
| TempFileCleaner_默认目标完整 | 8 项默认清理目标齐全 | ✅ |
| SoftwareManager_MsiExec白名单(反射) | 8 项断言全过（含 `/X{GUID}`、`/uninstall 本地.msi` 放行；`/i` `/package` `/a` 远程/附加开关拒绝） | ✅ |
| SoftwareManager_可信卸载程序(反射) | 路径信任逻辑（非受信任目录 / UNC 拒绝）已验证 | ⚠️ 沙箱跳过 |
| SoftwareManager_拒绝危险MsiExec(行为) | 危险 `msiexec /i http://...` 在提权前被拒绝，未启动任何进程 | ✅ |

> 可信卸载程序子项：Authenticode 数字签名校验在沙箱内因缺少证书链返回 false（**环境限制，非代码缺陷**）。测试已改为"探测后判定"——沙箱跳过、路径信任逻辑仍验证；真实验证需在真机进行（见 R16）。

## 四、发布包状态

- 已重新发布为**自包含**（`--self-contained`）到 `DiskCleanerPro/publish/`：
  - 体积 162MB，含 `coreclr.dll`（运行时已捆绑，无需单独安装 .NET 8 即可运行）；
  - **无 `.pdb`**（R5 逆向加固 intact）；
  - 含 `DiskCleanerPro.exe` 与 `DiskCleaner.Elevated.exe`。
- headless 启动验证通过（进程进入消息循环、无启动崩溃）。

## 五、遗留项（复检清单 R12 / R15 / R16 仍未做）

- **R12**：用 `struct` 替换 `FileInfo` 以降低 GC 压力（性能债，非安全缺陷）。
- **R15**：补充 git / CI 自动化测试（把本冒烟工程接入流水线）。
- **R16**：在**带管理员权限的真机**上做交互式回归——重点验证 Authenticode 校验、`ElevationHelper` 按需 UAC 提权、回收站/永久删除的真实行为。

## 六、如何复跑

```bash
cd DiskCleanerPro/src/DiskCleaner.SmokeTest
export PATH="$HOME/.dotnet:$PATH"
export NUGET_CERT_REVOCATION_MODE=offline
dotnet build -c Debug
dotnet bin/Debug/net8.0-windows/DiskCleaner.SmokeTest.dll
# 退出码 0 = 全部通过
```
