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
        // [修改] 增加 Form owner 参数，用于安全的 UI 回调
        public static async Task CheckAndUpdateAsync(string updateRootPath, Form owner)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(updateRootPath) || !Directory.Exists(updateRootPath)) return;

                string versionDir = Path.Combine(updateRootPath, "Version");
                string exeDir = Path.Combine(updateRootPath, "Exe");

                string versionFilePath = Path.Combine(versionDir, "version.txt");
                string remoteExePath = Path.Combine(exeDir, "GitBranchSwitcher.exe");

                if (!File.Exists(versionFilePath) || !File.Exists(remoteExePath)) return;

                await Task.Run(() =>
                {
                    try
                    {
                        string verStr = File.ReadAllText(versionFilePath).Trim();
                        if (!Version.TryParse(verStr, out Version? remoteVer) || remoteVer == null) return;

                        var localVer = Assembly.GetExecutingAssembly().GetName().Version;

                        // 只有 远程 > 本地 时才触发
                        if (remoteVer > localVer)
                        {
                            string notePath = Path.Combine(versionDir, "release_note.txt");
                            string notes = "（本次更新包含若干性能优化与修复）";
                            if (File.Exists(notePath))
                            {
                                try { notes = File.ReadAllText(notePath, Encoding.UTF8); } catch { }
                            }

                            // [修改] 使用传入的 owner 进行 Invoke
                            if (owner != null && !owner.IsDisposed && owner.IsHandleCreated)
                            {
                                owner.BeginInvoke((Action)(() =>
                                {
                                    MessageBox.Show(
                                        $"🎉 发现新版本 v{remoteVer} (当前 v{localVer})\n\n【更新公告】\n{notes}\n\n点击“确定”后将自动重启更新。", 
                                        "自动更新", 
                                        MessageBoxButtons.OK, 
                                        MessageBoxIcon.Information);

                                    PerformUpdate(remoteExePath);
                                }));
                            }
                        }
                    }
                    catch (Exception ex) 
                    { 
                        // 调试用：如果不想吞掉错误，可以用 Debug.WriteLine
                        Debug.WriteLine(ex.ToString());
                    }
                });
            }
            catch { }
        }

        private static void PerformUpdate(string remoteExePath)
        {
            string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(currentExe)) return;

            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string batchPath = Path.Combine(appDir, "update_script.bat");
            
            var batContent = new StringBuilder();
            batContent.AppendLine("@echo off");
            batContent.AppendLine("timeout /t 1 /nobreak > nul"); 
            batContent.AppendLine($"copy /Y \"{remoteExePath}\" \"{currentExe}\"");
            batContent.AppendLine($"start \"\" \"{currentExe}\"");
            batContent.AppendLine($"del \"%~f0\"");

            File.WriteAllText(batchPath, batContent.ToString(), Encoding.Default);

            var psi = new ProcessStartInfo
            {
                FileName = batchPath,
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            
            Process.Start(psi);
            Application.Exit();
        }
    }
}