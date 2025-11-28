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
        public static string? FindGitRoot(string startPath) { var dir = new DirectoryInfo(startPath); while (dir != null) { var gitDir = Path.Combine(dir.FullName, ".git"); if (Directory.Exists(gitDir) || File.Exists(gitDir)) return dir.FullName; dir = dir.Parent; } return null; }
        public static string GetFriendlyBranch(string repoPath) { { var (c, s, _) = RunGit(repoPath, "branch --show-current", 15000); if (c == 0 && !string.IsNullOrWhiteSpace(s)) return s.Trim(); } { var (c, s, _) = RunGit(repoPath, "rev-parse --abbrev-ref HEAD", 15000); if (c == 0 && !string.IsNullOrWhiteSpace(s) && s.Trim() != "HEAD") return s.Trim(); } { var (c, s, _) = RunGit(repoPath, "rev-parse --short=7 HEAD", 15000); if (c == 0 && !string.IsNullOrWhiteSpace(s)) return $"(detached @{s.Trim()})"; } return "(unknown)"; }
        public static IEnumerable<string> GetAllBranches(string repoPath) { var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); { var (code, stdout, _) = RunGit(repoPath, "for-each-ref --format=%(refname:short) refs/heads", 20000); if (code == 0) foreach (var l in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) set.Add(l.Trim()); } { var (code, stdout, _) = RunGit(repoPath, "for-each-ref --format=%(refname:short) refs/remotes/origin", 20000); if (code == 0) foreach (var l in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) { var name = l.Trim(); if (name.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase)) continue; var idx = name.IndexOf('/'); set.Add(idx >= 0 ? name[(idx + 1)..] : name); }} return set; }
        private static bool HasLocalChanges(string repoPath) { var (code, stdout, _) = RunGit(repoPath, "status --porcelain", 15000); return code == 0 && !string.IsNullOrWhiteSpace(stdout); }
        
        public static (bool ok, string message) SwitchAndPull(string repoPath, string targetBranch, bool useStash, bool fastMode) {
             var log = new StringBuilder();
             void Step(string s) => log.AppendLine(s);
             if (fastMode) { Step("> [Fast] Skip Fetch"); } 
             else { var r=RunGit(repoPath, $"fetch origin {targetBranch} --no-tags --prune --no-progress", 60000); if(r.code!=0) RunGit(repoPath, "fetch --all --tags --prune --no-progress", 180000); }
             bool stashed=false;
             if(useStash){ if(HasLocalChanges(repoPath)){ var r=RunGit(repoPath,"stash push -u -m \"GitBranchSwitcher\"",120000); if(r.code!=0) return(false,r.stderr); stashed=true; } }
             else { RunGit(repoPath,"reset --hard",60000); if(!fastMode) RunGit(repoPath,"clean -fd",60000); }
             if(RunGit(repoPath,$"show-ref --verify --quiet refs/heads/{targetBranch}",20000).code==0) RunGit(repoPath,$"checkout -f \"{targetBranch}\"",90000);
             else { if(fastMode && RunGit(repoPath,$"fetch origin {targetBranch}",60000).code!=0){} if(RunGit(repoPath,$"checkout -B \"{targetBranch}\" \"origin/{targetBranch}\"",120000).code!=0) return(false,"Checkout failed"); }
             if(!fastMode) RunGit(repoPath,"pull --ff-only",120000);
             if(useStash&&stashed) RunGit(repoPath,"stash pop --index",180000);
             return (true, "OK");
        }

        public static (bool ok, string log) RepairRepo(string repoPath) {
            var log = new StringBuilder();
            string gitDir = Path.Combine(repoPath, ".git");
            if (!Directory.Exists(gitDir)) return (false, "找不到 .git");
            var locks = Directory.GetFiles(gitDir, "*.lock", SearchOption.AllDirectories);
            foreach(var f in locks) { try{ File.Delete(f); log.AppendLine($"Deleted {Path.GetFileName(f)}"); }catch{} }
            var r = RunGit(repoPath, "fsck --full --no-progress", -1); // 修复也无限等待
            return (true, log.ToString() + "\n" + (r.code==0?"Healthy":r.stdout+r.stderr));
        }

        // ==================== GC 逻辑 (无超时) ====================

        public static (bool ok, string log, string sizeInfo) GarbageCollect(string repoPath, bool aggressive)
        {
            var log = new StringBuilder();
            void Step(string s) => log.AppendLine(s);

            // 1. 计算清理前大小
            string gitDir = Path.Combine(repoPath, ".git");
            long sizeBefore = GetDirectorySize(gitDir);
            Step($"初始大小: {FormatSize(sizeBefore)}");

            // 2. 执行清理
            Step("> Prune remote origin...");
            RunGit(repoPath, "remote prune origin", 60_000);

            string args;
            
            if (aggressive)
            {
                Step("> 🚀 深度清理 (--aggressive)... 正在执行，请耐心等待直到完成（不限时）");
                args = "gc --prune=now --aggressive";
            }
            else
            {
                Step("> 🧹 快速清理... 正在执行（不限时）");
                args = "gc --prune=now";
            }

            // [关键修改] timeoutMs 设为 -1，表示无限等待，直到 git.exe 自己结束
            var (code, stdout, stderr) = RunGit(repoPath, args, -1);

            if (code != 0) 
                return (false, log.AppendLine($"❌ 失败: {stderr}").ToString(), "无变化");

            // 3. 计算清理后大小
            long sizeAfter = GetDirectorySize(gitDir);
            long saved = sizeBefore - sizeAfter;
            if (saved < 0) saved = 0;

            string resultMsg = $"{FormatSize(saved)} ({FormatSize(sizeBefore)} -> {FormatSize(sizeAfter)})";
            log.AppendLine($"✅ 完成！ 瘦身: {resultMsg}");

            return (true, log.ToString(), FormatSize(saved));
        }

        private static long GetDirectorySize(string path) {
            try { if (!Directory.Exists(path)) return 0; return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length); } catch { return 0; }
        }
        private static string FormatSize(long bytes) {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" }; int counter = 0; decimal number = (decimal)bytes;
            while (Math.Round(number / 1024) >= 1) { number = number / 1024; counter++; }
            return string.Format("{0:n1}{1}", number, suffixes[counter]);
        }

        public static (int code, string stdout, string stderr) RunGit(string workingDir, string args, int timeoutMs = 120000)
        {
            var stdoutSb = new StringBuilder(); var stderrSb = new StringBuilder();
            string safeArgs = $"-c core.quotepath=false -c credential.helper= {args}";
            var psi = new ProcessStartInfo {
                FileName = "git", Arguments = safeArgs, WorkingDirectory = workingDir,
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0"; psi.Environment["GCM_INTERACTIVE"] = "Never"; psi.Environment["GIT_ASKPASS"] = "echo";

            try {
                using var p = new Process(); p.StartInfo = psi;
                var outWait = new System.Threading.ManualResetEvent(false); var errWait = new System.Threading.ManualResetEvent(false);
                p.OutputDataReceived += (_, e) => { if (e.Data == null) outWait.Set(); else stdoutSb.AppendLine(e.Data); };
                p.ErrorDataReceived  += (_, e) => { if (e.Data == null) errWait.Set(); else stderrSb.AppendLine(e.Data); };
                if (!p.Start()) return (-1, "", "Git无法启动");
                p.BeginOutputReadLine(); p.BeginErrorReadLine();

                // [关键逻辑] 处理 -1 无限等待
                if (timeoutMs < 0) {
                    p.WaitForExit(); 
                } else {
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-2, stdoutSb.ToString(), $"超时(>{timeoutMs/1000}s)"); }
                }
                outWait.WaitOne(5000); errWait.WaitOne(5000);
                return (p.ExitCode, stdoutSb.ToString(), stderrSb.ToString());
            } catch (Exception ex) { return (-3, "", ex.Message); }
        }
    }
}