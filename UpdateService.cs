using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GitBranchSwitcher
{
    public static class UpdateService
    {
        /// <summary>
        /// 检查更新
        /// </summary>
        /// <param name="updateDir">包含最新 GitBranchSwitcher.exe 的共享目录路径</param>
        public static async Task CheckAndUpdateAsync(string updateDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(updateDir) || !Directory.Exists(updateDir)) return;

                // 直接拼接 EXE 路径
                string remoteExePath = Path.Combine(updateDir, "GitBranchSwitcher.exe");

                if (!File.Exists(remoteExePath)) return;

                // 3. 比较版本号 (逻辑保持不变)
                await Task.Run(() =>
                {
                    var currentVer = Assembly.GetExecutingAssembly().GetName().Version;
                    var remoteInfo = FileVersionInfo.GetVersionInfo(remoteExePath);
                    
                    if (string.IsNullOrEmpty(remoteInfo.FileVersion)) return;
                    
                    var remoteVer = new Version(remoteInfo.FileVersion);

                    if (remoteVer > currentVer)
                    {
                        Application.OpenForms[0]?.BeginInvoke((Action)(() =>
                        {
                            if (MessageBox.Show(
                                    $"发现新版本 v{remoteVer} (当前 v{currentVer})！\n\n是否立即更新并重启？", 
                                    "自动更新", 
                                    MessageBoxButtons.YesNo, 
                                    MessageBoxIcon.Information) == DialogResult.Yes)
                            {
                                PerformUpdate(remoteExePath);
                            }
                        }));
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Update check failed: {ex.Message}");
            }
        }
        private static void PerformUpdate(string remoteExePath)
        {
            string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(currentExe)) return;

            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string batchPath = Path.Combine(appDir, "update_script.bat");
            string pid = Process.GetCurrentProcess().Id.ToString();

            // 生成更新脚本
            // 逻辑：
            // 1. 等待当前进程退出 (timeout /t 1)
            // 2. 复制远程文件覆盖本地 (copy /y)
            // 3. 重新启动程序 (start)
            // 4. 删除脚本自己 (del)
            var batContent = new StringBuilder();
            batContent.AppendLine("@echo off");
            batContent.AppendLine("timeout /t 1 /nobreak > nul"); 
            batContent.AppendLine($"copy /Y \"{remoteExePath}\" \"{currentExe}\"");
            batContent.AppendLine($"start \"\" \"{currentExe}\"");
            batContent.AppendLine($"del \"%~f0\"");

            File.WriteAllText(batchPath, batContent.ToString(), Encoding.Default);

            // 启动脚本
            var psi = new ProcessStartInfo
            {
                FileName = batchPath,
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);

            // 退出当前程序
            Application.Exit();
        }
    }
}