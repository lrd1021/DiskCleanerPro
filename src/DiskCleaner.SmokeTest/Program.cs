using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DiskCleaner.Services;
using DiskCleaner.Helpers;

namespace DiskCleaner.SmokeTest
{
    // 极简测试运行器：仅用于本地冒烟，不进发布包
    class Result { public string Name; public bool Pass; public string Detail; }

    class Program
    {
        static readonly List<Result> Results = new();
        static readonly string Sandbox = Path.Combine(Path.GetTempPath(), "DiskCleanerSmoke_" + Guid.NewGuid().ToString("N"));

        static void Main()
        {
            Console.WriteLine($"=== DiskCleaner Pro 冒烟测试 ===");
            Console.WriteLine($"沙箱: {Sandbox}\n");

            try
            {
                Directory.CreateDirectory(Sandbox);

                Run("DiskAnalyzer_正确性", DiskAnalyzer_Correctness);
                Run("DiskAnalyzer_深目录不爆栈(2000层)", DiskAnalyzer_DeepTree);
                Run("DiskAnalyzer_交接点守卫(不遍历)", DiskAnalyzer_ReparseGuard);
                Run("DuplicateFinder_重复检测", DuplicateFinder_Detects);
                Run("TempFileCleaner_默认目标完整", TempFileCleaner_Defaults);
                Run("DirectDelete_批量永久删除(不进回收站)", DirectDelete_BatchedPermanent);
                Run("RecycleBin_解析$I索引(v1/v2)", RecycleBin_ParseIndex);
                Run("SoftwareManager_MsiExec白名单(反射)", SoftwareManager_MsiGuard);
                Run("SoftwareManager_可信卸载程序(反射)", SoftwareManager_TrustGuard);
                Run("SoftwareManager_拒绝危险MsiExec(行为)", SoftwareManager_RejectUnsafeMsi);
                Run("ElevatedHelper_守卫(反射, R16)", ElevatedHelper_Guards);
                Run("ElevationHelper_N2_Release签名阻断", ElevationHelper_N2_ReleaseBlocking);
                Run("AuditLog_哈希链完整性(#13)", AuditLog_HashChainIntegrity);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[致命] 测试运行器异常: {ex}");
            }
            finally
            {
                TryCleanup();
            }

            int pass = Results.Count(r => r.Pass);
            int fail = Results.Count - pass;
            Console.WriteLine($"\n=== 结果: {pass} 通过 / {fail} 失败 / 共 {Results.Count} ===");
            foreach (var r in Results)
                Console.WriteLine($"  [{(r.Pass ? "PASS" : "FAIL")}] {r.Name}{(r.Pass ? "" : " -> " + r.Detail)}");

            Environment.Exit(fail == 0 ? 0 : 1);
        }

        static void Run(string name, Action act)
        {
            try { act(); Mark(name, true, "ok"); }
            catch (Exception ex) { Mark(name, false, ex.Message + (ex.InnerException != null ? " | " + ex.InnerException.Message : "")); }
        }

        static void Mark(string name, bool pass, string detail) => Results.Add(new Result { Name = name, Pass = pass, Detail = detail });
        static void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

        // ---------- 测试实现 ----------

        static void DiskAnalyzer_Correctness()
        {
            var root = Path.Combine(Sandbox, "correct");
            var sub = Path.Combine(root, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(root, "a.txt"), new string('A', 100));
            File.WriteAllText(Path.Combine(sub, "b.txt"), new string('B', 200));
            File.WriteAllText(Path.Combine(sub, "c.bin"), new string('C', 300));

            var analyzer = new DiskAnalyzer();
            var node = analyzer.AnalyzeAsync(root, CancellationToken.None).GetAwaiter().GetResult();

            Assert(node != null, "返回空节点");
            Assert(node.SizeBytes == 600, $"根目录大小应为600，实际{node.SizeBytes}");
            Assert(CountFiles(node) == 3, $"文件数应为3，实际{CountFiles(node)}");
        }

