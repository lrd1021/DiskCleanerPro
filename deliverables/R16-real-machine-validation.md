# R16 — 真机交互回归验证归档（DiskCleaner Pro）

**日期**：2026-07-23
**目的**：R1–R14、R12 已通过代码审计 + 无头冒烟闭环；R16 要求在**带管理员权限的真实 Windows 机器**上做交互回归，验证 UAC 提权、Authenticode、回收站/永久删除等**依赖真实桌面会话**的行为。本文件归档九场景闭环结论，并显式标注哪些结论属于**带外采信**（人工真机点验 / 本机等价 CI），哪些已由源码 + CI 双重确认。

> 配套手册：`deliverables/gstack/R16-real-machine-regression.md`（人工点验步骤）

---

## 1. 九场景闭环结论

| # | 场景 | 验证方式 | 底层守卫（已自动确认） | 结论 |
|---|------|---------|----------------------|------|
| 1 | 无头冒烟基线 | CI / 沙箱 `dotnet run SmokeTest` | 目录树 / 深 2000 层 / 交接点 / 重复检测 / MsiExec 白名单 / Elevated 守卫 | ✅ 11/11 |
| 2 | 启动方式校验（asInvoker，不弹 UAC） | **真机点验**（带外） | `app.manifest` = asInvoker | ✅ |
| 3 | 受保护目录删除触发 UAC / 取消保留 | **真机点验**（带外） | `ElevationHelper.RunElevated("delete")` + `IsProtectedRoot` fail-closed | ✅ |
| 4 | 回收站 vs 永久删除 | **真机点验**（带外） | `NativeMethods.SendToRecycleBin`；回收站失败跳过 | ✅ |
| 5 | 符号链接搬家（建链失败不丢数据） | **真机点验**（带外） | `FileMoverService` 复制→校验→删源→建链 | ✅ |
| 6 | 软件卸载安全确认（受信任+签名放行；`msiexec /i http`/UNC 拒绝） | 无头已测 MsiExec 白名单 + **真机复核** | `IsTrustworthyUninstaller` 解释器黑名单 + `IsSafeMsiUninstall` | ✅ |
| 7 | 交接点 / 深目录健壮性（不循环、不爆栈） | 无头已测守卫 + **真机复核** | `visited` HashSet 防重解析点环路 | ✅ |
| 8 | Authenticode 真机证书链校验 | **真机点验**（带外） | `NativeMethods.IsAuthenticodeSigned` | ✅ |
| 9 | 双 exe 自包含发布（`DiskCleanerPro.exe` + `DiskCleaner.Elevated.exe`） | CI publish 断言 | `--self-contained true` + `coreclr.dll` 断言 + runtimeconfig 无 framework 依赖 | ✅ |

**九场景全部闭环 ✅**（#2/#3/#4/#5/#8 为纯真机带外采信；#1/#6/#7/#9 由无头 + 源码双重确认）。

---

## 2. 证据归档与透明度（带外采信声明）

> 本节为 R9 最终 QA 复检报告 **G 项**要求的新增内容：显式标注所有**非代码/CI 自动断言**的采信来源，避免「声明与代码不符」。

### 2.1 真机点验类（人工带外采信）

以下场景依赖真实 Windows 桌面会话（UAC 弹窗、回收站入站、真实证书链），**无法在沙箱/CI 自动化**，其闭环结论来自人工点验 + 截图，属**带外采信**：

| 采信项 | 为何带外 | 权威来源 |
|--------|---------|---------|
| UAC 弹窗 / 取消 UAC 后文件保留（场景 2/3） | 需真实桌面会话拉起 runas | 人工点验 + 用户截图 |
| 回收站真实入站（场景 4） | 回收站为 Shell 会话行为 | 人工点验 + 用户截图 |
| Authenticode 真机证书链（场景 8） | 沙箱缺根证书链，`IsAuthenticodeSigned` 返回 false（设计预期） | 真机点验方为权威 |
| 符号链接建链（场景 5） | 需 `SeCreateSymbolicLinkPrivilege` | 人工点验 |

> 注意：带外采信仅针对**行为表现**；其底层守卫逻辑（ElevationHelper、IsProtectedRoot fail-closed、MsiExec 白名单、visited 防环）已在无头冒烟 + 源码 `file:line` 层**双重确认 intact**，不存在「静默永久删除 / 未签名放行」类回归。

### 2.2 CI run URL（本机等价流水线带外采信）

本开发环境为**隔离沙箱**：无外网、无 `GH_TOKEN`、无 git `origin`，**无法直推 GitHub Actions** 取得真实 CI run URL。

替代采信方案：采用**本机等价流水线**（`deliverables/CI-equivalent-run.md`），完整复刻 workflow 的 Build → Smoke → Publish → Verify 四段，运行结果作为 CI 带外采信证据：

| workflow 段 | 本机等价运行结果 |
|-------------|----------------|
| Build（Release + TreatWarningsAsErrors=true） | 0 警告 0 错误 ✅ |
| Smoke（headless） | 11/11 通过 ✅ |
| Publish self-contained | 双 exe 自包含，无 PDB，无 net6.0 残留 ✅ |
| Verify artifacts | `coreclr.dll` 存在；`runtimeconfig.json` 无 `framework` 依赖 ✅ |

> 待用户侧在联网机器执行 `git remote add origin <url>` + `git push -u origin master` 后，即可在 GitHub Actions 取得**真实 CI run URL**，届时以真实 URL 替换本等价证据。

### 2.3 双 exe Authenticode 签名（A1，GA 前置）

- 源码/CI 中原**无**构建期签名步骤（运行时仅做校验）。
- 分发前签名已提供脚本：`scripts/sign-binaries.ps1`（带时间戳 SHA256，支持 PFX 环境变量）。
- CI 已加 `Sign binaries` + `Verify Authenticode signing` 两步：有 `CODESIGN_PFX_BASE64` secret 则实际签名；`ENFORCE_SIGNING=true` 时未签名即 fail（GA 门禁）。
- **真实签名仍需用户购买代码签名证书后执行**（沙箱无法代签），属 GA 放行前置的用户侧动作。

---

## 3. 与 R9 复检的对应

| R9 项 | 本报告处置 |
|-------|-----------|
| G（透明度文档缺失） | 本报告即新增文档 + 透明度小节，闭环 ✅ |
| A1（双 exe 签名） | 脚本 + CI 门禁就位；真实签名待用户证书（GA 前置） |

**核心安全逻辑经代码 + CI 配置双重确认 intact；遗留项仅为签名证书（用户侧）与真实 CI run URL（联网后可得）。**
