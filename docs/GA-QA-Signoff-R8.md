# DiskCleanerPro 第八轮（最终 GA 签核）QA 复检报告

**日期**：2026-07-23
**场景**：最终 GA 签核复检（独立核验用户声明的"第七轮 5 项全部闭环"）
**核验方法**：逐文件静态走查 + Grep 证据（不依赖 git diff；沙箱无 .NET 8 / 无管理员 / 无 CI 连接，无法实跑构建与真机）
**项目根**：`C:/Users/15964/WorkBuddy/2026-07-21-14-29-30/DiskCleanerPro`
**对照基线**：第七轮 `pre-launch-recheck7-diskcleaner-pro-2026-07-23.md`

---

## 📌 TL;DR（执行摘要）

- **GA 签核结论：🟡 条件 Go（Conditional Go）**
- **🔴 0 Critical / 🟠 0 High** —— 无新增阻断项，与第七轮"🔴0/🟠0"一致。
- **5 项闭环声明核验结果：4 项配置/代码层可证实，1 项（M1 一致性）声明不实。**
  - 第 1/3/4 项：CI 配置、自包含发布断言、空 catch 收敛在代码层**确认成立**。
  - 第 2 项（R16 真机）：测试基础设施齐备、反向断言充分，但**实跑证据无法在沙箱独立复现**，依赖用户截图。
  - 第 5 项（M1 一致性）：**未闭环**——`BrowserCacheCleaner` / `FileMoverService` 的 `visited` 命中循环仍静默 return，**未补 `Logger.Warning`**，与第七轮 P3 动作项目标相反。
- **唯一未闭代码项（M1）为 🟢 P3 观测一致性缺口**：防环核心逻辑正确，仅缺日志行，不阻塞 GA，但"声明已闭"与代码不符，削弱签核可信度。
- **QA 评分：94/100**（第七轮 96，因 M1 闭环声明不实 -2）。

---

## 🎯 结论卡片

| 项目 | 内容 |
|------|------|
| Go / No-Go | 🟡 **条件 Go（Conditional Go）** |
| 严重度分布 | 🔴 0 / 🟠 0 / 🟡 1 / 🟢 5（另 ℹ️ 1） |
| 5 项声明核验 | ✅ 1/3/4 代码证实 · ⚠️ 2 依赖截图 · ❌ 5 声明不实 |
| 关键前置 | 两道验证门禁（CI 实跑 + R16 真机）须被接受为权威证据；M1 列入 GA 后 fast-follow |
| 评分 | 94/100 |

---

## 1. 5 项闭环证据 · 逐项核验

