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

            // 4. 同步远程代码 (Pull / Reset) - [本次核心修复位置]
            if (!fastMode)
            {
                // 检查远程分支是否存在 (防止本地有分支但远程没有的情况报错)
                bool remoteTrackingExists = RunGit(repoPath, $"show-ref --verify --quiet refs/remotes/origin/{targetBranch}", 20_000).code == 0;

                if (remoteTrackingExists)
                {
                    if (!useStash)
                    {
                        // [Force Mode]: 强制 Reset 到远程状态，丢弃本地所有未推送的 Commits
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
                            // 失败时明确报错，不强行合并
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
            Step($"初始大小: {FormatSize(sizeBefore)}"); // 使用新格式

            Step("> Prune remote origin...");
            RunGit(repoPath, "remote prune origin", 60_000);

            string args;
            if (aggressive) {
                Step("> 🚀 深度清理 (--aggressive)... (无限等待)");
                args = "gc --prune=now --aggressive";
            } else {
                Step("> 🧹 快速清理...");
                args = "gc --prune=now";
            }

            var (code, stdout, stderr) = RunGit(repoPath, args, -1);

            if (code != 0) 
                return (false, log.AppendLine($"❌ 失败: {stderr}").ToString(), "无变化", 0);

            long sizeAfter = GetDirectorySize(gitDir);
            long saved = sizeBefore - sizeAfter;
            if (saved < 0) saved = 0;

            string resultMsg = $"{FormatSize(saved)} ({FormatSize(sizeBefore)} -> {FormatSize(sizeAfter)})";
            log.AppendLine($"✅ 完成！ 瘦身: {resultMsg}");

            return (true, log.ToString(), FormatSize(saved), saved);
        }

        private static long GetDirectorySize(string path) { try { if (!Directory.Exists(path)) return 0; return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length); } catch { return 0; } }

        // [修改] 递进式格式化：1GB 200MB 50KB
        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0B";
            if (bytes < 1024) return $"{bytes}B";

            long gb = bytes / (1024 * 1024 * 1024);
            long rem = bytes % (1024 * 1024 * 1024);
            long mb = rem / (1024 * 1024);
            rem = rem % (1024 * 1024);
            long kb = rem / 1024;

            var sb = new StringBuilder();
            if (gb > 0) sb.Append($"{gb}GB ");
            if (mb > 0) sb.Append($"{mb}MB ");
            if (kb > 0) sb.Append($"{kb}KB");
            
            // 如果正好是 0KB (比如被整除)，但有 GB/MB，就不用显示 KB 了
            // 如果连 KB 都没有 (比如 500B)，上面第一行已经处理了
            
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
                    // 如果你的项目结构是嵌套的（仓库里套仓库），请注释掉下面这行
                    // return repos; 
                }

                // 2. 递归子目录
                var subDirs = Directory.GetDirectories(rootPath);
                foreach (var dir in subDirs)
                {
                    var name = Path.GetFileName(dir);
                    if (IsIgnoredFolder(name)) continue; // 仅跳过 .git 等系统目录
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
            // [修改] 仅跳过绝对不应该扫描的系统/元数据目录
            // Library 和 Temp 现在会被扫描
            return name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals(".idea", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) || // 前端库通常太深且无意义，建议保留
                   name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);
        }
    }
}