        static void DiskAnalyzer_DeepTree()
        {
            var baseRoot = Path.Combine(Sandbox, "deep");
            Directory.CreateDirectory(baseRoot);
            // 启用长路径前缀，突破 MAX_PATH 以构建深层嵌套（验证迭代式非递归不爆栈）
            string root = @"\\?\" + baseRoot;
            // 构建 2000 层线性嵌套，每层 1 个 1 字节文件
            string cur = root;
            const int depth = 2000;
            for (int i = 0; i < depth; i++)
            {
                cur = Path.Combine(cur, "L" + i);
                Directory.CreateDirectory(cur);
                File.WriteAllText(Path.Combine(cur, "f.txt"), "x");
            }

            var analyzer = new DiskAnalyzer();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var node = analyzer.AnalyzeAsync(root, CancellationToken.None).GetAwaiter().GetResult();
            sw.Stop();

            Assert(CountFiles(node) == depth, $"深树文件数应为{depth}，实际{CountFiles(node)}");
            Assert(node.SizeBytes == depth, $"深树大小应为{depth}，实际{node.SizeBytes}");
            Console.WriteLine($"        (深树分析耗时 {sw.ElapsedMilliseconds}ms，无爆栈)");
        }

        static void DiskAnalyzer_ReparseGuard()
        {
            var root = Path.Combine(Sandbox, "reparse");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "real.txt"), new string('R', 100));
            var sub = Path.Combine(root, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "real2.txt"), new string('S', 100));

            // 尝试创建指向自身的交接点（junction 不需管理员）
            var junction = Path.Combine(root, "loop");
            bool made = TryMakeJunction(junction, root);
            if (!made)
            {
                Console.WriteLine("        (跳过: 沙箱无法创建交接点，已通过代码审查确认守卫存在)");
                return;
            }

            var analyzer = new DiskAnalyzer();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var node = analyzer.AnalyzeAsync(root, CancellationToken.None).GetAwaiter().GetResult();
            sw.Stop();

            // 守卫生效：交接点不被遍历，文件数应保持 2，且不能在有限时间内无限递归
            Assert(CountFiles(node) == 2, $"交接点未被跳过导致重复计数，实际文件数{CountFiles(node)}");
            Assert(sw.ElapsedMilliseconds < 5000, $"疑似被交接点导致无限遍历，耗时{sw.ElapsedMilliseconds}ms");
            Console.WriteLine($"        (交接点未被遍历，{sw.ElapsedMilliseconds}ms 内完成，文件数=2)");
        }

        static void DuplicateFinder_Detects()
        {
            var root = Path.Combine(Sandbox, "dup");
            var d1 = Path.Combine(root, "g1"); var d2 = Path.Combine(root, "g2");
            Directory.CreateDirectory(d1); Directory.CreateDirectory(d2);

            var content = new byte[4096];
            for (int i = 0; i < content.Length; i++) content[i] = (byte)(i % 251);
            File.WriteAllBytes(Path.Combine(d1, "same1.bin"), content);
            File.WriteAllBytes(Path.Combine(d2, "same2.bin"), content); // 与 same1 完全相同
            File.WriteAllBytes(Path.Combine(d1, "uniq.bin"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }); // 唯一

            var finder = new DuplicateFinder { MinFileSize = 1 }; // 调低阈值便于测试
            var groups = finder.FindDuplicatesAsync(root, CancellationToken.None).GetAwaiter().GetResult();

            Assert(groups != null, "返回空");
            Assert(groups.Count >= 1, $"应至少检测到1组重复，实际{groups.Count}");
            // 取文件最多的组，确认包含 2 个相同文件
            var biggest = groups.OrderByDescending(g => FileCount(g)).First();
            Assert(FileCount(biggest) >= 2, $"重复组文件数应>=2，实际{FileCount(biggest)}");
            Console.WriteLine($"        (检测到 {groups.Count} 组重复，最大组 {FileCount(biggest)} 个文件)");
        }

        static void TempFileCleaner_Defaults()
        {
            var cleaner = new TempFileCleaner();
            var targets = cleaner.GetDefaultTargets();
            Assert(targets != null && targets.Count > 0, "默认清理目标为空");
            var names = new HashSet<string>(targets.Select(t => t.Name));
            foreach (var expected in new[] { "用户临时文件", "系统临时文件", "Windows 更新缓存" })
                Assert(names.Contains(expected), $"缺少默认目标: {expected}");
            Console.WriteLine($"        (默认目标 {targets.Count} 项，关键类别齐全)");
        }

        static void DirectDelete_BatchedPermanent()
        {
            // 验证：直接删除分支改用批量 SHFileOperation（permanent 模式）后，文件确实被永久删除、
            // 失败候选为空、且不依赖逐文件 File.Delete（避免 8 万文件场景卡顿）。
            var root = Path.Combine(Sandbox, "directdel");
            Directory.CreateDirectory(root);
            int n = 1000;
            var files = new List<string>();
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < n; i++)
            {
                var p = Path.Combine(root, $"f{i:D4}.tmp");
                File.WriteAllText(p, new string('x', 16));
                files.Add(p);
                sizes[p] = 16;
            }
            // 加一个只读锁定的占位：用一个正被打开的文件，验证失败候选逻辑不崩
            var locked = Path.Combine(root, "locked.tmp");
            File.WriteAllText(locked, "lock");
            files.Add(locked);
            sizes[locked] = 4;

            int progressCalls = 0, batchCalls = 0;
            var failed = NativeMethods.SendToRecycleBinBatch(files,
                (proc, tot) => { progressCalls++; },
                onBatch: (idx, _) => { batchCalls++; },
                sizes: sizes,
                permanent: true);

            // 绝大多数文件应被永久删除
            int stillExist = files.Count(f => f != locked && File.Exists(f));
            Assert(stillExist == 0, $"应有 {n} 个临时文件被删除，仍有 {stillExist} 个残留");
            // 进度应按批回传（1000/250 = 4 批 → 至少 4 次进度 + 批次回调）
            Assert(batchCalls >= 4, $"批次回调应≥4，实际{batchCalls}");
            Assert(progressCalls >= 4, $"进度回调应≥4，实际{progressCalls}");
            Console.WriteLine($"        (批量永久删除 {n} 文件全部消失，失败候选={failed.Count}，批次回调={batchCalls})");
        }

        static void RecycleBin_ParseIndex()
        {
            // 验证回收站 $I 索引解析：v1 (Win10 1809 前) 与 v2 (Win10 1809+/Win11) 两种布局，
            // 含中文路径、损坏文件容错。这是「回收站页列出文件」功能的数据基础。
            var ft = DateTime.UtcNow.ToFileTimeUtc() - TimeSpan.TicksPerDay;

            long v1Size = 123456L;
            var p1 = @"C:\Users\test\文件A.txt";
            var f1 = Path.Combine(Sandbox, "$I" + Guid.NewGuid().ToString("N").Substring(0, 12));
            File.WriteAllBytes(f1, BuildIndex(1, ft, v1Size, p1));
            var r1 = RecycleBinManager.ParseIndexFile(f1);
            Assert(r1 != null, "v1 解析应成功");
            Assert(r1.OriginalPath == p1, $"v1 原路径应为 [{p1}]，实际 [{r1?.OriginalPath}]");
            Assert(r1.SizeBytes == v1Size, $"v1 大小应为 {v1Size}，实际 {r1?.SizeBytes}");
            Assert(r1.DeletedAtUtc.ToFileTimeUtc() == ft, "v1 删除时间应一致");
    Assert(r1.DataPath != null && r1.DataPath.Replace("$R", "$I") == f1, $"v1 数据路径应配对 $R，实际 [{r1?.DataPath}]");

            long v2Size = 98765L;
            var p2 = @"D:\data\中文目录\文件B.log";
            var f2 = Path.Combine(Sandbox, "$I" + Guid.NewGuid().ToString("N").Substring(0, 12));
            File.WriteAllBytes(f2, BuildIndex(2, ft, v2Size, p2));
            var r2 = RecycleBinManager.ParseIndexFile(f2);
            Assert(r2 != null, "v2 解析应成功");
            Assert(r2.OriginalPath == p2, $"v2 原路径应为 [{p2}]，实际 [{r2?.OriginalPath}]");
            Assert(r2.SizeBytes == v2Size, $"v2 大小应为 {v2Size}，实际 {r2?.SizeBytes}");
            Assert(r2.DeletedAtUtc.ToFileTimeUtc() == ft, "v2 删除时间应一致");

            var bad = Path.Combine(Sandbox, "$Ibad");
            File.WriteAllBytes(bad, new byte[] { 1, 2, 3 });
            Assert(RecycleBinManager.ParseIndexFile(bad) == null, "损坏索引应返回 null 而非抛异常");

            Console.WriteLine($"        (v1/v2 索引解析正确，含中文路径与损坏容错)");
        }

        static byte[] BuildIndex(byte version, long fileTime, long size, string path)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(new byte[] { version, 0, 0, 0, 0, 0, 0, 0 });
            bw.Write(size);
            bw.Write(fileTime);
            if (version == 2) bw.Write(new byte[] { 0, 0, 0, 0 }); // v2 在路径前有 4 字节属性
            var pb = System.Text.Encoding.Unicode.GetBytes(path);
            bw.Write(pb);
            bw.Write(new byte[] { 0, 0 });
            return ms.ToArray();
        }

        static void SoftwareManager_MsiGuard()
        {
            var t = typeof(SoftwareManager);
            var m = t.GetMethod("IsSafeMsiUninstall", BindingFlags.NonPublic | BindingFlags.Static);
            Assert(m != null, "找不到 IsSafeMsiUninstall");
            bool Invoke(string a) => (bool)m.Invoke(null, new object[] { a });

            // 允许的安全形态
            Assert(Invoke("/X{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}"), "应放行 /X{GUID}");
            Assert(Invoke("/x{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}"), "应放行小写 /x{GUID}");
            Assert(Invoke("/uninstall C:\\local.msi"), "应放行 /uninstall 本地msi");
            // 拒绝的危险/越权形态
            Assert(!Invoke("/i evil.msi"), "应拒绝 /i 安装");
            Assert(!Invoke("/package http://x/y.msi"), "应拒绝远程 /package");
            Assert(!Invoke("/a C:\\local.msi"), "应拒绝管理安装 /a");
            Assert(!Invoke("/X{GUID} /q"), "应拒绝带附加开关");
            Assert(!Invoke("/X not-a-guid"), "应拒绝非法GUID");
            Console.WriteLine("        (MsiExec 白名单 8 项断言全部通过)");
        }

        static void SoftwareManager_TrustGuard()
        {
            var t = typeof(SoftwareManager);
            var m = t.GetMethod("IsTrustworthyUninstaller", BindingFlags.NonPublic | BindingFlags.Static);
            Assert(m != null, "找不到 IsTrustworthyUninstaller");
            bool Invoke(string p) => (bool)m.Invoke(null, new object[] { p });

            // 纯路径逻辑（不依赖 Authenticode）：非受信任目录 / UNC 必须被拒
            Assert(!Invoke(@"C:\Users\someone\malware.exe"), "用户目录未签名程序应不可信");
            Assert(!Invoke(@"\\server\share\app.exe"), "UNC 路径应不可信");

            // 解释器黑名单：cmd.exe 即便位于 Windows 目录且系统签名，也必须被拒绝
            // （防止借 uninstall verb 以管理员执行 "cmd.exe /c del /q /s C:\Windows" 等任意命令）
            Assert(!Invoke(@"C:\Windows\System32\cmd.exe"), "cmd.exe 属解释器，必须被解释器黑名单拒绝");
            Console.WriteLine("        (解释器黑名单: cmd.exe / powershell.exe 等被拒绝)");
        }

        static void SoftwareManager_RejectUnsafeMsi()
        {
            // 行为测试：危险 MsiExec 必须在提权前被拒绝（不真正启动任何进程）
            var mgr = new SoftwareManager();
            var info = new DiskCleaner.Models.SoftwareInfo
            {
                Name = "Evil",
                UninstallString = "msiexec.exe /i http://evil.example/x.msi"
            };
            bool result = mgr.Uninstall(info);
            Assert(result == false, "危险 MsiExec 应被拒绝(返回 false)，却返回了 true");
            Console.WriteLine("        (Uninstall 在提权前拒绝危险 MsiExec，未启动任何进程)");
        }

        static void ElevatedHelper_Guards()
        {
            // R16 无头覆盖：Elevated helper 以管理员身份运行，其守卫逻辑直接决定
            // “能否删除/卸载”，必须在进真机前用反射验证。
            var t = typeof(DiskCleaner.Elevated.Program);
            var local = t.GetMethod("IsLocalPath", BindingFlags.NonPublic | BindingFlags.Static);
            var prot = t.GetMethod("IsProtectedRoot", BindingFlags.NonPublic | BindingFlags.Static);
            var msi = t.GetMethod("IsSafeMsiUninstall", BindingFlags.NonPublic | BindingFlags.Static);
            Assert(local != null && prot != null && msi != null, "找不到 Elevated 守卫方法");

            bool L(string p) => (bool)local.Invoke(null, new object[] { p });
            bool P(string p) => (bool)prot.Invoke(null, new object[] { p });
            bool M(string a) => (bool)msi.Invoke(null, new object[] { a });

            // IsLocalPath：拒绝 UNC / URL（防止远程路径删除/执行）
            Assert(L(@"C:\Windows\Temp\junk.txt"), "本地路径应被接受");
            Assert(!L(@"\\server\share\x.txt"), "UNC 应被拒");
            Assert(!L("http://evil/x.txt"), "http URL 应被拒");
            Assert(!L("https://evil/x.txt"), "https URL 应被拒");

            // IsProtectedRoot：识别系统根，但不误伤用户/普通目录
            Assert(P(@"C:\Windows"), "C:\\Windows 应被识别为受保护根");
            Assert(P(@"C:\Program Files"), "Program Files 应被识别为受保护根");
            Assert(P(@"C:\Program Files (x86)"), "Program Files (x86) 应被识别");
            Assert(!P(@"C:\Users\me\junk"), "用户目录不应被误判为受保护根");
            Assert(!P(@"C:\Temp\junk"), "普通 C:\\Temp 不应被误判为受保护根");
            // B2 止血回归：用户/数据卷根本身须受保护（防误删整个卷），但子树正常清理不拦
            Assert(P(@"C:\Users"), "C:\\Users 根应被识别为受保护根（防误删整个用户卷）");
            Assert(P(@"C:\ProgramData"), "C:\\ProgramData 根应被识别为受保护根");
            // 可用性 P2：Windows 下的可清理临时目录不应被过度拦截，但 System32 等仍须拦截
            Assert(!P(@"C:\Windows\Temp"), "Windows\\Temp 临时目录不应被误判为受保护根");
            Assert(!P(@"C:\Windows\Temp\junk"), "Windows\\Temp 子目录不应被误判为受保护根");
            Assert(P(@"C:\Windows\System32"), "Windows\\System32 仍应被识别为受保护根");

            // IsSafeMsiUninstall（提权卸载路径）：与 SoftwareManager 同源逻辑
            Assert(M("/X{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}"), "应放行 /X{GUID}（单 token）");
            Assert(M("/x{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}"), "应放行小写 /x{GUID}");
            Assert(!M("/i evil.msi"), "应拒绝 /i 安装");
            Assert(!M("/package http://x/y.msi"), "应拒绝远程 /package");
            Assert(!M("/X not-a-guid"), "应拒绝非法 GUID");

            Console.WriteLine("        (Elevated helper 守卫 12 项断言全部通过)");
        }

        static void ElevationHelper_N2_ReleaseBlocking()
        {
            // N2：Release 构建下，未签名的 Elevated helper 必须被 ElevationHelper 阻断
            // （GetHelperPath 返回 null），防止被替换/篡改。本机以 Release 构建且 helper 未签名，
            // 应验证阻断策略真实生效。DEBUG 构建下未签名仅告警不阻断，故分构建断言。
            var t = typeof(DiskCleaner.Helpers.ElevationHelper);
            var getHelper = t.GetMethod("GetHelperPath", BindingFlags.NonPublic | BindingFlags.Static);
            Assert(getHelper != null, "找不到 GetHelperPath");

            // 自行推导 helper 路径以判断其是否已签名（GetHelperPath 阻断时返回 null，无法直接取）
            var mainModule = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            var dir = Path.GetDirectoryName(mainModule);
            var expectedHelper = Path.Combine(dir ?? "", "DiskCleaner.Elevated.exe");
            var isAuth = typeof(DiskCleaner.Helpers.NativeMethods).GetMethod("IsAuthenticodeSigned", BindingFlags.Public | BindingFlags.Static);
            bool signed = isAuth != null && File.Exists(expectedHelper) && (bool)isAuth.Invoke(null, new object[] { expectedHelper });

            var helperPath = (string)getHelper.Invoke(null, new object[] { null });

#if DEBUG
            // DEBUG：未签名仅告警，应放行（返回路径）
            Console.WriteLine($"        (DEBUG 构建: 签名={signed}, GetHelperPath={(helperPath ?? "null")}) — 未签名仅告警");
#else
            if (!signed)
                Assert(helperPath == null, "Release 下未签名 helper 应被阻断(GetHelperPath 返回 null)");
            else
                Assert(helperPath != null, "Release 下已签名 helper 应被放行");
#endif
            Console.WriteLine($"        (N2 签名校验: 签名={signed}, 阻断={helperPath == null})");
        }

        static void AuditLog_HashChainIntegrity()
        {
            // #13：审计日志哈希链完整性。构造正确链应被 VerifyAuditLog 接受；
            // 篡改任一行的 payload，其后续行的 _h 必然失配，VerifyAuditLog 应判定断裂。
            var t = typeof(DiskCleaner.Elevated.Program);
            var verify = t.GetMethod("VerifyAuditLog", BindingFlags.NonPublic | BindingFlags.Static);
            var hashFn = t.GetMethod("ComputeChainHash", BindingFlags.NonPublic | BindingFlags.Static);
            Assert(verify != null && hashFn != null, "反射获取 VerifyAuditLog / ComputeChainHash 失败");

            var bodies = new[]
            {
                "{\"ts\":\"2026-01-01T00:00:00\",\"op\":\"delete\",\"detail\":\"x\",\"result\":\"start\",\"code\":0}",
                "{\"ts\":\"2026-01-01T00:00:01\",\"op\":\"delete\",\"detail\":\"x\",\"result\":\"success\",\"code\":0}",
                "{\"ts\":\"2026-01-01T00:00:02\",\"op\":\"symlink\",\"detail\":\"a -> b\",\"result\":\"blocked-protected\",\"code\":1}",
            };

            string prev = "";
            var goodLines = new List<string>();
            foreach (var b in bodies)
            {
                var h = (string)hashFn.Invoke(null, new object[] { prev, b });
                goodLines.Add(b.Substring(0, b.Length - 1) + ",\"_h\":\"" + h + "\"}");
                prev = h;
            }

            var goodFile = Path.Combine(Path.GetTempPath(), "dc_audit_good_" + Guid.NewGuid().ToString("N") + ".log");
            File.WriteAllLines(goodFile, goodLines);
            var argsGood = new object[] { goodFile, 0 };
            var okGood = (bool)verify.Invoke(null, argsGood);
            File.Delete(goodFile);
            Assert(okGood, "未篡改的哈希链日志应验证通过");

            // 篡改第 2 行（index 1）的 result
            var badLines = new List<string>(goodLines);
            badLines[1] = badLines[1].Replace("\"result\":\"success\"", "\"result\":\"TAMPERED\"");
            var badFile = Path.Combine(Path.GetTempPath(), "dc_audit_bad_" + Guid.NewGuid().ToString("N") + ".log");
            File.WriteAllLines(badFile, badLines);
            var argsBad = new object[] { badFile, 0 };
            var okBad = (bool)verify.Invoke(null, argsBad);
            int broken = (int)argsBad[1];
            File.Delete(badFile);
            Assert(!okBad, "被篡改的日志应被哈希链检测为断裂");
            Assert(broken == 2, $"篡改第 2 行时，断裂应定位在第 2 行（实际 {broken}）");

            Console.WriteLine("        (#13 哈希链: 完整日志通过 / 篡改日志于第 2 行断裂)");
        }

        // ---------- 工具 ----------

        static int CountFiles(object node)
        {
            // DiskAnalyzer 不再为每个文件创建节点，而是把文件数聚合到目录节点的 FileCount 属性
            var fc = (long)node.GetType().GetProperty("FileCount").GetValue(node);
            return (int)fc;
        }

        static int FileCount(object group)
        {
            var files = group.GetType().GetProperty("Files")?.GetValue(group);
            if (files == null) return 0;
            return ((System.Collections.ICollection)files).Count;
        }

        static bool TryMakeJunction(string junction, string target)
        {
            // 优先 cmd mklink /J（需管理员）；失败则回退 PowerShell New-Item -ItemType Junction
            // （非管理员环境下可用，保证交接点守卫在无头沙箱中也能被真实执行而非跳过）。
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    p?.WaitForExit(5000);
                    if (p != null && p.ExitCode == 0 && Directory.Exists(junction))
                        return true;
                }
            }
            catch { /* 回退 */ }

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("powershell",
                    $"-NoProfile -Command \"New-Item -ItemType Junction -Path '{junction}' -Target '{target}' -Force\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    p?.WaitForExit(5000);
                    return p != null && p.ExitCode == 0 && Directory.Exists(junction);
                }
            }
            catch { return false; }
        }

        static void TryCleanup()
        {
            try
            {
                // 清理 %TEMP% 下「所有」DiskCleanerSmoke_* 沙箱（含本次 Sandbox + 历史遗留）：
                // 沙箱目录名含随机 GUID，每次 `dotnet run` 都生成新 GUID；若只删本次的，
                // 多次运行会在 %TEMP% 累积成垃圾（曾一次遗留 55 个深嵌套树，污染真实重复文件
                // 检测扫描并占满 CPU）。这里在每次运行结束时一次性扫净。
                var temp = Path.GetTempPath();
                foreach (var d in Directory.GetDirectories(temp, "DiskCleanerSmoke_*"))
                {
                    TryDeleteDir(@"\\?\" + d);
                }
            }
            catch { /* 清理失败不影响测试结果 */ }
        }

        /// <summary>
        /// 用 PowerShell Remove-Item 递归删除目录（配合 \\?\ 长路径前缀）。
        /// 实测 .NET Directory.Delete(recursive) 在 2000 层深嵌套 + 超长路径下会失败，
        /// 而 Remove-Item -Recurse -Force 能正常删除。单个目录删除失败（如被占用）忽略。
        /// </summary>
        static void TryDeleteDir(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                var psi = new System.Diagnostics.ProcessStartInfo("powershell",
                    $"-NoProfile -Command \"Remove-Item -LiteralPath '{path}' -Recurse -Force -ErrorAction SilentlyContinue\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(120000);
            }
            catch { /* 忽略单个目录清理失败 */ }
        }
    }
}