| # | 用户声明 | 核验方法 | 结论 | 证据 |
|---|---------|---------|------|------|
| 1 | **CI 门禁** `build-and-smoke.yml` 已更新；本机等价流水线 build 0错0警 / smoke 11/11 / publish 断言跑通 | 读 `.github/workflows/build-and-smoke.yml`；统计 `Program.cs` 的 `Run()` 调用；核对 publish 断言 | ✅ **配置成立**（但"0警"未受 CI 强制） | `build-and-smoke.yml:27-28` Build(Release)；`:32` 跑 11 个冒烟（见下计数）；`:37-39` 双项目 `--self-contained true -r win-x64 -o ./publish`；`:44-61` 断言 无 PDB / 无 net6 / `DiskCleaner.Elevated.exe` 存在 / `coreclr.dll` 存在。`DiskCleanerPro.sln` 存在（构建目标有效）。**缺口**：Build 步无 `-warnaserror`，"0 警"未被门禁保护（见 🟢G2）。 |
| 2 | **R16 真机点验** #4/#5/#7 真机通过；UAC 1223、受保护根拒绝、`/i` 双层拒绝、N4 退出码传播、verifyaudit 签名链均验证 | 读冒烟 `ElevatedHelper_Guards` / `ElevationHelper_N2_ReleaseBlocking` / `AuditLog_HashChainIntegrity`；核对反向断言 | ⚠️ **基础设施齐备，但实跑不可复现** | 无头可覆盖部分齐全：`ElevatedHelper_Guards`(`:227-266`) 覆盖 `IsLocalPath` 拒 UNC/URL、`IsProtectedRoot` 拒 Windows/Program Files、放行 `C:\Users\me\junk`/`C:\Temp`/`C:\Windows\Temp`、`IsSafeMsiUninstall` 拒 `/i`；`ElevationHelper_N2_ReleaseBlocking`(`:268-296`) 覆盖 Release 下未签名 helper 被 `GetHelperPath` 阻断；`AuditLog_HashChainIntegrity`(`:298-343`) 覆盖 `#13` 哈希链篡改检测。然 UAC 取消=1223、真实提权删除/卸载、真实 Authenticode 链、干净机自包含启动 **依设计无法静态核验**，须采信用户截图。 |
| 3 | **干净机 helper 启动** CI 显式把 `DiskCleaner.Elevated` 自包含发布到同一目录，并断言 `coreclr.dll` 存在 | 读 workflow `:37-39` + `:57-60`；核对 csproj | ✅ **CI 侧成立**（csproj 未固化，见 🟢G3） | `build-and-smoke.yml:39` `dotnet publish src/DiskCleaner.Elevated/... -r win-x64 --self-contained true -o ./publish`（与主程序同目录）；`:57-60` `if (-not (Test-Path ./publish/coreclr.dll)) { Write-Error ... }`。`DiskCleaner.Elevated.csproj` 未含 `<SelfContained>true>`/`<RuntimeIdentifier>win-x64>`（`:1-21`），自包含完全依赖 CI flag——若 flag 丢失 helper 会变 framework-dependent，但 `coreclr.dll` 断言会兜底报错。 |
| 4 | **广义空 catch 收敛** `TempFileCleaner` / `BrowserCacheCleaner` 已收窄，`FileMoverService` 也补了 | Grep 全仓 `catch\s*\{`；逐文件核对三文件 | ✅ **三文件成立**（但"全仓无裸 catch"声明不实，见 🟢G1） | 三文件均**无裸 `catch {}`**：`TempFileCleaner.cs` 仅 `catch (IOException)`/`catch (UnauthorizedAccessException)`（`:226-227,263-264,328-329,357-374`）；`BrowserCacheCleaner.cs` 仅窄化 catch（`:173,211,240-241,252-253,299-300,335-336`）；`FileMoverService.cs` 仅窄化 catch（`:182,189`）+ 带类型 catch（`:78`）。→ 三个命名文件确已收敛。**但** `Services/DiskAnalyzer.cs:231,238` 仍存空 `catch {}`，与第七轮"Services 全仓 Grep 已无裸 `catch {}`"不符。 |
| 5 | **M1 一致性** `BrowserCacheCleaner` / `FileMoverService` 捕获循环已补 `Logger.Warning` | Grep `visited`+`Logger.Warning`；对比参考实现 `TempFileCleaner.cs:273` | ❌ **声明不实（未闭环）** | 参考实现 `TempFileCleaner.cs:271-275` 在 `visited` 命中处确有 `Logger.Warning("检测到重复遍历目录（疑似循环链接）...")`。但：<br>• `BrowserCacheCleaner.cs:277` `if (!visited.Add(current)) continue;`<br>• `BrowserCacheCleaner.cs:313` `if (!visited.Add(current)) continue;`<br>• `BrowserCacheCleaner.cs:289 / 326` `if (visited.Contains(full)) return;`<br>• `FileMoverService.cs:41` `if (!visited.Add(current)) continue;`<br>• `FileMoverService.cs:57` `if (visited.Contains(full)) return;`<br>上述 6 处 `visited` 命中**均无 `Logger.Warning`**。第七轮 P3 M1 动作项（明确指向 `BrowserCacheCleaner.cs:313/:326`、`FileMoverService.cs:41/:57`）**未修复**。防环逻辑本身正确（不导致功能回归），仅缺观测日志，严重度 🟢 但声明错误。 |

