using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GitBranchSwitcher
{
    public class UserStat
    {
        public string Name { get; set; } = "";
        public int TotalSwitches { get; set; } = 0;     
        public double TotalDuration { get; set; } = 0;  
        // [新增] 累计瘦身 (字节)
        public long TotalSpaceCleaned { get; set; } = 0; 
        public DateTime LastActive { get; set; }
    }

    public static class LeaderboardService
    {
        private static string _sharedFilePath = ""; 
        private static List<UserStat>? _cachedList = null;
        private static DateTime _lastFetchTime = DateTime.MinValue;

        public static void SetPath(string path) => _sharedFilePath = path;

        private static readonly Random _jitter = new Random();

        // [修改] 上传数据：增加 cleanedBytes 参数
        // 返回：最新累计值 (次数, 时长, 空间)
        public static async Task<(int totalCount, double totalTime, long totalSpace)> UploadMyScoreAsync(double durationSeconds, long cleanedBytes) {
            if (string.IsNullOrEmpty(_sharedFilePath))
                return (0, 0, 0);

            return await Task.Run(async () => // 注意这里改为 async lambda 以支持 Task.Delay
            {
                string myName = Environment.UserName;
                int fCount = 0;
                double fTime = 0;
                long fSpace = 0;

                // [优化] 增加重试次数到 10 次，并使用指数退避策略
                int maxRetries = 10;
                for (int i = 0; i < maxRetries; i++) {
                    try {
                        // 尝试获取文件锁并读取
                        var data = ReadAndLock(out var fileStream);
                        using (fileStream) {
                            var me = data.FirstOrDefault(u => u.Name == myName);
                            if (me == null) {
                                me = new UserStat {
                                    Name = myName
                                };
                                data.Add(me);
                            }

                            if (durationSeconds > 0) {
                                me.TotalSwitches++;
                                me.TotalDuration += durationSeconds;
                            }

                            if (cleanedBytes > 0) {
                                me.TotalSpaceCleaned += cleanedBytes;
                            }

                            me.LastActive = DateTime.Now;

                            fCount = me.TotalSwitches;
                            fTime = me.TotalDuration;
                            fSpace = me.TotalSpaceCleaned;

                            // 写入数据
                            fileStream.SetLength(0);
                            using var writer = new StreamWriter(fileStream);
                            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions {
                                WriteIndented = true
                            });
                            writer.Write(json);
                            // [关键] 显式 Flush 确保数据落盘
                            writer.Flush();
                        }

                        _cachedList = null;
                        return (fCount, fTime, fSpace);
                    } catch (IOException) {
                        // [优化] 指数退避 + 随机抖动
                        // 第一次等待约 50-150ms，第二次 100-300ms... 第十次可能等待 1-2秒
                        // 这种随机性极大降低了多个客户端再次碰撞的概率
                        int baseDelay = 50 * (int)Math.Pow(2, i);
                        int actualDelay = _jitter.Next(baseDelay, baseDelay * 3);
                        await Task.Delay(actualDelay);
                    } catch (Exception) {
                        return (0, 0, 0);
                    }
                }

                return (0, 0, 0); // 多次重试依然失败，静默放弃
            });
        }

        public static async Task<List<UserStat>> GetLeaderboardAsync()
        {
            if (string.IsNullOrEmpty(_sharedFilePath)) return new List<UserStat>();
            if (_cachedList != null && (DateTime.Now - _lastFetchTime).TotalSeconds < 30) return _cachedList;

            return await Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(_sharedFilePath)) return new List<UserStat>();
                    using var fs = new FileStream(_sharedFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs);
                    var text = sr.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(text)) return new List<UserStat>();
                    var list = JsonSerializer.Deserialize<List<UserStat>>(text) ?? new List<UserStat>();
                    _cachedList = list; _lastFetchTime = DateTime.Now;
                    return list;
                }
                catch { return new List<UserStat>(); }
            });
        }

        // 获取我的最新数据
        public static async Task<(int, double, long)> GetMyStatsAsync()
        {
            var list = await GetLeaderboardAsync();
            var me = list.FirstOrDefault(u => u.Name == Environment.UserName);
            if (me != null) return (me.TotalSwitches, me.TotalDuration, me.TotalSpaceCleaned);
            return (0, 0, 0);
        }

        private static List<UserStat> ReadAndLock(out FileStream fs)
        {
            if (!File.Exists(_sharedFilePath)) { try { var d=Path.GetDirectoryName(_sharedFilePath); if(!string.IsNullOrEmpty(d))Directory.CreateDirectory(d); File.WriteAllText(_sharedFilePath, "[]"); } catch {} }
            fs = new FileStream(_sharedFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, true, 1024, leaveOpen: true);
            var text = reader.ReadToEnd();
            return string.IsNullOrWhiteSpace(text) ? new List<UserStat>() : (JsonSerializer.Deserialize<List<UserStat>>(text) ?? new List<UserStat>());
        }
    }
}