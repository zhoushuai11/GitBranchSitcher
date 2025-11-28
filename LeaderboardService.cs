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
        public int TotalSwitches { get; set; } = 0;     // 次数
        public double TotalDuration { get; set; } = 0;  // 总时长(秒)
        public DateTime LastActive { get; set; }
    }

    public static class LeaderboardService
    {
        // 共享文件路径
        private static string _sharedFilePath = ""; 

        public static void SetPath(string path)
        {
            _sharedFilePath = path;
        }

        // [修改] 上传数据时，传入本次耗时
        public static async Task UploadMyScoreAsync(double durationSeconds)
        {
            if (string.IsNullOrEmpty(_sharedFilePath)) return;

            await Task.Run(() =>
            {
                string myName = Environment.UserName; 

                // 重试机制，防止文件被锁
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        var data = ReadAndLock(out var fileStream);
                        using (fileStream) 
                        {
                            var me = data.FirstOrDefault(u => u.Name == myName);
                            if (me == null)
                            {
                                me = new UserStat { Name = myName, TotalSwitches = 0, TotalDuration = 0 };
                                data.Add(me);
                            }
                            // 累加数据
                            me.TotalSwitches++;
                            me.TotalDuration += durationSeconds;
                            me.LastActive = DateTime.Now;

                            fileStream.SetLength(0);
                            using var writer = new StreamWriter(fileStream);
                            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                            writer.Write(json);
                        }
                        return; 
                    }
                    catch (IOException) { Thread.Sleep(200); }
                    catch { return; }
                }
            });
        }

        public static async Task<List<UserStat>> GetLeaderboardAsync()
        {
            if (string.IsNullOrEmpty(_sharedFilePath) || !File.Exists(_sharedFilePath))
                return new List<UserStat>();

            return await Task.Run(() =>
            {
                try
                {
                    using var fs = new FileStream(_sharedFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs);
                    var text = sr.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(text)) return new List<UserStat>();
                    return JsonSerializer.Deserialize<List<UserStat>>(text) ?? new List<UserStat>();
                }
                catch { return new List<UserStat>(); }
            });
        }

        private static List<UserStat> ReadAndLock(out FileStream fs)
        {
            if (!File.Exists(_sharedFilePath))
            {
                var dir = Path.GetDirectoryName(_sharedFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_sharedFilePath, "[]");
            }
            fs = new FileStream(_sharedFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, true, 1024, leaveOpen: true);
            var text = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text)) return new List<UserStat>();
            return JsonSerializer.Deserialize<List<UserStat>>(text) ?? new List<UserStat>();
        }
    }
}