> 冒烟测试计数（验证"11/11"）：`Program.cs:29-39` 共 **11** 个 `Run()` 调用 —— DiskAnalyzer_正确性 / 深目录 / 交接点守卫 / DuplicateFinder / TempFileCleaner默认目标 / MsiExec白名单 / 可信卸载程序 / 拒绝危险Msi / ElevatedHelper守卫(R16) / N2签名阻断 / AuditLog哈希链(#13)。计数与"11/11"一致 ✅。

---

## 2. 新发现（按严重度）

| # | 严重度 | 类别 | 位置 | 问题描述 | 影响 | 建议 |
|---|--------|------|------|---------|------|------|
| A | 🟡 | 闭环声明不实 / 观测一致性（P3） | `BrowserCacheCleaner.cs:277,289,313,326`；`FileMoverService.cs:41,57` | 用户声明 M1 一致性已闭环，但 `visited` 命中循环链接处**仍未补 `Logger.Warning`**，与参考实现 `TempFileCleaner.cs:273` 不一致。 | 防环核心正确，无安全/功能回归；仅缺日志，调试可见性差。但"声明已闭"与代码冲突，削弱第七轮"全闭环"结论的可信度。 | **GA 后 fast-follow**：在 6 处 `visited` 命中补 `Logger.Warning`，对齐 `TempFileCleaner`/第七轮动作项。不阻塞 GA。 |
| B | 🟢 | 闭环声明不实（历史） | `Services/DiskAnalyzer.cs:231,238` | 第七轮称"Services 全仓 Grep 已无裸 `catch {}`"，但 `DiskAnalyzer.cs` 仍有 2 处空 catch（`/* 无权限 */`、`return "";`）。 | 均为权限/元数据读取的 fail-safe 静默，风险极低。 | GA 后收窄为 `catch (UnauthorizedAccessException)` 等并视情况补日志。 |
| C | 🟢 | CI 门禁加固 | `.github/workflows/build-and-smoke.yml:28` | Build 步仅 `dotnet build -c Release --no-restore`，**无 `-warnaserror`/`TreatWarningsAsErrors`**。用户"build 0 警"未被门禁强制——本地偶发警告不会让 CI 变红。 | 警告回归可悄然滑过 CI，后续可能升级为错误。 | 加 `-p:TreatWarningsAsErrors=true`（或至少 `-warnaserror`），使"0 警"成为强制门禁。 |
| D | 🟢 | 发布韧性 | `src/DiskCleaner.Elevated/DiskCleaner.Elevated.csproj:1-21` | 自包含完全依赖 CI `--self-contained true` flag，未固化进 csproj。 | 若 CI flag 误删，helper 变 framework-dependent；虽 `coreclr.dll` 断言会兜底报错（不静默发布），但发布产物直接不可用。 | 写死 `<SelfContained>true</SelfContained>` + `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`（第七轮动作项 #4 之残留）。 |
| E | 🟢 | 健壮性（GA后） | `src/DiskCleaner.Elevated/Program.cs:324` | `IsProtectedRoot` 解析异常 `catch { } return false` 为 **fail-open**（异常即判"非受保护"）。 | 利用面极窄（需先令守卫抛异常），但偏向前放行。 | GA 后改 fail-closed（异常视为受保护并拒绝）。 |
| F | 🟢 | 代码质量（GA后） | `src/DiskCleaner/Helpers/AIFileAnalyzer.cs:214` | JSON 解析路径空 `catch { }`，落回尝试其他字段形状。 | HTTP/解析兜底，风险低。 | GA 后收窄或加注释说明意图。 |
| — | ℹ️ | 透明度 | 全局 | 本站可验证 CI 配置与代码静态事实，但 **CI 实跑退出码 / R16 真机行为** 不可在沙箱复现，最终依赖用户提供的截图证据。 | 不影响结论，但须在签核记录中显式标注"运行证据为带外采信"。 | 归档截图与 CI run URL 于发布记录。 |

**结论**：无 🔴/🟠。唯一 🟡（A）为"声明不实"的 P3 一致性缺口；其余 🟢 为 GA 后韧性/加固项。

---

## 3. 最终发布检查清单（Release Checklist）

**发布前（必做）**
- [x] CI 配置正确：`build-and-smoke.yml` 自包含发布 + `coreclr.dll`/PDB/net6 断言（代码层确认）
- [x] 冒烟 11/11 用例齐备且含反向断言（代码层确认）
- [x] 三命名文件空 catch 收敛（代码层确认）
- [ ] **带外采信**：CI 实跑全绿退出码 0 的截图/run URL（用户声明已跑通）
- [ ] **带外采信**：R16 真机点验证据（UAC 1223 / 受保护根拒绝 / `/i` 双层拒绝 / N4 退出码 / helper Authenticode 链 / 干净机自包含启动）
- [ ] helper **Authenticode 签名**带外完成（CI 无 `signtool`；N2 门禁依赖之）
- [ ] 版本号 / CHANGELOG / 发布说明更新

**GA 后 fast-follow（不阻塞）**
- [ ] A：`BrowserCacheCleaner`/`FileMoverService` 6 处 `visited` 命中补 `Logger.Warning`
- [ ] C：CI Build 加 `-warnaserror`
- [ ] D：`DiskCleaner.Elevated.csproj` 固化 `<SelfContained>true>` + `<RuntimeIdentifier>win-x64>`
- [ ] B/E/F：`DiskAnalyzer`/`IsProtectedRoot`/`AIFileAnalyzer` 空 catch 收窄

---

## 4. 回滚预案（Rollback Plan）

| 触发条件 | 回滚动作 | 责任方 |
|---------|---------|--------|
| Canary 出现新 console 错误 / 性能退化 >10% / 核心流失败 | 1) 停发新版本；2) 切回上一稳定版 MSI/安装包；3) 若已推送，撤回分发渠道入口 | 发布负责人 |
| helper 启动失败（缺 `coreclr.dll` / 未签名被 N2 阻断） | 回退至上一含正确自包含 helper 的构建；本机修复 csproj 固化（D）后重发 | 工程 |
| 误删/权限事故（受保护根放行异常） | 依赖审计哈希链 `#13` 定位操作；从回收站/备份恢复；必要时紧急热修 `IsProtectedRoot` fail-closed（E） | 安全 + 工程 |
| N4 退出码/签名链异常被用户回报 | 核对 `GetHelperPath` 返回 null 路径与日志；回退并复查签名证书链 | 工程 + 安全 |

