# DiskCleanerPro 第九轮（最终验证）QA 复检报告

**日期**：2026-07-23
**场景**：第八轮报告（条件 Go）后，用户声修复 A–G 七项，逐项代码/配置核验是否真正闭环
**核验方式**：直接 Read + Grep 源码与 CI workflow（不依赖 git diff）；本地无 .NET 8 SDK，构建型门禁以配置核验 + CI 带外为权威

---

## 📌 TL;DR（执行摘要）

- **最终签核：🟡 条件 Go（GA）**
- A–F 六项代码修复 + D 发布韧性 **全部在 file:line 层可验证闭环**（6/7）
- **G（透明度文档）未闭环**：目标文件 `R16-real-machine-validation.md` 在仓库中**不存在**，既有 `R16-real-machine-regression.md` 亦**无**"证据归档与透明度"小节与带外采信声明
- **A1（双 exe Authenticode 签名）仍为 GA 前置**：源码/CI 中均**无**签名步骤，与 R8 结论一致
- 无新增回归（A–G 未引入任何新的裸 `catch{}`）；仅存 `SoftwareManager.cs:119/166` 空 catch 为 R8 既有 P3 项，非本轮引入

**GA 放行须满足 2 项条件**：
1. **完成 G**：新增 `R16-real-machine-validation.md`（或于 regression 文档补）"证据归档与透明度"小节，显式标注 UAC / 真机 / CI run URL 等带外采信项
2. **完成 A1**：分发前对 `DiskCleanerPro.exe` + `DiskCleaner.Elevated.exe` 完成带时间戳 Authenticode 签名，并在 CI 加分发前签名断言

---

## 1. A–G 逐项核验（file:line）

| 项 | 修复意图 | 核验位置 | 结论 |
|----|---------|---------|------|
| **A** | visited 命中补 `Logger.Warning`；内层目录由 `Contains` 改 `Add`，对齐参考实现 | `BrowserCacheCleaner.cs:277-281,293-297,321-325,338-342`；`FileMoverService.cs:41-45,61-65`；参考 `TempFileCleaner.cs:271-273` | ✅ 闭环 |
| **B** | `SafeEnumerateDirectories`/`SafeGetLastModified` 广义 catch 收窄为 `IOException`/`UnauthorizedAccessException` + 日志 | `DiskAnalyzer.cs:217-240`（`catch IOException` L231、`catch UnauthorizedAccessException` L235）；`L242-255`（L245/L250） | ✅ 闭环 |
| **C** | Build 步骤加 `-p:TreatWarningsAsErrors=true`，0 警强制门禁 | `build-and-smoke.yml:28` | ✅ 配置闭环（运行态依赖 CI 带外，见 §4） |
| **D** | `Elevated.csproj` 不固化 SelfContained（避 NETSDK1151）；CI "Verify publish artifacts" 增 runtimeconfig.json 检查 + 既有 coreclr.dll 断言 | `DiskCleaner.Elevated.csproj:1-21`（无 SelfContained/RID）；`build-and-smoke.yml:38-39`（CLI `-r win-x64 --self-contained true`）、`L57-61`（coreclr 断言）、`L62-72`（runtimeconfig 无 framework 依赖） | ✅ 闭环 |
| **E** | `IsProtectedRoot` 解析异常 fail-open(`return false`)→fail-closed(`return true`) | `DiskCleaner.Elevated/Program.cs:324-329`（`catch(Exception)` + 注释 "fail-closed" + `return true`） | ✅ 闭环 |
| **F** | `AIFileAnalyzer` JSON 解析 `catch{}` 收窄为 `JsonException` + 注释 | `AIFileAnalyzer.cs:214-215`（注释 "兜底…静默继续尝试其他字段" + `catch (System.Text.Json.JsonException) {}`） | ✅ 闭环 |
| **G** | 新增 `R16-real-machine-validation.md`"证据归档与透明度"小节，标注 UAC/真机/CI run URL 带外采信 | 全仓检索：无 `R16-real-machine-validation.md`；既有 `R16-real-machine-regression.md`（L1-110）**无**透明度小节/带外声明 | ❌ **未闭环** |

