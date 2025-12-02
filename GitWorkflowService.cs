using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace GitBranchSwitcher
{
    // 定义进度报告的数据结构
    public class RepoSwitchResult
    {
        public GitRepo Repo { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
        public double DurationSeconds { get; set; }
        public int ProgressIndex { get; set; } // 当前是第几个
        public int TotalCount { get; set; }    // 总数
    }

    public class GitWorkflowService
    {
        private readonly int _maxParallel;

        public GitWorkflowService(int maxParallel = 16)
        {
            _maxParallel = maxParallel;
        }

        public async Task<double> SwitchReposAsync(
            List<GitRepo> repos, 
            string targetBranch, 
            bool useStash, 
            bool fastMode, 
            IProgress<RepoSwitchResult> progress)
        {
            using var sem = new SemaphoreSlim(_maxParallel);
            var tasks = new List<Task>();
            int finishedCount = 0;
            var batchSw = Stopwatch.StartNew();

            foreach (var repo in repos)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync();
                    var sw = Stopwatch.StartNew();
                    bool ok = false;
                    string msg = "";
                    string currentBranch = "";

                    try
                    {
                        var res = GitHelper.SwitchAndPull(repo.Path, targetBranch, useStash, fastMode);
                        ok = res.ok;
                        msg = res.message;
                        // 更新实体状态（注意：这里修改的是引用对象，UI层需注意线程安全或刷新）
                        currentBranch = GitHelper.GetFriendlyBranch(repo.Path);
                        repo.SwitchOk = ok;
                        repo.LastMessage = msg;
                        repo.CurrentBranch = currentBranch;
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        msg = ex.Message;
                    }
                    finally
                    {
                        sw.Stop();
                        sem.Release();
                        
                        // 线程安全地增加计数
                        int currentDone = Interlocked.Increment(ref finishedCount);

                        // 报告进度
                        progress?.Report(new RepoSwitchResult
                        {
                            Repo = repo,
                            Success = ok,
                            Message = msg,
                            DurationSeconds = sw.Elapsed.TotalSeconds,
                            ProgressIndex = currentDone,
                            TotalCount = repos.Count
                        });
                    }
                }));
            }

            await Task.WhenAll(tasks);
            batchSw.Stop();
            return batchSw.Elapsed.TotalSeconds;
        }
    }
}