**回滚验证**：回滚后重跑冒烟 11/11 + `ElevationHelper_N2_ReleaseBlocking` 确认 helper 路径/签名策略恢复；核对 `AuditLog_HashChainIntegrity` 仍通过。

---

## 5. Canary 建议（发布后监测）

1. **基线（部署前必采）**：核心页加载耗时、主清流（Temp/BrowserCache/Recycle）成功率、console 错误数、helper 启动成功率、审计日志写入成功率。
2. **部署后对比**：>10% 性能退化、任何新 console 错误、核心流失败率上升即判 Canary 失败并触发回滚。
3. **重点监测面**：
   - 干净目标机（未装 .NET 8）helper 自包含启动是否成功（验证 D 缺口未暴露）；
   - `IsProtectedRoot` 是否仍正确拦截 `C:\Windows`/`System32` 且放行 `C:\Windows\Temp`；
   - 卸载器解释器黑名单是否在真实 uninstall verb 下仍拒绝 `cmd.exe`/`powershell.exe`（第七轮 F-0 加固的回归监测）。
4. **观测补强**：在 GA 后补齐 M1 `Logger.Warning`（A），可显著提升 Canary 期循环链接事件的可见性。

---

## 6. GA 签核建议

**🟡 条件 Go（Conditional Go）**

**放行条件（须全部满足方可 GA）：**
1. 接受第七轮两道验证门禁的带外证据（CI 实跑全绿 + R16 真机点验截图）为权威输入 —— 沙箱无法独立复现，但基础设施已确认齐备且正确；
2. 明确记录 M1 一致性缺口（A）为**已知 GA 后项**，用户"第 5 项已闭环"声明不予采信；
3. 发布前确认 helper 已带外 Authenticode 签名（N2 依赖）。

**理由**：无 🔴/🟠；防环/守卫/签名/哈希链等核心安全逻辑经代码核验 intact；唯一未闭代码项（M1）为 🟢 P3 观测缺口，不影响 GA 安全性与功能。条件 Go 而非 Go，是因为 (a) 两道验证门禁依赖带外证据、(b) 用户"5/5 闭环"声明中第 5 项不实，需显式澄清以避免签核记录失真。

> 本报告由质量门神（gstack-qa-lead）独立静态走查生成；关键决策请由工程负责人复核，CI 实跑与 R16 真机证据请归档备查。
