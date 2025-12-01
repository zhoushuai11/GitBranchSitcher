using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace GitBranchSwitcher
{
    public static class GitHelper
    {
        // ==================== 基础辅助方法 ====================

        public static string? FindGitRoot(string startPath)
        {
            var dir = new DirectoryInfo(startPath);
            while (dir != null)
            {
                var gitDir = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(gitDir) || File.Exists(gitDir)) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        public static string GetFriendlyBranch(string repoPath)
        {
            { var (c, s, _) = RunGit(repoPath, "branch --show-current", 15000); if (c == 0 && !string.IsNullOrWhiteSpace(s)) return s.Trim(); }
            { var (c, s, _) = RunGit(repoPath, "rev-parse --abbrev-ref HEAD", 15000); if (c == 0 && !string.IsNullOrWhiteSpace(s) && s.Trim() != "HEAD") return s.Trim(); }
            { var (c, s, _) = RunGit(repoPath, "rev-parse --short=7 HEAD", 15000); if (c == 0 && !string.IsNullOrWhiteSpace(s)) return $"(detached @{s.Trim()})"; }
            return "(unknown)";
        }

        public static IEnumerable<string> GetAllBranches(string repoPath)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 本地分支
            {
                var (code, stdout, _) = RunGit(repoPath, "for-each-ref --format=%(refname:short) refs/heads", 20000);
                if (code == 0) foreach (var l in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) set.Add(l.Trim());
            }
            // 远程分支
            {
                var (code, stdout, _) = RunGit(repoPath, "for-each-ref --format=%(refname:short) refs/remotes/origin", 20000);
                if (code == 0) foreach (var l in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var name = l.Trim();
                        if (name.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase)) continue;
                        var idx = name.IndexOf('/');
                        set.Add(idx >= 0 ? name[(idx + 1)..] : name);
                    }
            }
            return set;
        }

        // [新增] 快速 Fetch 方法，用于后台更新分支列表
        public static void FetchFast(string repoPath)
        {
            // 15秒超时，只拉取不带 tags，尽可能快
            RunGit(repoPath, "fetch origin --prune --no-tags", 15000);
        }

        private static bool HasLocalChanges(string repoPath)
        {
            var (code, stdout, _) = RunGit(repoPath, "status --porcelain", 15000);
            return code == 0 && !string.IsNullOrWhiteSpace(stdout);
        }

        // ==================== 核心切线逻辑 ====================

        public static (bool ok, string message) SwitchAndPull(string repoPath, string targetBranch, bool useStash, bool fastMode)
        {
            var log = new StringBuilder();
            void Step(string s) => log.AppendLine(s);

            // 1. 网络操作 (Fetch)
            if (fastMode)
            {
                Step("> [极速模式] 跳过 Fetch");
            }
            else
            {
                Step($"> 尝试极速拉取: origin {targetBranch}...");
                // 优先拉取单分支
                var fetchRes = RunGit(repoPath, $"fetch origin {targetBranch} --no-tags --prune --no-progress", 60_000);
                if (fetchRes.code != 0)
                {
                    Step($"⚠️ 极速拉取失败 ({fetchRes.stderr?.Trim()}), 尝试全量拉取...");
                    RunGit(repoPath, $"fetch --all --tags --prune --no-progress", 180_000);
                }
            }

            // 2. 本地修改处理 (Working Tree)
            bool stashed = false;
            if (useStash)
            {
                if (HasLocalChanges(repoPath))
                {
                    Step($"> stash push...");
                    var (cs, ss, es) = RunGit(repoPath, "stash push -u -m \"GitBranchSwitcher-auto\"", 120_000);
                    if (cs != 0) return (false, log.AppendLine($"❌ Stash失败: {es}").ToString());
                    stashed = true;
                }
            }
            else
            {
                // 强制模式：丢弃工作区修改
                Step($"> 强制清理工作区 (clean)...");
                RunGit(repoPath, "reset --hard", 60_000);
                if (!fastMode) RunGit(repoPath, "clean -fd", 60_000);
            }

            // 3. 检查与切换 (Switch/Checkout)
            bool localExists = RunGit(repoPath, $"show-ref --verify --quiet refs/heads/{targetBranch}", 20_000).code == 0;
            if (localExists)
            {
                Step($"> checkout -f \"{targetBranch}\"");
                var (c1, s1, e1) = RunGit(repoPath, $"checkout -f \"{targetBranch}\"", 90_000);
                if (c1 != 0) return (false, log.AppendLine($"checkout 失败: {e1}").ToString());
            }
            else
            {
                if (fastMode) RunGit(repoPath, $"fetch origin {targetBranch} --no-tags", 60_000);

                bool remoteExists = RunGit(repoPath, $"show-ref --verify --quiet refs/remotes/origin/{targetBranch}", 20_000).code == 0;
                if (!remoteExists) return (false, log.AppendLine($"❌ 分支不存在: {targetBranch}").ToString());

                if (!useStash) RunGit(repoPath, "reset --hard", 60_000);

                Step($"> checkout -B (new track)");
                var (c2, s2, e2) = RunGit(repoPath, $"checkout -B \"{targetBranch}\" \"origin/{targetBranch}\"", 120_000);
                if (c2 != 0) return (false, log.AppendLine($"创建分支失败: {e2}").ToString());
            }

            // 4. 同步远程代码 (Pull / Reset)
            if (!fastMode)
            {
                // 检查远程分支是否存在
                bool remoteTrackingExists = RunGit(repoPath, $"show-ref --verify --quiet refs/remotes/origin/{targetBranch}", 20_000).code == 0;

                if (remoteTrackingExists)
                {
                    if (!useStash)
                    {
                        // [Force Mode]: 强制 Reset 到远程状态
                        Step($"> [强制模式] Reset to origin/{targetBranch}...");
                        var (cr, sr, er) = RunGit(repoPath, $"reset --hard origin/{targetBranch}", 60_000);
                        if (cr != 0) return (false, log.AppendLine($"❌ 强制同步失败: {er}").ToString());
                    }
                    else
                    {
                        // [Safe Mode]: 尝试快进合并
                        Step($"> 尝试同步 (Fast-forward)...");
                        var (cm, sm, em) = RunGit(repoPath, $"merge --ff-only origin/{targetBranch}", 60_000);
                        
                        if (cm != 0)
                        {
                            log.AppendLine($"❌ 同步失败: 本地分支与远程分叉，无法快进 (Diverged)。");
                            log.AppendLine($"原因: {em}");
                            if (stashed) log.AppendLine("⚠️ 提示: 您的工作区修改已 Stash，但代码拉取失败。");
                            return (false, log.ToString());
                        }
                    }
                }
                else
                {
                    Step("> 远程无此分支引用，跳过 Pull。");
                }
            }
            else
            {
                Step($"> [极速模式] 跳过 Pull");
            }

            // 5. Stash Pop
            if (useStash && stashed)
            {
                Step($"> stash pop");
                var (cp, sp, ep) = RunGit(repoPath, "stash pop --index", 180_000);
                if (cp != 0)
                {
                    log.AppendLine($"⚠️ Stash Pop 冲突: 请手动处理。");
                    return (false, log.ToString());
                }
            }

            return (true, log.AppendLine($"OK").ToString());
        }
public static (bool ok, string log, string sizeInfo, long bytesSaved) GarbageCollect(string repoPath, bool aggressive)
        {
            var log = new StringBuilder();
            void Step(string s) => log.AppendLine(s);

            string gitDir = Path.Combine(repoPath, ".git");
            long sizeBefore = GetDirectorySize(gitDir);
            Step($"初始大小: {FormatSize(sizeBefore)}");

            // [新增] 1. 强力清理 Reflog
            // 这是瘦身的关键！不清理 reflog，很多"悬空"对象会被视为"活跃"而保留。
            Step("> Expire reflog (强制清理操作记录)...");
            RunGit(repoPath, "reflog expire --expire=now --all", 30_000);

            Step("> Prune remote origin...");
            RunGit(repoPath, "remote prune origin", 60_000);

            string args;
            if (aggressive) {
                // [修改] 增加 window 和 depth 参数，尝试获得更好的压缩比
                Step("> 🚀 深度清理 (--aggressive --window=50)... (极慢)");
                args = "gc --prune=now --aggressive --window=50";
            } else {
                Step("> 🧹 快速清理...");
                args = "gc --prune=now";
            }

            var (code, stdout, stderr) = RunGit(repoPath, args, -1);

            if (code != 0) 
                return (false, log.AppendLine($"❌ 失败: {stderr}").ToString(), "无变化", 0);

            long sizeAfter = GetDirectorySize(gitDir);
            long saved = sizeBefore - sizeAfter;

            // [修改] 诚实反馈：如果变大了，显示负数
            // 不要再写 if (saved < 0) saved = 0; 
            
            string resultMsg;
            if (saved >= 0)
            {
                resultMsg = $"{FormatSize(saved)} ({FormatSize(sizeBefore)} -> {FormatSize(sizeAfter)})";
                log.AppendLine($"✅ 完成！ 瘦身: {resultMsg}");
            }
            else
            {
                // 变多了通常是因为打包了松散对象但旧文件因占用未删除，或者索引膨胀
                resultMsg = $"⚠️ 膨胀 {FormatSize(-saved)} ({FormatSize(sizeBefore)} -> {FormatSize(sizeAfter)})";
                log.AppendLine($"✅ 完成，但体积增加了。可能原因：\n1. Unity/IDE 占用了文件，导致旧 pack 没删掉。\n2. 松散对象被打包产生了额外的索引文件。");
            }

            return (true, log.ToString(), resultMsg, saved);
        }

        private static long GetDirectorySize(string path) { try { if (!Directory.Exists(path)) return 0; return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length); } catch { return 0; } }

        // [修改] 支持负数显示
        private static string FormatSize(long bytes)
        {
            if (bytes == 0) return "0B";
            
            string prefix = bytes < 0 ? "-" : "";
            long absBytes = Math.Abs(bytes);

            if (absBytes < 1024) return $"{prefix}{absBytes}B";

            long gb = absBytes / (1024 * 1024 * 1024);
            long rem = absBytes % (1024 * 1024 * 1024);
            long mb = rem / (1024 * 1024);
            rem = rem % (1024 * 1024);
            long kb = rem / 1024;

            var sb = new StringBuilder();
            sb.Append(prefix);
            if (gb > 0) sb.Append($"{gb}GB ");
            if (mb > 0) sb.Append($"{mb}MB ");
            if (kb > 0) sb.Append($"{kb}KB");
            
            return sb.ToString().Trim();
        }

        // ==================== 修复逻辑 ====================

        public static (bool ok, string log) RepairRepo(string repoPath)
        {
            var log = new StringBuilder();
            string gitDir = Path.Combine(repoPath, ".git");
            if (!Directory.Exists(gitDir)) return (false, "找不到 .git");
            var locks = Directory.GetFiles(gitDir, "*.lock", SearchOption.AllDirectories);
            foreach (var f in locks) { try { File.Delete(f); log.AppendLine($"Deleted {Path.GetFileName(f)}"); } catch { } }
            var r = RunGit(repoPath, "fsck --full --no-progress", -1);
            return (true, log.ToString() + "\n" + (r.code == 0 ? "Healthy" : r.stdout + r.stderr));
        }

        public static (int code, string stdout, string stderr) RunGit(string workingDir, string args, int timeoutMs = 120000)
        {
            var stdoutSb = new StringBuilder(); var stderrSb = new StringBuilder();
            string safeArgs = $"-c core.quotepath=false -c credential.helper= {args}";
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = safeArgs,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0"; psi.Environment["GCM_INTERACTIVE"] = "Never"; psi.Environment["GIT_ASKPASS"] = "echo";

            try
            {
                using var p = new Process(); p.StartInfo = psi;
                var outWait = new System.Threading.ManualResetEvent(false); var errWait = new System.Threading.ManualResetEvent(false);
                p.OutputDataReceived += (_, e) => { if (e.Data == null) outWait.Set(); else stdoutSb.AppendLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data == null) errWait.Set(); else stderrSb.AppendLine(e.Data); };
                if (!p.Start()) return (-1, "", "Git无法启动");
                p.BeginOutputReadLine(); p.BeginErrorReadLine();

                if (timeoutMs < 0) { p.WaitForExit(); }
                else { if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-2, stdoutSb.ToString(), $"超时(>{timeoutMs / 1000}s)"); } }
                
                outWait.WaitOne(5000); errWait.WaitOne(5000);
                return (p.ExitCode, stdoutSb.ToString(), stderrSb.ToString());
            }
            catch (Exception ex) { return (-3, "", ex.Message); }
        }
        
        public static List<string> ScanForGitRepositories(string rootPath)
        {
            var repos = new List<string>();
            try
            {
                if (!Directory.Exists(rootPath)) return repos;

                // 1. 检查当前目录
                if (IsGitRoot(rootPath))
                {
                    repos.Add(rootPath);
                }

                // 2. 递归子目录
                var subDirs = Directory.GetDirectories(rootPath);
                foreach (var dir in subDirs)
                {
                    var name = Path.GetFileName(dir);
                    if (IsIgnoredFolder(name)) continue; 
                    repos.AddRange(ScanForGitRepositories(dir));
                }
            }
            catch { }
            return repos;
        }

        private static bool IsGitRoot(string path)
        {
            return Directory.Exists(Path.Combine(path, ".git"));
        }

        private static bool IsIgnoredFolder(string name)
        {
            return name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals(".idea", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);
        }
    }
}