### 1.1 A 细节（M1 一致性）
- `BrowserCacheCleaner.cs` 两处遍历（目录大小统计 `L268-311` 与 `SafeGetAllFiles` `L313-...`）的**外层**（`!visited.Add(current)`）与**内层目录**（`!visited.Add(full)`）命中处均补 `Logger.Warning($"检测到重复遍历目录（疑似循环链接）…")` 并 `continue`/`return`。
- `FileMoverService.cs` 同样：外层 `L41-45`、内层目录 `L61-65` 补 `Logger.Warning`。
- 内层目录原为"只 `Contains` 不 `Add`"的潜在循环泄漏，现已统一为 `visited.Add(full)`（检查即登记），与参考实现 `TempFileCleaner.cs:271-273`（`!visited.Add(e.FullName)` → `Logger.Warning`）完全一致。

### 1.2 E 细节（fail-closed）
- 守卫逻辑位于独立提权助手 `DiskCleaner.Elevated/Program.cs`（非主程序 `Program.cs`）。异常分支 `L324-329`：`catch (Exception ex)` → 注释明示 "fail-closed：路径解析异常时保守视为受保护，拒绝操作（防止敌手利用异常绕过守卫）" → `Console.Error.WriteLine(...)` → `return true`。
- 注：此处用 `Console.Error.WriteLine` 而非 `Logger`，因 Elevated 为独立控制台 exe，无需引入 Logger 依赖；fail-closed 行为正确，可接受。

### 1.3 F 细节（安全收窄）
- 目标 JSON 解析兜底 catch 已收窄为 `JsonException` 并加注释说明"字段形状不匹配时静默继续尝试其他字段"。
- 同文件 `L184`（`catch (Exception ex)`，方法级返回失败结果）与 `L255`（`catch (Exception)`，HTTP 重试、满足条件 re-throw）为恰当的方法级/重试处理，非 F 所指"JSON 解析 catch"，保留合理。

---

## 2. CI workflow 三段断言完整性（build-and-smoke.yml）

| 段 | 行 | 断言 | 状态 |
|----|----|------|------|
| Build | L27-28 | `dotnet build -c Release --no-restore -p:TreatWarningsAsErrors=true` | ✅ 0 警强制门禁 |
| Smoke | L30-32 | `dotnet run --project src/DiskCleaner.SmokeTest --configuration Release`（exit 0=通过） | ✅ 无头冒烟 |
| Verify publish artifacts | L41-73 | ①无 PDB；②无 net6.0 残留；③`DiskCleaner.Elevated.exe` 存在；④`coreclr.dll` 存在（既有自包含断言）；⑤**新增** `DiskCleaner.Elevated.runtimeconfig.json` 存在且 `runtimeOptions.framework` 为空（无 framework 依赖） | ✅ 完整（D 闭环） |

---

## 3. GA 前置 A1（双 exe Authenticode 签名）

- 全仓检索 `signtool|SignTool|/sign|codesign|StrongNameKey|Authenticode`：命中项**全部**为运行时校验代码（`NativeMethods.IsAuthenticodeSigned`、`ElevationHelper` 要求 Release 版有效签名），**无**任何构建期签名步骤。
- `build-and-smoke.yml` 无 signtool 调用；三个 csproj 均无 `<SignAssembly>`/签名配置。
- 发布产物 `publish/` 目录存在，但无 CI 签名环节，签名须为分发前带外步骤。

> **结论：A1 仍为 GA 前置**（与 R8 一致，本轮未触及）。需：①对 `DiskCleanerPro.exe` + `DiskCleaner.Elevated.exe` 完成带时间戳 Authenticode 签名；②CI 加分发前签名断言。

---

## 4. 回归 / 遗漏扫描

