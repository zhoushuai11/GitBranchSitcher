using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GitBranchSwitcher
{
    public partial class MainForm : Form {
        private TableLayoutPanel tlTop;
        private CheckedListBox lbParents;
        private TextBox txtSearch;
        private Button btnAddParent;
        private Button btnRemoveParent;
        private Button btnSelectAllParents;
        private Button btnClearParents;
        private Label lblHintParents;
        private SplitContainer splitMain;
        private SplitContainer splitUpper;
        private ListView lvRepos;
        private FlowLayoutPanel repoToolbar;
        private Panel panelLeft;
        private Panel pnlRight;
        private Label lblTargetBranch;
        private ComboBox cmbTargetBranch;
        private Button btnSwitchAll;
        private Button btnUseCurrentBranch;
        private CheckBox chkStashOnSwitch;
        private CheckBox chkFastMode; 
        private FlowLayoutPanel statePanel;
        private PictureBox pbState; 
        private Label lblStateText;
        private PictureBox pbFlash; 
        private System.Windows.Forms.Timer flashTimer;
        private TextBox txtLog;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel statusLabel;
        private ToolStripProgressBar statusProgress;
        private ToolStripStatusLabel statusStats; 

        private readonly BindingList<GitRepo> _repos = new BindingList<GitRepo>();
        private List<string> _allBranches = new List<string>();
        private AppSettings _settings;
        private System.Threading.CancellationTokenSource? _loadCts;
        private int _loadSeq = 0;
        private HashSet<string> _checkedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const int TARGET_BOX = 500; 
        private const int FLASH_BOX = 300;
        private enum SwitchState { NotStarted, Switching, Done }

        public MainForm() {
            _settings = AppSettings.Load();
            InitializeComponent();
            TrySetRuntimeIcon();
            InitUi();
            LoadStateImagesRandom(); 
            SetSwitchState(SwitchState.NotStarted);
            
            // 初始化排行榜路径
            LeaderboardService.SetPath(_settings.LeaderboardPath);
            
            SeedParentsToUi();
            UpdateStatsUi(); 
        }

        private void InitializeComponent() {
            Text = "Unity 项目切线工具 (Rank Edition)";
            Width = 1400;
            Height = 900;
            StartPosition = FormStartPosition.CenterScreen;
        }

        private void InitUi() {
            tlTop = new TableLayoutPanel { Dock = DockStyle.Top, Height = 120, ColumnCount = 6, Padding = new Padding(8) };
            tlTop.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tlTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tlTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tlTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tlTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tlTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tlTop.RowCount = 2;
            tlTop.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tlTop.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            lbParents = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
            btnAddParent = new Button { Text = "添加父目录…" };
            btnRemoveParent = new Button { Text = "移除选中" };
            var lblSearch = new Label { Text = "过滤：", AutoSize = true, Anchor = AnchorStyles.Left };
            txtSearch = new TextBox { Width = 220, Anchor = AnchorStyles.Left };
            var parentOps = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true };
            btnSelectAllParents = new Button { Text = "全选父目录", AutoSize = true };
            btnClearParents = new Button { Text = "全不选父目录", AutoSize = true };
            parentOps.Controls.Add(btnSelectAllParents); parentOps.Controls.Add(btnClearParents);
            lblHintParents = new Label { Text = "提示：勾选要使用的父目录；支持过滤；Delete 可删除；右键可添加/移除。", AutoSize = true, ForeColor = SystemColors.GrayText };
            tlTop.Controls.Add(lbParents, 0, 0); tlTop.Controls.Add(btnAddParent, 1, 0); tlTop.Controls.Add(btnRemoveParent, 2, 0);
            tlTop.Controls.Add(lblSearch, 3, 0); tlTop.Controls.Add(txtSearch, 4, 0); tlTop.Controls.Add(parentOps, 5, 0);
            tlTop.Controls.Add(lblHintParents, 0, 1); tlTop.SetColumnSpan(lblHintParents, 6);
            var cm = new ContextMenuStrip();
            cm.Items.Add("添加父目录…", null, (_, __) => btnAddParent.PerformClick());
            cm.Items.Add("移除选中", null, (_, __) => btnRemoveParent.PerformClick());
            lbParents.ContextMenuStrip = cm;
            btnAddParent.Click += (_, __) => { using var fbd = new FolderBrowserDialog(); if (fbd.ShowDialog(this) == DialogResult.OK) { var path = fbd.SelectedPath.Trim(); if (!Directory.Exists(path)) return; if (!_settings.ParentPaths.Contains(path)) { _settings.ParentPaths.Add(path); _settings.Save(); } RefilterParentsList(); } };
            btnRemoveParent.Click += async (_, __) => { var rm = new List<string>(); foreach(var i in lbParents.SelectedItems) rm.Add(i.ToString()); foreach(var i in lbParents.CheckedItems) rm.Add(i.ToString()); foreach(var p in rm) { _settings.ParentPaths.Remove(p); _checkedParents.Remove(p); } _settings.Save(); RefilterParentsList(); await LoadReposForCheckedParentsAsync(); };
            txtSearch.TextChanged += (_, __) => RefilterParentsList();
            lbParents.ItemCheck += async (_, e) => { var p = lbParents.Items[e.Index].ToString(); BeginInvoke(new Action(async()=> { if(lbParents.GetItemChecked(e.Index)) _checkedParents.Add(p); else _checkedParents.Remove(p); await LoadReposForCheckedParentsAsync(); })); };
            btnSelectAllParents.Click += async (_, __) => { _checkedParents = new HashSet<string>(_settings.ParentPaths); for(int i=0;i<lbParents.Items.Count;i++) lbParents.SetItemChecked(i,true); await LoadReposForCheckedParentsAsync(); };
            btnClearParents.Click += async (_, __) => { _checkedParents.Clear(); for(int i=0;i<lbParents.Items.Count;i++) lbParents.SetItemChecked(i,false); await LoadReposForCheckedParentsAsync(); };
            lbParents.KeyDown += async (_, e) => { if(e.KeyCode==Keys.Delete) btnRemoveParent.PerformClick(); };

            splitMain = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            splitUpper = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
            Shown += (_, __) => { splitMain.SplitterDistance = (int)(ClientSize.Height * 0.58); splitUpper.SplitterDistance = (int)(ClientSize.Width * 0.52); };

            lvRepos = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, CheckBoxes = true };
            lvRepos.Columns.Add("结果 (耗时)", 140);
            lvRepos.Columns.Add("当前分支", 220);
            lvRepos.Columns.Add("仓库名", 240);
            lvRepos.Columns.Add("路径", 400);

            var listMenu = new ContextMenuStrip();
            var itemOpenDir = listMenu.Items.Add("📂 打开文件夹");
            listMenu.Items.Add(new ToolStripSeparator());
            var itemRepair = listMenu.Items.Add("🛠️ 解锁与修复 (删除 .lock)");
            listMenu.Items.Add(new ToolStripSeparator());
            var itemGcFast = listMenu.Items.Add("🧹 方案 A：快速瘦身 (推荐)");
            var itemGcDeep = listMenu.Items.Add("🌪️ 方案 B：深度瘦身 (极慢)");
            
            itemOpenDir.Click += (_, __) => { if (lvRepos.SelectedItems.Count == 0) return; var r = (GitRepo)lvRepos.SelectedItems[0].Tag; Process.Start("explorer.exe", r.Path); };
            itemRepair.Click += async (_, __) => {
                if (lvRepos.SelectedItems.Count == 0) { MessageBox.Show("请先选中一个仓库"); return; }
                var item = lvRepos.SelectedItems[0]; var r = (GitRepo)item.Tag;
                if (MessageBox.Show($"确定要修复 [{r.Name}] 吗？\n\n将强制删除 index.lock 等锁文件并检查健康状况。", "修复确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                item.Text = "🛠️ 修复中..."; Log($">>> 开始修复: {r.Name} ...");
                await Task.Run(() => {
                    var sw = Stopwatch.StartNew(); var res = GitHelper.RepairRepo(r.Path); sw.Stop();
                    BeginInvoke((Action)(() => { item.Text = res.ok ? "✅ 修复完成" : "❌ 失败"; Log($"[{r.Name}] {res.log}"); MessageBox.Show($"[{r.Name}] 修复报告：\n\n{res.log}", "完成"); }));
                });
            };
            async void PerformGc(bool aggressive) {
                if (lvRepos.SelectedItems.Count == 0) { MessageBox.Show("请先选中一个仓库"); return; }
                var item = lvRepos.SelectedItems[0]; var r = (GitRepo)item.Tag;
                string modeName = aggressive ? "深度瘦身 (Aggressive)" : "快速瘦身";
                string warn = aggressive ? "\n\n⚠️ 注意：深度模式会重组所有对象，大仓库可能耗时 10-20 分钟！" : "";
                if (MessageBox.Show($"确定对 [{r.Name}] 进行 {modeName} 吗？{warn}", "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                item.Text = "🧹 清理中..."; Log($">>> 开始 {modeName}: {r.Name} ...");
                await Task.Run(() => {
                    var sw = Stopwatch.StartNew(); var res = GitHelper.GarbageCollect(r.Path, aggressive); sw.Stop();
                    BeginInvoke((Action)(() => {
                        if (res.ok) item.Text = $"✅ 减小 {res.sizeInfo}"; else item.Text = "❌ 失败/超时";
                        Log($"[{r.Name}] {res.log}");
                        if (res.ok) MessageBox.Show($"[{r.Name}] 清理完毕！\n共节省空间: {res.sizeInfo}\n耗时: {sw.Elapsed.TotalSeconds:F0}秒", "完成");
                        else MessageBox.Show($"[{r.Name}] 清理失败或超时。\n请查看底部日志。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                });
            }
            itemGcFast.Click += (_, __) => PerformGc(false);
            itemGcDeep.Click += (_, __) => PerformGc(true);
            lvRepos.ContextMenuStrip = listMenu;

            repoToolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(6) };
            var btnR1 = new Button { Text = "取消" }; var btnR2 = new Button { Text = "全选" }; var btnR3 = new Button { Text = "全不选" };
            
            // [新增] 🏆 排行榜按钮
            var btnRank = new Button { Text = "🏆 排行榜", AutoSize = true, ForeColor = Color.DarkGoldenrod, Font = new Font(DefaultFont, FontStyle.Bold) };
            btnRank.Click += (_, __) => ShowLeaderboard();

            repoToolbar.Controls.Add(btnR1); repoToolbar.Controls.Add(btnR2); repoToolbar.Controls.Add(btnR3);
            repoToolbar.Controls.Add(btnRank); // 加入工具栏

            btnR1.Click += (_,__) => { foreach(ListViewItem i in lvRepos.Items) i.Checked=false; };
            btnR2.Click += (_,__) => { foreach(ListViewItem i in lvRepos.Items) i.Checked=true; };
            btnR3.Click += (_,__) => { foreach(ListViewItem i in lvRepos.Items) i.Checked=false; };
            panelLeft = new Panel { Dock = DockStyle.Fill };
            panelLeft.Controls.Add(lvRepos); panelLeft.Controls.Add(repoToolbar);

            pnlRight = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
            var rightLayout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true };
            rightLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); rightLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); rightLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var hint = new Label { Text = "提示：全量 Fetch 模式，确保能获取所有远程分支。", AutoSize = true, ForeColor = SystemColors.HotTrack };
            rightLayout.Controls.Add(hint, 0, 0); rightLayout.SetColumnSpan(hint, 3);

            lblTargetBranch = new Label { Text = "目标分支：", AutoSize = true };
            cmbTargetBranch = new ComboBox { Width = 400, DropDownStyle = ComboBoxStyle.DropDown, Anchor = AnchorStyles.Left|AnchorStyles.Right };
            btnUseCurrentBranch = new Button { Text = "使用选中项", AutoSize = true };
            btnUseCurrentBranch.Click += (_, __) => { 
                var item = lvRepos.Items.Cast<ListViewItem>().FirstOrDefault(i=>i.Checked);
                if(item == null) { MessageBox.Show("请先勾选一个仓库"); return; }
                var repo = (GitRepo)item.Tag;
                var branch = repo.CurrentBranch;
                if (!string.IsNullOrEmpty(branch) && branch != "—" && branch != "...") { cmbTargetBranch.SelectedIndex = -1; cmbTargetBranch.Text = branch; } 
                else { MessageBox.Show("选中仓库没有有效的当前分支信息"); }
            };
            cmbTargetBranch.TextUpdate += (_, __) => UpdateBranchDropdown();

            chkStashOnSwitch = new CheckBox { Text = "尝试 Stash 本地修改 [不勾选 = 强制覆盖]", AutoSize = true, Checked = _settings.StashOnSwitch, ForeColor = Color.DarkRed };
            chkStashOnSwitch.CheckedChanged += (_, __) => { _settings.StashOnSwitch = chkStashOnSwitch.Checked; _settings.Save(); };

            chkFastMode = new CheckBox { Text = "⚡ 极速本地切换 (跳过 Fetch/Pull)", AutoSize = true, Checked = _settings.FastMode, ForeColor = Color.DarkGreen, Font = new Font(DefaultFont, FontStyle.Bold) };
            chkFastMode.CheckedChanged += (_, __) => { _settings.FastMode = chkFastMode.Checked; _settings.Save(); };

            btnSwitchAll = new Button { Text = "🚀 一键切线 (Switch)", Height = 40, Width = 200, Font = new Font(DefaultFont, FontStyle.Bold), Anchor = AnchorStyles.Left | AnchorStyles.Right };
            btnSwitchAll.Click += async (_, __) => await SwitchAllAsync();

            statePanel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            pbState = new PictureBox { Width = TARGET_BOX, Height = TARGET_BOX, SizeMode = PictureBoxSizeMode.CenterImage };
            lblStateText = new Label { Text = "Ready", Font = new Font(DefaultFont, FontStyle.Bold), AutoSize = true };
            pbFlash = new PictureBox { Width = FLASH_BOX, Height = FLASH_BOX, Visible = false, SizeMode = PictureBoxSizeMode.CenterImage };
            flashTimer = new System.Windows.Forms.Timer { Interval = 800 }; flashTimer.Tick += (_,__) => { pbFlash.Visible=false; flashTimer.Stop(); };
            statePanel.Controls.Add(pbState); statePanel.Controls.Add(lblStateText); statePanel.Controls.Add(pbFlash);

            rightLayout.Controls.Add(lblTargetBranch, 0, 1); rightLayout.Controls.Add(cmbTargetBranch, 1, 1); rightLayout.Controls.Add(btnUseCurrentBranch, 2, 1);
            rightLayout.Controls.Add(btnSwitchAll, 0, 2); rightLayout.SetColumnSpan(btnSwitchAll, 3);
            rightLayout.Controls.Add(chkStashOnSwitch, 0, 3); rightLayout.SetColumnSpan(chkStashOnSwitch, 3);
            rightLayout.Controls.Add(chkFastMode, 0, 4); rightLayout.SetColumnSpan(chkFastMode, 3);
            rightLayout.Controls.Add(statePanel, 0, 5); rightLayout.SetColumnSpan(statePanel, 3);
            pnlRight.Controls.Add(rightLayout);
            splitUpper.Panel1.Controls.Add(panelLeft); splitUpper.Panel2.Controls.Add(pnlRight); splitMain.Panel1.Controls.Add(splitUpper);

            txtLog = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, ReadOnly = true, Font = new Font("Consolas", 9) };
            splitMain.Panel2.Controls.Add(txtLog);

            statusStrip = new StatusStrip();
            statusLabel = new ToolStripStatusLabel("就绪");
            statusStats = new ToolStripStatusLabel { Alignment = ToolStripItemAlignment.Right, ForeColor = Color.Blue };
            statusProgress = new ToolStripProgressBar { Visible = false, Style = ProgressBarStyle.Marquee };
            statusStrip.Items.Add(statusLabel); statusStrip.Items.Add(new ToolStripStatusLabel { Spring = true }); statusStrip.Items.Add(statusStats); statusStrip.Items.Add(statusProgress);
            Controls.Add(splitMain); Controls.Add(tlTop); Controls.Add(statusStrip);
        }

        // [新增] 显示排行榜窗口的逻辑
        private async void ShowLeaderboard()
        {
            if (string.IsNullOrEmpty(_settings.LeaderboardPath))
            {
                string input = Microsoft.VisualBasic.Interaction.InputBox("请输入共享文件路径 (如 \\\\SERVER\\Share\\rank.json):", "设置", _settings.LeaderboardPath);
                if (string.IsNullOrWhiteSpace(input)) return;
                _settings.LeaderboardPath = input; _settings.Save(); LeaderboardService.SetPath(input);
            }

            // 动态创建一个简单的窗口
            var form = new Form { Text = "👑 卷王 & 摸鱼王 排行榜", Width = 600, Height = 500, StartPosition = FormStartPosition.CenterParent };
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 290 };
            var listCount = new ListView { Dock = DockStyle.Fill, View = View.Details, GridLines = true, FullRowSelect = true };
            listCount.Columns.Add("排名", 50); listCount.Columns.Add("🌭 香肠切线王 (次数)", 150); listCount.Columns.Add("次数", 60);
            var listDuration = new ListView { Dock = DockStyle.Fill, View = View.Details, GridLines = true, FullRowSelect = true };
            listDuration.Columns.Add("排名", 50); listDuration.Columns.Add("🐟 香肠摸鱼王 (时长)", 150); listDuration.Columns.Add("总时长", 80);

            var lblMy = new Label { Dock = DockStyle.Bottom, Height = 40, TextAlign = ContentAlignment.MiddleCenter, Font = new Font(DefaultFont, FontStyle.Bold), Text = "正在加载数据..." };

            split.Panel1.Controls.Add(listCount);
            split.Panel2.Controls.Add(listDuration);
            form.Controls.Add(split);
            form.Controls.Add(lblMy);

            form.Shown += async (_, __) => {
                var data = await LeaderboardService.GetLeaderboardAsync();
                
                // 1. 次数榜
                var sortedCount = data.OrderByDescending(x => x.TotalSwitches).ToList();
                for (int i = 0; i < sortedCount.Count; i++) {
                    var u = sortedCount[i];
                    var icon = i == 0 ? "🥇" : (i == 1 ? "🥈" : (i == 2 ? "🥉" : ""));
                    listCount.Items.Add(new ListViewItem(new[] { (i + 1).ToString(), $"{icon} {u.Name}", u.TotalSwitches.ToString() }));
                }

                // 2. 时长榜
                var sortedTime = data.OrderByDescending(x => x.TotalDuration).ToList();
                for (int i = 0; i < sortedTime.Count; i++) {
                    var u = sortedTime[i];
                    var icon = i == 0 ? "👑" : ""; // 摸鱼王皇冠
                    string timeStr = u.TotalDuration > 3600 ? $"{(u.TotalDuration/3600):F1}h" : $"{(u.TotalDuration/60):F0}m";
                    listDuration.Items.Add(new ListViewItem(new[] { (i + 1).ToString(), $"{icon} {u.Name}", timeStr }));
                }

                // 3. 我的数据
                var me = data.FirstOrDefault(x => x.Name == Environment.UserName);
                if (me != null) {
                    int myRankCount = sortedCount.IndexOf(me) + 1;
                    int myRankTime = sortedTime.IndexOf(me) + 1;
                    string myTime = me.TotalDuration > 60 ? $"{(me.TotalDuration/60):F0}分钟" : $"{me.TotalDuration:F0}秒";
                    lblMy.Text = $"我 ({me.Name})：切线 {me.TotalSwitches} 次 (第{myRankCount}名) | 摸鱼 {myTime} (第{myRankTime}名)";
                } else {
                    lblMy.Text = "暂无我的数据，快去切一次线吧！";
                }
            };

            form.ShowDialog(this);
        }

        private void TrySetRuntimeIcon() { try { var icon = ImageHelper.LoadIconFromResource("appicon"); if (icon != null) this.Icon = icon; } catch { } }
        private void ApplyImageTo(PictureBox pb, string key, int s) { if (pb.Image != null) { var o = pb.Image; pb.Image = null; o.Dispose(); } var img = ImageHelper.LoadRandomImageFromResource(key); if (img != null) { pb.SizeMode = (img.Width > s || img.Height > s) ? PictureBoxSizeMode.Zoom : PictureBoxSizeMode.CenterImage; pb.Image = img; } }
        private void LoadStateImagesRandom() { ApplyImageTo(pbState, "state_notstarted", TARGET_BOX); ApplyImageTo(pbFlash, "flash_success", FLASH_BOX); }
        private void SetSwitchState(SwitchState st) { if (st == SwitchState.NotStarted) { ApplyImageTo(pbState, "state_notstarted", TARGET_BOX); lblStateText.Text = "未开始"; } if (st == SwitchState.Switching) { ApplyImageTo(pbState, "state_switching", TARGET_BOX); lblStateText.Text = "切线中..."; } if (st == SwitchState.Done) { ApplyImageTo(pbState, "state_done", TARGET_BOX); lblStateText.Text = "搞定!"; } }
        private void SeedParentsToUi() { if(lbParents==null) return; lbParents.BeginUpdate(); lbParents.Items.Clear(); foreach(var p in _settings.ParentPaths) { int i=lbParents.Items.Add(p); if(_checkedParents.Contains(p)) lbParents.SetItemChecked(i,true); } lbParents.EndUpdate(); }
        private void RefilterParentsList() { lbParents.BeginUpdate(); lbParents.Items.Clear(); var kw=txtSearch.Text.Trim(); foreach(var p in _settings.ParentPaths) { if(string.IsNullOrEmpty(kw)||p.IndexOf(kw,StringComparison.OrdinalIgnoreCase)>=0) { int i=lbParents.Items.Add(p); if(_checkedParents.Contains(p)) lbParents.SetItemChecked(i,true); } } lbParents.EndUpdate(); }
        private void UpdateStatsUi() { if (statusStats != null) { TimeSpan ts = TimeSpan.FromSeconds(_settings.TodayTotalSeconds); string timeStr = ts.TotalMinutes >= 1 ? $"{(int)ts.TotalMinutes}分{ts.Seconds}秒" : $"{ts.Seconds}秒"; statusStats.Text = $"📅 今日统计：切线 {_settings.TodaySwitchCount} 次 | 总耗时 {timeStr}"; } }

        private async Task LoadReposForCheckedParentsAsync() {
            _loadCts?.Cancel(); _loadCts = new System.Threading.CancellationTokenSource(); var token = _loadCts.Token; var seq = ++_loadSeq;
            lvRepos.BeginUpdate(); lvRepos.Items.Clear(); lvRepos.EndUpdate(); _repos.Clear(); _allBranches.Clear(); cmbTargetBranch.Items.Clear();
            var parents = _checkedParents.Where(Directory.Exists).ToList();
            if(!parents.Any()) { statusLabel.Text="就绪"; SetSwitchState(SwitchState.NotStarted); return; }
            var targets = new List<(string name, string path, string parent)>();
            var subConfig = _settings.SubDirectoriesToScan ?? new List<string>{""};
            foreach(var p in parents) foreach(var sub in subConfig) {
                string full = string.IsNullOrEmpty(sub)?p:Path.Combine(p,sub);
                string name = string.IsNullOrEmpty(sub)?"root":Path.GetFileName(sub);
                targets.Add((name, full, p));
            }
            lvRepos.BeginUpdate();
            var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var t in targets) {
                if(token.IsCancellationRequested) break;
                if(!Directory.Exists(t.path)) { Log($"⚠️ [扫描跳过] 路径不存在: {t.path}"); continue; }
                string? root = GitHelper.FindGitRoot(t.path);
                if(root==null) { Log($"⚠️ [扫描跳过] 非 Git 仓库: {t.path}"); continue; }
                if (seenRoots.Contains(root)) continue; seenRoots.Add(root);
                var r = new GitRepo(t.name, root);
                lvRepos.Items.Add(new ListViewItem(new[]{"⏳", "—", $"[{Path.GetFileName(t.parent)}] {t.name}", root}) { Tag=r, Checked=true });
            }
            lvRepos.EndUpdate();
            var tasks = new List<Task>();
            foreach(ListViewItem item in lvRepos.Items) tasks.Add(Task.Run(()=>{
                if(token.IsCancellationRequested) return;
                ((GitRepo)item.Tag).CurrentBranch = GitHelper.GetFriendlyBranch(((GitRepo)item.Tag).Path);
            }));
            await Task.WhenAll(tasks);
            if(token.IsCancellationRequested || seq!=_loadSeq) return;
            lvRepos.BeginUpdate();
            foreach(ListViewItem item in lvRepos.Items) item.SubItems[1].Text = ((GitRepo)item.Tag).CurrentBranch;
            lvRepos.EndUpdate();
            statusLabel.Text = "加载完成";
            await RefreshBranchesAsync();
        }

        private async Task RefreshBranchesAsync() {
            if (lvRepos.Items.Count == 0) return;
            statusLabel.Text = "读取分支...";
            var all = new HashSet<string>();
            var tasks = new List<Task<IEnumerable<string>>>();
            foreach(ListViewItem item in lvRepos.Items) tasks.Add(Task.Run(()=>GitHelper.GetAllBranches(((GitRepo)item.Tag).Path)));
            foreach(var r in await Task.WhenAll(tasks)) foreach(var b in r) all.Add(b);
            _allBranches = all.OrderBy(x=>x).ToList();
            UpdateBranchDropdown();
            statusLabel.Text = "就绪";
        }

        private void UpdateBranchDropdown() {
            cmbTargetBranch.BeginUpdate(); cmbTargetBranch.Items.Clear();
            var txt = cmbTargetBranch.Text;
            var list = string.IsNullOrEmpty(txt) ? _allBranches : _allBranches.Where(b=>b.IndexOf(txt,StringComparison.OrdinalIgnoreCase)>=0).ToList();
            foreach(var b in list.Take(500)) cmbTargetBranch.Items.Add(b);
            cmbTargetBranch.EndUpdate();
            cmbTargetBranch.SelectionStart = txt.Length;
            if (list.Count > 0 && cmbTargetBranch.Focused) { cmbTargetBranch.DroppedDown = true; Cursor.Current = Cursors.Default; }
        }

        private async Task SwitchAllAsync() {
            var target = cmbTargetBranch.Text.Trim();
            if(string.IsNullOrEmpty(target)) { MessageBox.Show("请输入分支名"); return; }
            var items = lvRepos.Items.Cast<ListViewItem>().Where(i=>i.Checked).ToList();
            if(!items.Any()) return;
            btnSwitchAll.Enabled=false; statusProgress.Visible=true; SetSwitchState(SwitchState.Switching);
            foreach(var i in items) { i.Text="⏳"; i.SubItems[1].Text="..."; }
            int done=0; 
            var sem = new System.Threading.SemaphoreSlim(_settings.MaxParallel);
            var tasks = new List<Task>();
            Log($">>> 开始一键切线：{target} [极速模式:{_settings.FastMode}]");
            
            var batchSw = Stopwatch.StartNew(); // [新增] 总计时

            foreach(var item in items) {
                tasks.Add(Task.Run(async () => {
                    await sem.WaitAsync();
                    var r = (GitRepo)item.Tag;
                    var sw = Stopwatch.StartNew();
                    try {
                        var res = GitHelper.SwitchAndPull(r.Path, target, _settings.StashOnSwitch, _settings.FastMode);
                        r.SwitchOk = res.ok;
                        r.LastMessage = res.message;
                        r.CurrentBranch = GitHelper.GetFriendlyBranch(r.Path);
                    } finally { sw.Stop(); sem.Release(); }
                    BeginInvoke((Action)(() => {
                        item.Text = (r.SwitchOk?"✅":"❌") + $" {sw.Elapsed.TotalSeconds:F1}s";
                        item.SubItems[1].Text = r.CurrentBranch;
                        Log($"[{r.Name}] {r.LastMessage?.Replace("\n"," ")}");
                        if(r.SwitchOk) { ApplyImageTo(pbFlash,"flash_success",FLASH_BOX); pbFlash.Visible=true; flashTimer.Start(); }
                        statusLabel.Text = $"处理中 {++done}/{items.Count}";
                    }));
                }));
            }
            await Task.WhenAll(tasks);
            batchSw.Stop();

            // [新增] 统计更新与上报
            _settings.CheckDateReset();
            _settings.TodaySwitchCount++;
            _settings.TodayTotalSeconds += batchSw.Elapsed.TotalSeconds;
            _settings.Save();
            UpdateStatsUi();
            
            // 默默上传数据到共享文件
            if(!string.IsNullOrEmpty(_settings.LeaderboardPath)) {
                _ = LeaderboardService.UploadMyScoreAsync(batchSw.Elapsed.TotalSeconds);
            }

            SetSwitchState(SwitchState.Done); statusProgress.Visible=false; btnSwitchAll.Enabled=true; statusLabel.Text="完成"; Log("🏁 全部完成");
        }
        private void Log(string s) => txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\r\n");
    }
}