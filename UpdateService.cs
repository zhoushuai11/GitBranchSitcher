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
        public static async Task CheckAndUpdateAsync(string updateRootPath, Form owner)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(updateRootPath) || !Directory.Exists(updateRootPath)) return;

                string versionDir = Path.Combine(updateRootPath, "Version");
                string exeDir = Path.Combine(updateRootPath, "Exe");
                string versionFilePath = Path.Combine(versionDir, "version.txt");

                // 获取远程文件名（使用程序集名称，如 GitBranchSwitcher.exe）
                var asmName = Assembly.GetEntryAssembly().GetName().Name;
                string remoteExePath = Path.Combine(exeDir, asmName + ".exe");

                if (!File.Exists(versionFilePath) || !File.Exists(remoteExePath)) return;

                await Task.Run(() =>
                {
                    try
                    {
                        string verStr = File.ReadAllText(versionFilePath).Trim();
                        if (!Version.TryParse(verStr, out Version? remoteVer) || remoteVer == null) return;

                        var localVer = Assembly.GetExecutingAssembly().GetName().Version;

                        if (remoteVer > localVer)
                        {
                            string notePath = Path.Combine(versionDir, "release_note.txt");
                            string notes = "（本次更新包含若干性能优化与修复）";
                            if (File.Exists(notePath))
                            {
                                try { notes = File.ReadAllText(notePath, Encoding.UTF8); } catch { }
                            }

                            if (owner != null && !owner.IsDisposed && owner.IsHandleCreated)
                            {
                                owner.BeginInvoke((Action)(() =>
                                {
                                    // 弹窗提示
                                    MessageBox.Show(
                                        $"🎉 发现新版本 v{remoteVer} (当前 v{localVer})\n\n【更新公告】\n{notes}\n\n点击“确定”后将自动重启更新。", 
                                        "自动更新", 
                                        MessageBoxButtons.OK, 
                                        MessageBoxIcon.Information);

                                    // 执行更新
                                    PerformUpdate(remoteExePath);
                                }));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
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
            // 使用 .cmd 后缀
            string batchPath = Path.Combine(appDir, $"update_{Guid.NewGuid().ToString("N")}.cmd");
            
            var batContent = new StringBuilder();
            
            // [关键修复 1] 切换 CMD 代码页到 UTF-8，防止中文路径乱码
            batContent.AppendLine("@chcp 65001 >NUL");
            batContent.AppendLine("@echo off");
            
            // 等待主进程完全退出
            batContent.AppendLine("timeout /t 1 /nobreak >NUL"); 
            
            // [关键修复 2] 复制文件 (加引号防止路径空格问题)
            batContent.AppendLine($"copy /Y \"{remoteExePath}\" \"{currentExe}\"");
            
            // 启动更新后的程序
            batContent.AppendLine($"start \"\" \"{currentExe}\"");
            
            // 删除脚本自身
            batContent.AppendLine($"del \"%~f0\"");

            // [关键修复 3] 必须使用 UTF8 (无 BOM) 格式保存，配合 chcp 65001
            File.WriteAllText(batchPath, batContent.ToString(), new UTF8Encoding(false));

            var psi = new ProcessStartInfo
            {
                FileName = batchPath,
                UseShellExecute = true, // 必须为 true 才能隐藏窗口
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            
            Process.Start(psi);
            Application.Exit();
        }
    }
}