- **无新增回归**：A–G 未引入任何新裸 `catch{}`。全仓剩余空 catch 仅 `SoftwareManager.cs:119`（`catch { /* 跳过无权限的键 */ }`）与 `:166`（`catch { resolvedFile = fileName; }`）——属 R8 既有 P3 项（#4/#6），**非本轮引入**，列为已知 GA 后项。
- **B 行为提示（非回归）**：`DiskAnalyzer` 两方法 catch 收窄为 `IOException`/`UnauthorizedAccessException` 后，其余意外异常（如 `PathTooLongException`、`ArgumentException`）将向上传播而非被静默吞掉——属修复预期的纵深加固，风险低、按设计。
- **G 缺口**：用户声"已修复 G"，但仓库中无目标文件、既有文档无透明度小节 → 声明与代码不符，须补。
- **A1 缺口**：R8 已列 GA 前置，本轮仍未做。
- **A2（R8 记录修正）**：本轮 A–G 实际代码修复已使 M1/固化自包含/fail-closed 真正闭环，原 A2"修正闭环记录"诉求已被本轮实质修复满足，可视为消解。

---

## 5. 最终 QA 签核

**🟡 条件 Go（GA）**

放行条件（须全部满足）：
1. **G 完成**：新增 `R16-real-machine-validation.md`（或于 `R16-real-machine-regression.md` 补）"证据归档与透明度"小节，显式标注 UAC / 真机 / CI run URL 等带外采信项。
2. **A1 完成**：双 exe 带时间戳 Authenticode 签名 + CI 分发前签名断言（仍为 GA 前置）。

**核心安全逻辑（解释器 EoP 守卫、受保护根 fail-closed、交接点/循环守卫、自包含发布）经代码 + CI 配置双重确认 intact**。遗留项均为文档透明度节与发布签名步骤，无产品逻辑缺陷。

---

## 6. 复检成员产出索引

- gstack-qa-lead 原始产出：`deliverables/gstack/R9-final-qa-recheck-diskcleaner-pro.md`（本文件：A–G file:line 核验表 + CI 三段断言 + A1 状态 + 回归扫描 + 最终签核）

> 本报告由软件工坊 AI 协作生成，关键决策请由工程负责人复核。

---

## 7. 处置记录（2026-07-23）

针对本报告 §5 列出的两项 GA 放行条件，已处置如下：

### 7.1 G 项（透明度文档缺失）→ ✅ 闭环
- 新增 `deliverables/R16-real-machine-validation.md`：含九场景真机闭环结论表 + 完整的「证据归档与透明度」小节，显式标注 UAC/回收站/Authenticode 真机点验与 CI 等价运行等为**带外采信项**（§2.1–2.2）。
- `deliverables/gstack/R16-real-machine-regression.md` 末尾补「证据归档与透明度（指向 validation 文档）」小节，形成双向引用。
- 说明：原对话记录称 G 已于第七轮闭环，但经核实 `R16-real-machine-validation.md` 实际未落盘，本次为真实补齐。

### 7.2 A1 项（双 exe 签名）→ 工具链就位，真实签名仍 GA 前置
- 新增 `scripts/sign-binaries.ps1`：用内置 `Set-AuthenticodeSignature`（无需 signtool）对 `DiskCleanerPro.exe` + `DiskCleaner.Elevated.exe` 做带时间戳 SHA256 Authenticode 签名；支持 PFX base64 环境变量，供用户购买证书后本地运行或 CI 调用。
- `build-and-smoke.yml` 新增两步：
  - `Sign binaries (Authenticode, GA gate)`：`CODESIGN_PFX_BASE64` secret 门控，有证书则实际签名（含时间戳）。
  - `Verify Authenticode signing (GA gate)`：分发前签名断言；常规 CI 未签名仅 warning，`vars.ENFORCE_SIGNING=true` 时未签名即 fail。
- 校验：`sign-binaries.ps1` PowerShell 语法 OK；`build-and-smoke.yml` YAML 解析 OK。

### 7.3 当前 GA 状态
- **G 项已满足**。
- **A1 代码/CI 侧已满足**；仅余「用户购买代码签名证书并执行签名」为 GA 放行前置的用户侧动作（沙箱无法代签，与原始 A1 结论一致）。
- 核心安全逻辑（解释器 EoP 守卫、受保护根 fail-closed、交接点/循环守卫、自包含发布）经代码 + CI 配置双重确认 intact。
