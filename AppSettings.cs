using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GitBranchSwitcher
{
    public class AppSettings
    {
        public bool StashOnSwitch { get; set; } = true;
        public bool FastMode { get; set; } = false;
        public int MaxParallel { get; set; } = 16;
        public List<string> ParentPaths { get; set; } = new List<string>();

        public List<string> SubDirectoriesToScan { get; set; } = new List<string>
        {
            "", 
            "Assets/ToBundle",
            "Assets/Script",
            "Assets/Script/Biubiubiu2", 
            "Assets/Art",
            "Assets/Scenes",
            "Library/ConfigCache",
            "Assets/Audio"
        };

        // 统计字段
        public DateTime LastStatDate { get; set; } = DateTime.MinValue; 
        public int TodaySwitchCount { get; set; } = 0;                  
        public double TodayTotalSeconds { get; set; } = 0;              

        // [修改] 在这里填入你的共享路径，作为默认值
        // 注意前面的 @ 符号，和双反斜杠
        public string LeaderboardPath { get; set; } = @"\\SS-ZHOUSHUAI\GitRankData\rank.json"; 

        public string DirNotStarted { get; set; } = "";
        public string DirSwitching { get; set; } = "";
        public string DirDone { get; set; } = "";
        public string DirFlash { get; set; } = "";

        private static string SettingsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitBranchSwitcher");
        private static string SettingsFile => Path.Combine(SettingsDir, "settings.json");

        public static AppSettings Load()
        {
            AppSettings s = new AppSettings();
            try {
                if (File.Exists(SettingsFile)) {
                    var json = File.ReadAllText(SettingsFile);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                    if (loaded != null) s = loaded;
                }
            } catch { }

            // 补全默认路径
            if (s.SubDirectoriesToScan == null) s.SubDirectoriesToScan = new List<string>();
            var requiredPaths = new[] { "Assets/Script", "Assets/Script/Biubiubiu2" };
            bool changed = false;
            foreach (var req in requiredPaths) {
                if (!s.SubDirectoriesToScan.Any(x => string.Equals(x, req, StringComparison.OrdinalIgnoreCase))) {
                    s.SubDirectoriesToScan.Add(req);
                    changed = true;
                }
            }
            if (s.MaxParallel < 16) { s.MaxParallel = 16; changed = true; }

            // [新增] 如果用户的配置文件里没有路径（或者是空的），强制设为你的默认路径
            if (string.IsNullOrWhiteSpace(s.LeaderboardPath))
            {
                s.LeaderboardPath = @"\\SS-ZHOUSHUAI\GitRankData\rank.json";
                changed = true;
            }

            s.CheckDateReset();

            if (changed) s.Save();
            return s;
        }

        public void Save()
        {
            try {
                Directory.CreateDirectory(SettingsDir);
                File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            } catch { }
        }

        public void CheckDateReset()
        {
            if (LastStatDate.Date != DateTime.Now.Date)
            {
                LastStatDate = DateTime.Now.Date;
                TodaySwitchCount = 0;
                TodayTotalSeconds = 0;
            }
        }
    }
}