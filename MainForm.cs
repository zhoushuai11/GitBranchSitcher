using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Text;

namespace GitBranchSwitcher {
    public partial class MainForm : Form {
        // ==========================================
        // [关键修复] 必须在这里定义这三个 GroupBox
        // ==========================================
        private GroupBox grpTop; // 顶部工程区
        private GroupBox grpList; // 左侧列表区
        private GroupBox grpActions; // 右侧操作区

        // === 布局容器 ===
        private SplitContainer splitGlobal;
        private SplitContainer splitUpper;
        private SplitContainer splitMiddle;
        private Form consoleWindow;          // [新增] 独立的控制台窗口
        private TableLayoutPanel layoutMain; // 如果不用 SplitContainer 全局布局，备用

        // === 控件定义 ===
        private CheckedListBox lbParents;
        private Button btnAddParent, btnRemoveParent, btnSelectAllParents, btnClearParents;

        private ListView lvRepos;
        private FlowLayoutPanel repoToolbar;

        private GroupBox grpDetails;
        private SplitContainer splitConsole;
        private ListView lvFileChanges;
        private RichTextBox rtbDiff;
        private Panel pnlDetailRight, pnlActions;
        private Label lblRepoInfo;
        private TextBox txtCommitMsg;
        private Button btnCommit, btnPull, btnPush, btnStash;

        private GroupBox grpLog;
        private TextBox txtLog;

        // 右侧操作区控件
        private Label lblTargetBranch, lblFetchStatus;
        private ComboBox cmbTargetBranch;
        private Button btnSwitchAll, btnUseCurrentBranch, btnToggleConsole;
        private CheckBox chkStashOnSwitch, chkFastMode;
        private FlowLayoutPanel statePanel;
        private PictureBox pbState, pbFlash;
        private Label lblStateText;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel statusLabel, statusStats;
        private ToolStripProgressBar statusProgress;
        private System.Windows.Forms.Timer flashTimer;

        // === 数据 ===
        private readonly BindingList<GitRepo> _repos = new BindingList<GitRepo>();
        private List<string> _allBranches = new List<string>();
        private AppSettings _settings;
        private System.Threading.CancellationTokenSource? _loadCts;
        private int _loadSeq = 0;
        private HashSet<string> _checkedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private ListViewGroup grpStaged, grpUnstaged;
        private const int TARGET_BOX = 500, FLASH_BOX = 300;

        private GitWorkflowService _workflowService;

        private enum SwitchState {
            NotStarted,
            Switching,
            Done
        }

        private int done = 0;
        private System.Threading.SemaphoreSlim sem = new System.Threading.SemaphoreSlim(16);
        private List<Task> tasks = new List<Task>();

        public MainForm() {
            _settings = AppSettings.Load();
            InitializeComponent();
#if !BOSS_MODE
            TrySetRuntimeIcon();
#endif
            InitUi();
#if !BOSS_MODE
            LoadStateImagesRandom();
            LeaderboardService.SetPath(_settings.LeaderboardPath);
            _ = InitMyStatsAsync();
#endif
            SetSwitchState(SwitchState.NotStarted);
            SeedParentsToUi();

            if (_settings.CachedBranchList != null && _settings.CachedBranchList.Count > 0) {
                _allBranches = new List<string>(_settings.CachedBranchList);
                UpdateBranchDropdown();
            }

            _ = LoadReposForCheckedParentsAsync(false);

            _workflowService = new GitWorkflowService(_settings.MaxParallel);
        }

        protected override void OnShown(EventArgs e) {
            base.OnShown(e);
            ConfigureInitialLayout();
#if !PURE_MODE
            _ = UpdateService.CheckAndUpdateAsync(_settings.UpdateSourcePath, this);
#endif
        }

        private void ConfigureInitialLayout() {
            try {
                // 设置左侧的分割比例
                splitGlobal.SplitterDistance = (int)(this.Height * 0.75); // 日志区域稍微小一点
                splitUpper.SplitterDistance = 140;
                splitMiddle.SplitterDistance = (int)(splitMiddle.Width * 0.65); // 列表占宽一点
            } catch {
            }
        }

        private async Task InitMyStatsAsync() {
            if (!string.IsNullOrEmpty(_settings.LeaderboardPath)) {
                var (c, t, s) = await LeaderboardService.GetMyStatsAsync();
                UpdateStatsUi(c, t, s);
            }
        }

        private void InitializeComponent() {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            string vStr = $"{version.Major}.{version.Minor}.{version.Build}";
#if PURE_MODE
            Text = $"Git 分支管理工具 (Pure) - v{vStr}";
#elif BOSS_MODE
            Text = $"Git 分支管理工具 (Enterprise) - v{vStr}";
#else
            Text = $"Unity 项目切线工具 (Slim King) - v{vStr}";
#endif
            Width = 1450;
            Height = 950;
            StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            this.BackColor = Color.WhiteSmoke;
        }

        private Button MakeBtn(string text, Color? backColor = null) {
            var b = new Button {
                Text = text,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Height = 28
            };
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Color.LightGray;
            if (backColor.HasValue)
                b.BackColor = backColor.Value;
            else
                b.BackColor = Color.White;
            return b;
        }

        private void InitUi() {
            // === 全局布局容器 ===
            splitGlobal = new SplitContainer {
                Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6
            };
            splitUpper = new SplitContainer {
                Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6
            };
            splitMiddle = new SplitContainer {
                Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 6
            };

            // ==========================================
            // 1. 工程区 (grpTop)
            // ==========================================
            grpTop = new GroupBox {
                Text = "① 工程区 (Project Workspace)", Dock = DockStyle.Fill, Padding = new Padding(10)
            };

            var pnlTopContent = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1
            };
            pnlTopContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pnlTopContent.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            lbParents = new CheckedListBox {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                IntegralHeight = false,
                BorderStyle = BorderStyle.None,
                BackColor = Color.WhiteSmoke
            };

            var pnlTopBtns = new FlowLayoutPanel {
                AutoSize = true, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Right, Margin = new Padding(0)
            };

            btnAddParent = MakeBtn("📂 添加父目录...", Color.AliceBlue);
            btnAddParent.Width = 140;
            btnRemoveParent = MakeBtn("🗑️ 移除选中");
            btnRemoveParent.Width = 140;

            var btnSelectAll = MakeBtn("全选");
            btnSelectAll.Width = 68;
            var btnSelectNone = MakeBtn("全不选");
            btnSelectNone.Width = 68;
            var pnlSelectBtns = new FlowLayoutPanel {
                AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0)
            };
            pnlSelectBtns.Controls.Add(btnSelectAll);
            pnlSelectBtns.Controls.Add(btnSelectNone);

            pnlTopBtns.Controls.Add(btnAddParent);
            pnlTopBtns.Controls.Add(btnRemoveParent);
            pnlTopBtns.Controls.Add(pnlSelectBtns);

            pnlTopContent.Controls.Add(lbParents, 0, 0);
            pnlTopContent.Controls.Add(pnlTopBtns, 1, 0);
            grpTop.Controls.Add(pnlTopContent);

            // 绑定 Top 事件
            var cm = new ContextMenuStrip();
            cm.Items.Add("添加父目录…", null, (_, __) => btnAddParent.PerformClick());
            cm.Items.Add("移除选中", null, (_, __) => btnRemoveParent.PerformClick());
            lbParents.ContextMenuStrip = cm;
            btnAddParent.Click += (_, __) => {
                using var fbd = new FolderBrowserDialog();
                if (fbd.ShowDialog(this) == DialogResult.OK) {
                    var path = fbd.SelectedPath.Trim();
                    if (!Directory.Exists(path))
                        return;
                    if (!_settings.ParentPaths.Contains(path)) {
                        _settings.ParentPaths.Add(path);
                        _settings.Save();
                    }

                    SeedParentsToUi();
                    _ = LoadReposForCheckedParentsAsync(true);
                }
            };
            btnRemoveParent.Click += async (_, __) => {
                var rm = new List<string>();
                foreach (var i in lbParents.SelectedItems)
                    rm.Add(i.ToString());
                foreach (var i in lbParents.CheckedItems)
                    rm.Add(i.ToString());
                foreach (var p in rm) {
                    _settings.ParentPaths.Remove(p);
                    _checkedParents.Remove(p);
                }

                _settings.Save();
                SeedParentsToUi();
                await LoadReposForCheckedParentsAsync(true);
            };
            btnSelectAll.Click += async (_, __) => {
                _checkedParents = new HashSet<string>(_settings.ParentPaths);
                for (int i = 0; i < lbParents.Items.Count; i++)
                    lbParents.SetItemChecked(i, true);
                await LoadReposForCheckedParentsAsync(false);
            };
            btnSelectNone.Click += async (_, __) => {
                _checkedParents.Clear();
                for (int i = 0; i < lbParents.Items.Count; i++)
                    lbParents.SetItemChecked(i, false);
                await LoadReposForCheckedParentsAsync(true);
            };
            lbParents.ItemCheck += async (_, e) => {
                var p = lbParents.Items[e.Index].ToString();
                BeginInvoke(new Action(async () => {
                    if (lbParents.GetItemChecked(e.Index))
                        _checkedParents.Add(p);
                    else
                        _checkedParents.Remove(p);
                    await LoadReposForCheckedParentsAsync(false);
                }));
            };

            // 放入 SplitUpper 上部
            splitUpper.Panel1.Controls.Add(grpTop);

            // ==========================================
            // 2. 仓库列表 (grpList)
            // ==========================================
            grpList = new GroupBox {
                Text = "② 仓库列表 (Repositories)", Dock = DockStyle.Fill, Padding = new Padding(5)
            };
            repoToolbar = new FlowLayoutPanel {
                Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 5)
            };

            var btnToggleSelect = MakeBtn("✅ 全选/反选");
            var btnRescan = MakeBtn("🔄 刷新");
            var btnNewClone = MakeBtn("➕ 新建拉线", Color.Azure);
            btnNewClone.ForeColor = Color.DarkBlue;
#if !BOSS_MODE && !PURE_MODE
            var btnRank = MakeBtn("🏆 排行榜", Color.Ivory);
            btnRank.ForeColor = Color.DarkGoldenrod;
#endif
            var btnSuperSlim = MakeBtn("🔥 一键瘦身", Color.MistyRose);
            btnSuperSlim.ForeColor = Color.DarkRed;

            repoToolbar.Controls.Add(btnToggleSelect);
            repoToolbar.Controls.Add(btnRescan);
            repoToolbar.Controls.Add(new Label {
                Width = 10
            });
            repoToolbar.Controls.Add(btnNewClone);
            repoToolbar.Controls.Add(new Label {
                Width = 10
            });
#if !BOSS_MODE && !PURE_MODE
            repoToolbar.Controls.Add(btnRank);
#endif
            repoToolbar.Controls.Add(btnSuperSlim);

            lvRepos = new ListView {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                CheckBoxes = true,
                BorderStyle = BorderStyle.FixedSingle
            };
            lvRepos.Columns.Add("状态", 50);
            lvRepos.Columns.Add("当前分支", 280);
            lvRepos.Columns.Add("仓库名", 180);
            lvRepos.Columns.Add("路径", 400);
            try {
                var prop = typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                prop?.SetValue(lvRepos, true, null);
            } catch {
            }

            grpList.Controls.Add(lvRepos);
            grpList.Controls.Add(repoToolbar);

            // 放入 SplitMiddle 左部
            splitMiddle.Panel1.Controls.Add(grpList);

            // 事件
            lvRepos.SelectedIndexChanged += async (_, __) => await RefreshRepoDetails();
            btnToggleSelect.Click += (_, __) => {
                bool hasUn = lvRepos.Items.Cast<ListViewItem>().Any(i => !i.Checked);
                lvRepos.BeginUpdate();
                foreach (ListViewItem i in lvRepos.Items)
                    i.Checked = hasUn;
                lvRepos.EndUpdate();
            };
            btnRescan.Click += async (_, __) => await LoadReposForCheckedParentsAsync(true);
            btnNewClone.Click += (_, __) => {
                var form = new CloneForm();
                if (form.ShowDialog(this) == DialogResult.OK && form.CreatedWorkspaces.Count > 0) {
                    bool c = false;
                    foreach (var p in form.CreatedWorkspaces) {
                        if (!_settings.ParentPaths.Contains(p)) {
                            _settings.ParentPaths.Add(p);
                            _checkedParents.Add(p);
                            c = true;
                        }
                    }

                    if (c) {
                        _settings.Save();
                        SeedParentsToUi();
                        _ = LoadReposForCheckedParentsAsync(true);
                    }
                }
            };
#if !BOSS_MODE && !PURE_MODE
            btnRank.Click += (_, __) => ShowLeaderboard();
#endif
            btnSuperSlim.Click += (_, __) => StartSuperSlimProcess();
            var listMenu = new ContextMenuStrip();
            listMenu.Items.Add("📂 打开文件夹", null, (_, __) => {
                if (lvRepos.SelectedItems.Count > 0)
                    Process.Start("explorer.exe", ((GitRepo)lvRepos.SelectedItems[0].Tag).Path);
            });
            listMenu.Items.Add("🛠️ 修复锁文件", null, async (_, __) => {
                if (lvRepos.SelectedItems.Count == 0)
                    return;
                var r = (GitRepo)lvRepos.SelectedItems[0].Tag;
                await Task.Run(() => GitHelper.RepairRepo(r.Path));
                MessageBox.Show("修复完成");
            });
            lvRepos.ContextMenuStrip = listMenu;

            // ==========================================
            // 3. 快捷操作 (grpActions)
            // ==========================================
            grpActions = new GroupBox {
                Text = "③ 快捷操作 (Actions)", Dock = DockStyle.Fill, Padding = new Padding(10)
            };

            var pnlActionContent = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true
            };
            // 设置行布局
            for (int k = 0; k < 7; k++)
                pnlActionContent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            pnlActionContent.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            lblFetchStatus = new Label {
                Text = "", AutoSize = true, ForeColor = Color.Magenta, Font = new Font("Segoe UI", 9, FontStyle.Italic)
            };
            lblTargetBranch = new Label {
                Text = "🎯 目标分支：", AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold), Margin = new Padding(0, 10, 0, 5)
            };

            var pnlComboRow = new FlowLayoutPanel {
                AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0)
            };
            cmbTargetBranch = new ComboBox {
                Width = 260, DropDownStyle = ComboBoxStyle.DropDown
            };
            btnUseCurrentBranch = MakeBtn("👈 填入");
            pnlComboRow.Controls.Add(cmbTargetBranch);
            pnlComboRow.Controls.Add(btnUseCurrentBranch);

            btnSwitchAll = new Button {
                Text = "🚀 一键切线 (Switch)",
                Height = 50,
                Dock = DockStyle.Top,
                BackColor = Color.DodgerBlue,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 12, FontStyle.Bold)
            };
            btnSwitchAll.FlatAppearance.BorderSize = 0;

            chkStashOnSwitch = new CheckBox {
                Text = "🔒 尝试 Stash 本地修改 [推荐]",
                AutoSize = true,
                Checked = _settings.StashOnSwitch,
                ForeColor = Color.DarkSlateBlue,
                Margin = new Padding(0, 10, 0, 0)
            };
            chkFastMode = new CheckBox {
                Text = "⚡ 极速本地切换 (跳过 Fetch)",
                AutoSize = true,
                Checked = _settings.FastMode,
                ForeColor = Color.DarkGreen,
                Font = new Font(DefaultFont, FontStyle.Bold)
            };

            btnToggleConsole = MakeBtn("💻 打开/关闭 Git 控制台", Color.OldLace);
            btnToggleConsole.Width = 200;
            btnToggleConsole.Height = 35;
            btnToggleConsole.Margin = new Padding(0, 15, 0, 0);

            statePanel = new FlowLayoutPanel {
                Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 20, 0, 0)
            };
            pbState = new PictureBox {
                Width = TARGET_BOX, Height = TARGET_BOX, SizeMode = PictureBoxSizeMode.CenterImage
            };
            lblStateText = new Label {
                Text = "Ready", Font = new Font("Segoe UI", 14, FontStyle.Bold), AutoSize = true, ForeColor = Color.Gray
            };
            pbFlash = new PictureBox {
                Width = FLASH_BOX, Height = FLASH_BOX, Visible = false, SizeMode = PictureBoxSizeMode.CenterImage
            };
            statePanel.Controls.Add(pbState);
            statePanel.Controls.Add(lblStateText);
            statePanel.Controls.Add(pbFlash);

            pnlActionContent.Controls.Add(lblFetchStatus);
            pnlActionContent.Controls.Add(lblTargetBranch);
            pnlActionContent.Controls.Add(pnlComboRow);
            pnlActionContent.Controls.Add(new Label {
                Height = 10
            });
            pnlActionContent.Controls.Add(btnSwitchAll);
            pnlActionContent.Controls.Add(chkStashOnSwitch);
            pnlActionContent.Controls.Add(chkFastMode);
            pnlActionContent.Controls.Add(btnToggleConsole);
            pnlActionContent.Controls.Add(statePanel);
            grpActions.Controls.Add(pnlActionContent);

            // 放入 SplitMiddle 右部
            splitMiddle.Panel2.Controls.Add(grpActions);

            // 组合 SplitUpper
            splitUpper.Panel2.Controls.Add(splitMiddle);

            // 事件绑定
            btnUseCurrentBranch.Click += (_, __) => {
                var item = lvRepos.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Checked);
                if (item == null) {
                    MessageBox.Show("请先勾选");
                    return;
                }

                var repo = (GitRepo)item.Tag;
                var branch = repo.CurrentBranch;
                if (!string.IsNullOrEmpty(branch) && branch != "—") {
                    cmbTargetBranch.SelectedIndex = -1;
                    cmbTargetBranch.Text = branch;
                } else {
                    MessageBox.Show("无效分支");
                }
            };
            cmbTargetBranch.TextUpdate += (_, __) => {
                try {
                    UpdateBranchDropdown();
                } catch {
                }
            };
            chkStashOnSwitch.CheckedChanged += (_, __) => {
                _settings.StashOnSwitch = chkStashOnSwitch.Checked;
                _settings.Save();
            };
            chkFastMode.CheckedChanged += (_, __) => {
                _settings.FastMode = chkFastMode.Checked;
                _settings.Save();
            };
            btnSwitchAll.Click += async (_, __) => await SwitchAllAsync();
            flashTimer = new System.Windows.Forms.Timer {
                Interval = 800
            };
            flashTimer.Tick += (_, __) => {
                pbFlash.Visible = false;
                flashTimer.Stop();
            };

            // [修改后]
            btnToggleConsole.Text = "💻 打开 Git 控制台";
            btnToggleConsole.Click += (_, __) => {
                if (consoleWindow.Visible) {
                    consoleWindow.Hide();
                    btnToggleConsole.Text = "💻 打开 Git 控制台";
                } else {
                    consoleWindow.Show(); // 显示独立窗口
                    if (consoleWindow.WindowState == FormWindowState.Minimized) 
                        consoleWindow.WindowState = FormWindowState.Normal;
                    consoleWindow.Activate(); // 激活焦点
                    btnToggleConsole.Text = "💻 关闭 Git 控制台";
                }
            };

            // ==========================================
            // 4. Git 控制台 (grpDetails)
            // ==========================================
            grpDetails = new GroupBox {
                Text = "④ Git 控制台 (Console)", Dock = DockStyle.Fill, Padding = new Padding(5), BackColor = Color.White
            };
            splitConsole = new SplitContainer {
                Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 5
            };

            lvFileChanges = new ListView {
                Dock = DockStyle.Fill,
                View = View.Details,
                GridLines = false,
                FullRowSelect = true,
                BorderStyle = BorderStyle.FixedSingle,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                ShowGroups = true,
                MultiSelect = false
            };
            grpStaged = new ListViewGroup("staged", "已暂存 (Staged)");
            grpUnstaged = new ListViewGroup("unstaged", "未暂存 (Unstaged)");
            lvFileChanges.Groups.Add(grpStaged);
            lvFileChanges.Groups.Add(grpUnstaged);
            lvFileChanges.Columns.Add("状态", 40);
            lvFileChanges.Columns.Add("文件路径", 500);

            pnlDetailRight = new Panel {
                Dock = DockStyle.Fill
            };
            lblRepoInfo = new Label {
                Dock = DockStyle.Top,
                Height = 25,
                Text = "请选择仓库...",
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Color.DarkSlateGray,
                BackColor = Color.WhiteSmoke
            };

            pnlActions = new Panel {
                Dock = DockStyle.Bottom, Height = 95, Padding = new Padding(5)
            };
            txtCommitMsg = new TextBox {
                Dock = DockStyle.Top,
                Height = 55,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                PlaceholderText = "Commit Message...",
                BorderStyle = BorderStyle.FixedSingle
            };
            var pnlBtns = new FlowLayoutPanel {
                Dock = DockStyle.Bottom, Height = 32, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 3, 0, 0)
            };
            btnCommit = new Button {
                Text = "Commit",
                Width = 90,
                Height = 28,
                BackColor = Color.DodgerBlue,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnCommit.FlatAppearance.BorderSize = 0;
            btnPush = MakeBtn("⬆ Push", Color.AliceBlue);
            btnPush.Width = 80;
            btnPull = MakeBtn("⬇ Pull", Color.AliceBlue);
            btnPull.Width = 80;
            btnStash = MakeBtn("📦 Stash");
            btnStash.Width = 70;
            pnlBtns.Controls.Add(btnCommit);
            pnlBtns.Controls.Add(btnPush);
            pnlBtns.Controls.Add(btnPull);
            pnlBtns.Controls.Add(btnStash);
            pnlActions.Controls.Add(txtCommitMsg);
            pnlActions.Controls.Add(pnlBtns);

            rtbDiff = new RichTextBox {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                WordWrap = false
            };

            pnlDetailRight.Controls.Add(rtbDiff);
            pnlDetailRight.Controls.Add(lblRepoInfo);
            pnlDetailRight.Controls.Add(pnlActions);

            splitConsole.Panel1.Controls.Add(lvFileChanges);
            splitConsole.Panel2.Controls.Add(pnlDetailRight);
            grpDetails.Controls.Add(splitConsole);

            // Bind Console Events
            lvFileChanges.SelectedIndexChanged += async (_, __) => await ShowSelectedFileDiff();
            lvFileChanges.DoubleClick += async (_, __) => await ToggleStagedStatus();
            btnCommit.Click += async (_, __) => await RunDetailAction("Commit");
            btnPull.Click += async (_, __) => await RunDetailAction("Pull");
            btnPush.Click += async (_, __) => await RunDetailAction("Push");
            btnStash.Click += async (_, __) => await RunDetailAction("Stash");
            var fileMenu = new ContextMenuStrip();
            fileMenu.Items.Add("➕ 加入/移出 暂存区", null, async (_, __) => await ToggleStagedStatus());
            fileMenu.Items.Add("📂 打开目录", null, (_, __) => {
                if (lvFileChanges.SelectedItems.Count > 0 && lvRepos.SelectedItems.Count > 0)
                    Process.Start("explorer.exe", "/select,\"" + Path.Combine(((GitRepo)lvRepos.SelectedItems[0].Tag).Path, lvFileChanges.SelectedItems[0].SubItems[1].Text) + "\"");
            });
            var itemDiscard = fileMenu.Items.Add("🧨 还原", null, async (_, __) => {
                if (lvFileChanges.SelectedItems.Count == 0)
                    return;
                var item = lvFileChanges.SelectedItems[0];
                if (item.Group == grpStaged) {
                    MessageBox.Show("请先 Unstage。");
                    return;
                }

                if (MessageBox.Show("确定丢弃修改？", "确认", MessageBoxButtons.YesNo) == DialogResult.Yes) {
                    var r = (GitRepo)lvRepos.SelectedItems[0].Tag;
                    await Task.Run(() => {
                        GitHelper.RunGit(r.Path, $"checkout -- \"{item.SubItems[1].Text}\"", 5000);
                        if (item.Text.Contains("??"))
                            GitHelper.RunGit(r.Path, $"clean -f \"{item.SubItems[1].Text}\"", 5000);
                    });
                    await RefreshRepoDetails();
                }
            });
            lvFileChanges.ContextMenuStrip = fileMenu;
            
            // [关键修改] 初始化独立窗口，而不是放入 SplitContainer
            consoleWindow = new Form {
                Text = "Git 控制台 (独立视图)",
                Width = 1000,
                Height = 700,
                StartPosition = FormStartPosition.CenterScreen,
                Icon = this.Icon, // 保持图标一致
                ShowInTaskbar = false // 可选：设为 false 让它像工具窗口；设为 true 则在任务栏有独立图标
            };
            // 将控制台面板放入窗口
            consoleWindow.Controls.Add(grpDetails);
    
            // [重要] 拦截关闭事件：点击关闭时只是隐藏，而不是销毁
            consoleWindow.FormClosing += (s, e) => {
                if (e.CloseReason == CloseReason.UserClosing) {
                    e.Cancel = true; // 阻止销毁
                    consoleWindow.Hide(); // 只是隐藏
                    btnToggleConsole.Text = "💻 打开 Git 控制台";
                }
            };

            // ==========================================
            // 5. 运行日志 (grpLog)
            // ==========================================
            grpLog = new GroupBox {
                Text = "⑤ 运行日志 (Logs)", Dock = DockStyle.Fill
            };
            txtLog = new TextBox {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                ReadOnly = true,
                Font = new Font("Consolas", 9),
                BackColor = Color.WhiteSmoke,
                BorderStyle = BorderStyle.None
            };
            grpLog.Controls.Add(txtLog);

            // ==========================================
            // 全局组装
            // ==========================================
            splitMiddle.Panel1.Controls.Add(grpList);
            splitMiddle.Panel2.Controls.Add(grpActions);
            splitUpper.Panel1.Controls.Add(grpTop);
            splitUpper.Panel2.Controls.Add(splitMiddle);

            // B. 组装左侧整体 (Top: splitUpper, Bottom: grpLog)
            splitGlobal.Panel1.Controls.Add(splitUpper);
            splitGlobal.Panel2.Controls.Add(grpLog); // 日志现在占据左侧下方

            statusStrip = new StatusStrip();
            statusLabel = new ToolStripStatusLabel("就绪") {
                Margin = new Padding(10, 0, 0, 0)
            };
            statusStrip.Items.Add(statusLabel);
            statusStrip.Items.Add(new ToolStripStatusLabel {
                Spring = true
            });
#if !BOSS_MODE && !PURE_MODE
            statusStats = new ToolStripStatusLabel {
                Alignment = ToolStripItemAlignment.Right, ForeColor = Color.SteelBlue, Margin = new Padding(0, 0, 10, 0)
            };
            statusStrip.Items.Add(statusStats);
#endif
            statusProgress = new ToolStripProgressBar {
                Visible = false, Style = ProgressBarStyle.Marquee, Width = 200
            };
            statusStrip.Items.Add(statusProgress);
            // 确保顺序：先加 StatusStrip (Bottom)，再加 splitGlobal (Fill)
            Controls.Add(statusStrip);
            Controls.Add(splitGlobal);
        }

        // === 逻辑方法 (保持原样) ===
        private void SeedParentsToUi() {
            if (lbParents == null)
                return;
            lbParents.BeginUpdate();
            lbParents.Items.Clear();
            foreach (var p in _settings.ParentPaths) {
                int i = lbParents.Items.Add(p);
                if (_checkedParents.Contains(p))
                    lbParents.SetItemChecked(i, true);
            }

            lbParents.EndUpdate();
        }

        private void RenderRepoItem(ListViewItem item) {
            if (item == null || item.Tag == null)
                return;
            var repo = (GitRepo)item.Tag;
            string display = repo.CurrentBranch;
            if (repo.IsSyncChecked) {
                if (!repo.HasUpstream) {
                    display += "  ⚠️ 无远程";
                    item.SubItems[1].ForeColor = Color.Gray;
                } else if (repo.Incoming == 0 && repo.Outgoing == 0) {
                    display += "  ✔ 最新";
                    item.SubItems[1].ForeColor = Color.Green;
                } else {
                    string arrows = "";
                    if (repo.Outgoing > 0)
                        arrows += $" {repo.Outgoing}↑";
                    if (repo.Incoming > 0)
                        arrows += $" {repo.Incoming}↓";
                    display += $"  {arrows}";
                    if (repo.Incoming > 0)
                        item.SubItems[1].ForeColor = Color.Red;
                    else
                        item.SubItems[1].ForeColor = Color.Blue;
                }
            } else {
                item.SubItems[1].ForeColor = Color.Black;
            }

            item.SubItems[1].Text = display;
        }

        private async Task BatchSyncStatusUpdate() {
            if (lvRepos.Items.Count == 0)
                return;
            var targetItems = new List<ListViewItem>();
            foreach (ListViewItem i in lvRepos.Items)
                targetItems.Add(i);
            statusLabel.Text = "正在后台扫描同步状态...";
            await Task.Run(() => {
                var opts = new ParallelOptions {
                    MaxDegreeOfParallelism = 10
                };
                Parallel.ForEach(targetItems, opts, (item) => {
                    var repo = (GitRepo)item.Tag;
                    var syncResult = GitHelper.GetSyncCounts(repo.Path);
                    repo.IsSyncChecked = true;
                    if (syncResult == null) {
                        repo.HasUpstream = false;
                        repo.Incoming = 0;
                        repo.Outgoing = 0;
                    } else {
                        repo.HasUpstream = true;
                        repo.Incoming = syncResult.Value.behind;
                        repo.Outgoing = syncResult.Value.ahead;
                    }

                    try {
                        BeginInvoke((Action)(() => RenderRepoItem(item)));
                    } catch {
                    }
                });
            });
            BeginInvoke((Action)(() => statusLabel.Text = "就绪"));
        }

        private async Task RefreshRepoDetails() {
            if (splitConsole.SplitterDistance < 50)
                splitConsole.SplitterDistance = (int)(splitConsole.Width * 0.4);
            if (lvRepos.SelectedItems.Count == 0) {
                grpDetails.Enabled = false;
                lblRepoInfo.Text = "请选择一个仓库...";
                lvFileChanges.Items.Clear();
                rtbDiff.Clear();
                return;
            }

            grpDetails.Enabled = true;
            var item = lvRepos.SelectedItems[0];
            var repo = (GitRepo)item.Tag;
            lblRepoInfo.Text = $"📂 {repo.Name}  /  📍 {repo.CurrentBranch}";
            await Task.Run(() => {
                var changes = GitHelper.GetFileChanges(repo.Path);
                var syncResult = GitHelper.GetSyncCounts(repo.Path);
                repo.IsSyncChecked = true;
                if (syncResult != null) {
                    repo.HasUpstream = true;
                    repo.Incoming = syncResult.Value.behind;
                    repo.Outgoing = syncResult.Value.ahead;
                } else {
                    repo.HasUpstream = false;
                }

                BeginInvoke((Action)(() => {
                    lvFileChanges.BeginUpdate();
                    lvFileChanges.Items.Clear();
                    int stagedCount = 0;
                    foreach (var c in changes) {
                        char x = c.RawStatus[0];
                        char y = c.RawStatus[1];
                        if (x != ' ' && x != '?') {
                            var lvi = new ListViewItem(x.ToString()) {
                                Group = grpStaged, ForeColor = Color.SeaGreen, Font = new Font(DefaultFont, FontStyle.Bold)
                            };
                            lvi.SubItems.Add(c.FilePath);
                            lvFileChanges.Items.Add(lvi);
                            stagedCount++;
                        }

                        if (y != ' ' || c.RawStatus == "??") {
                            string status = (c.RawStatus == "??")? "??" : y.ToString();
                            var lvi = new ListViewItem(status) {
                                Group = grpUnstaged
                            };
                            if (status == "M")
                                lvi.ForeColor = Color.RoyalBlue;
                            else if (status == "D")
                                lvi.ForeColor = Color.Crimson;
                            else
                                lvi.ForeColor = Color.Gray;
                            lvi.SubItems.Add(c.FilePath);
                            lvFileChanges.Items.Add(lvi);
                        }
                    }

                    lvFileChanges.Columns[1].Width = -2;
                    if (lvFileChanges.Columns[1].Width < 300)
                        lvFileChanges.Columns[1].Width = 300;
                    lvFileChanges.EndUpdate();
                    btnPull.Text = repo.Incoming > 0? $"⬇ {repo.Incoming}" : "⬇ Pull";
                    btnPush.Text = repo.Outgoing > 0? $"⬆ {repo.Outgoing}" : "⬆ Push";
                    btnCommit.Enabled = stagedCount > 0;
                    btnCommit.Text = stagedCount > 0? $"Commit ({stagedCount})" : "Commit";
                    RenderRepoItem(item);
                }));
            });
        }

        private async Task ShowSelectedFileDiff() {
            if (lvFileChanges.SelectedItems.Count == 0 || lvRepos.SelectedItems.Count == 0) {
                rtbDiff.Clear();
                return;
            }

            var repo = (GitRepo)lvRepos.SelectedItems[0].Tag;
            var fileItem = lvFileChanges.SelectedItems[0];
            string filePath = fileItem.SubItems[1].Text;
            bool isStaged = (fileItem.Group == grpStaged);
            bool isUntracked = fileItem.Text.Contains("??") || fileItem.Text.Contains("A") && !isStaged;
            await Task.Run(() => {
                string diffContent = GitHelper.GetFileDiff(repo.Path, filePath, isStaged, isUntracked);
                BeginInvoke((Action)(() => ColorizeDiff(diffContent)));
            });
        }

        private void ColorizeDiff(string content) {
            rtbDiff.Text = "";
            if (string.IsNullOrEmpty(content))
                return;
            string[] lines = content.Split('\n');
            foreach (var line in lines) {
                int start = rtbDiff.TextLength;
                rtbDiff.AppendText(line + "\n");
                rtbDiff.Select(start, line.Length);
                if (line.StartsWith("+") && !line.StartsWith("+++"))
                    rtbDiff.SelectionColor = Color.LightGreen;
                else if (line.StartsWith("-") && !line.StartsWith("---"))
                    rtbDiff.SelectionColor = Color.LightSalmon;
                else if (line.StartsWith("@@"))
                    rtbDiff.SelectionColor = Color.LightSkyBlue;
                else
                    rtbDiff.SelectionColor = Color.Gainsboro;
            }

            rtbDiff.Select(0, 0);
            rtbDiff.ScrollToCaret();
        }

        private async Task ToggleStagedStatus() {
            if (lvFileChanges.SelectedItems.Count == 0 || lvRepos.SelectedItems.Count == 0)
                return;
            var repo = (GitRepo)lvRepos.SelectedItems[0].Tag;
            var item = lvFileChanges.SelectedItems[0];
            string file = item.SubItems[1].Text;
            lvFileChanges.Enabled = false;
            await Task.Run(() => {
                if (item.Group == grpUnstaged)
                    GitHelper.StageFile(repo.Path, file);
                else
                    GitHelper.UnstageFile(repo.Path, file);
            });
            await RefreshRepoDetails();
            lvFileChanges.Enabled = true;
        }

        private async Task RunDetailAction(string action) {
            if (lvRepos.SelectedItems.Count == 0)
                return;
            var repo = (GitRepo)lvRepos.SelectedItems[0].Tag;
            pnlActions.Enabled = false;
            try {
                if (action == "Commit") {
                    string msg = txtCommitMsg.Text.Trim();
                    if (string.IsNullOrEmpty(msg)) {
                        MessageBox.Show("请输入提交信息");
                        return;
                    }

                    var res = await Task.Run(() => GitHelper.Commit(repo.Path, msg));
                    if (res.ok) {
                        txtCommitMsg.Clear();
                        Log($"[{repo.Name}] Commit Success: {msg}");
                    } else
                        MessageBox.Show(res.message);
                } else if (action == "Stash") {
                    await Task.Run(() => GitHelper.RunGit(repo.Path, "stash", 10000));
                    Log($"[{repo.Name}] Stashed");
                } else if (action == "Pull") {
                    var res = await Task.Run(() => GitHelper.PullCurrentBranch(repo.Path));
                    Log($"[{repo.Name}] Pull: {res.message}");
                } else if (action == "Push") {
                    var res = await Task.Run(() => GitHelper.Push(repo.Path));
                    if (res.ok)
                        Log($"[{repo.Name}] Push OK");
                    else
                        MessageBox.Show(res.msg);
                }
            } finally {
                pnlActions.Enabled = true;
                await RefreshRepoDetails();
            }
        }

        private async Task LoadReposForCheckedParentsAsync(bool forceRescan = false) {
            _loadCts?.Cancel();
            _loadCts = new System.Threading.CancellationTokenSource();
            var token = _loadCts.Token;
            var seq = ++_loadSeq;
            lvRepos.BeginUpdate();
            lvRepos.Items.Clear();
            lvRepos.EndUpdate();
            _repos.Clear();
            _allBranches.Clear();
            cmbTargetBranch.Items.Clear();
            var parents = _checkedParents.Where(Directory.Exists).ToList();
            if (!parents.Any()) {
                statusLabel.Text = "就绪";
                SetSwitchState(SwitchState.NotStarted);
                return;
            }

            if (!forceRescan && _settings.RepositoryCache.Count > 0) {
                var finalRepos = new List<(string name, string path, string parent)>();
                bool allFound = true;
                foreach (var p in parents) {
                    var cache = _settings.RepositoryCache.FirstOrDefault(x => string.Equals(x.ParentPath, p, StringComparison.OrdinalIgnoreCase));
                    if (cache != null && cache.Children != null) {
                        foreach (var child in cache.Children)
                            if (Directory.Exists(child.FullPath))
                                finalRepos.Add((child.Name, child.FullPath, Path.GetFileName(p)));
                    } else {
                        allFound = false;
                        break;
                    }
                }

                if (allFound) {
                    lvRepos.BeginUpdate();
                    foreach (var (name, path, parentName) in finalRepos) {
                        var r = new GitRepo(name, path);
                        string display = name == "Root"? $"[{parentName}] (根)" : $"[{parentName}] {name}";
                        lvRepos.Items.Add(new ListViewItem(new[] {
                            "⏳", "—", display, path
                        }) {
                            Tag = r, Checked = true
                        });
                    }

                    lvRepos.EndUpdate();
                    statusLabel.Text = "加载完成 (缓存)";
                    StartReadBranches(token);
                    _ = BatchSyncStatusUpdate();
                    return;
                }
            }

            statusLabel.Text = "正在全盘扫描 Git 仓库...";
            statusProgress.Visible = true;
            var foundRepos = await Task.Run(() => {
                var dict = new Dictionary<string, List<SubRepoItem>>();
                foreach (var p in parents) {
                    if (token.IsCancellationRequested)
                        break;
                    var list = new List<SubRepoItem>();
                    foreach (var path in GitHelper.ScanForGitRepositories(p)) {
                        string name = string.Equals(path, p, StringComparison.OrdinalIgnoreCase)? "Root" : path.Substring(p.Length).TrimStart(Path.DirectorySeparatorChar);
                        list.Add(new SubRepoItem {
                            Name = name, FullPath = path
                        });
                    }

                    dict[p] = list;
                }

                return dict;
            });
            if (token.IsCancellationRequested || seq != _loadSeq) {
                statusProgress.Visible = false;
                return;
            }

            foreach (var kvp in foundRepos) {
                var exist = _settings.RepositoryCache.FirstOrDefault(x => string.Equals(x.ParentPath, kvp.Key, StringComparison.OrdinalIgnoreCase));
                if (exist != null)
                    _settings.RepositoryCache.Remove(exist);
                _settings.RepositoryCache.Add(new ParentRepoCache {
                    ParentPath = kvp.Key, Children = kvp.Value
                });
            }

            _settings.Save();
            lvRepos.BeginUpdate();
            foreach (var kvp in foundRepos)
                foreach (var item in kvp.Value) {
                    var r = new GitRepo(item.Name, item.FullPath);
                    string display = item.Name == "Root"? $"[{Path.GetFileName(kvp.Key)}] (根)" : $"[{Path.GetFileName(kvp.Key)}] {item.Name}";
                    lvRepos.Items.Add(new ListViewItem(new[] {
                        "⏳", "—", display, item.FullPath
                    }) {
                        Tag = r, Checked = true
                    });
                }

            lvRepos.EndUpdate();
            statusProgress.Visible = false;
            statusLabel.Text = $"扫描完成";
            StartReadBranches(token);
            _ = BatchSyncStatusUpdate();
        }

        private void StartReadBranches(System.Threading.CancellationToken token) {
            var tasks = new List<Task>();
            foreach (ListViewItem item in lvRepos.Items) {
                tasks.Add(Task.Run(() => {
                    if (token.IsCancellationRequested)
                        return;
                    ((GitRepo)item.Tag).CurrentBranch = GitHelper.GetFriendlyBranch(((GitRepo)item.Tag).Path);
                }));
            }

            _ = Task.WhenAll(tasks).ContinueWith(t => {
                if (token.IsCancellationRequested)
                    return;
                BeginInvoke((Action)(() => {
                    lvRepos.BeginUpdate();
                    foreach (ListViewItem item in lvRepos.Items)
                        RenderRepoItem(item);
                    lvRepos.EndUpdate();
                    RefreshBranchesAsync();
                    _ = AutoFetchAndRefreshAsync(token);
                }));
            });
        }

        private async Task AutoFetchAndRefreshAsync(System.Threading.CancellationToken token) {
            try {
                var allPaths = new List<string>();
                var rootPaths = new List<string>();
                foreach (ListViewItem item in lvRepos.Items) {
                    if (item.Tag is GitRepo r) {
                        allPaths.Add(r.Path);
                        if (r.Name == "Root")
                            rootPaths.Add(r.Path);
                    }
                }

                if (allPaths.Count == 0)
                    return;
                var targetPaths = rootPaths.Count > 0? rootPaths : allPaths;
                lblFetchStatus.Text = rootPaths.Count > 0? $"📡 正在同步 {targetPaths.Count} 个主仓库..." : $"📡 正在同步 {targetPaths.Count} 个仓库...";
                await Task.Run(() => {
                    var opts = new ParallelOptions {
                        MaxDegreeOfParallelism = 8
                    };
                    Parallel.ForEach(targetPaths, opts, (path) => {
                        if (token.IsCancellationRequested)
                            return;
                        GitHelper.FetchFast(path);
                    });
                });
                if (token.IsCancellationRequested)
                    return;
                BeginInvoke((Action)(() => {
                    lblFetchStatus.Text = "";
                    RefreshBranchesAsync();
                }));
                await BatchSyncStatusUpdate();
            } catch {
            }
        }

        private async Task RefreshBranchesAsync() {
            if (lvRepos == null || lvRepos.IsDisposed || lvRepos.Items.Count == 0)
                return;
            var targetPaths = new List<string>();
            foreach (ListViewItem item in lvRepos.Items) {
                if (item.Tag is GitRepo r && !string.IsNullOrEmpty(r.Path))
                    targetPaths.Add(r.Path);
            }

            var all = new HashSet<string>();
            var tasks = new List<Task<IEnumerable<string>>>();
            foreach (var path in targetPaths)
                tasks.Add(Task.Run(() => GitHelper.GetAllBranches(path)));
            try {
                var results = await Task.WhenAll(tasks);
                foreach (var list in results)
                    if (list != null)
                        foreach (var b in list)
                            all.Add(b);
            } catch (Exception ex) {
                Log($"Err: {ex.Message}");
            }

            _allBranches = all.OrderBy(x => x).ToList();
            if (_allBranches.Count > 0) {
                if (_settings.CachedBranchList == null)
                    _settings.CachedBranchList = new List<string>();
                _settings.CachedBranchList = _allBranches;
                _settings.Save();
            }

            if (cmbTargetBranch != null && !cmbTargetBranch.IsDisposed)
                UpdateBranchDropdown();
        }

        private void UpdateBranchDropdown() {
            try {
                if (cmbTargetBranch == null || cmbTargetBranch.IsDisposed)
                    return;
                string currentText = cmbTargetBranch.Text;
                cmbTargetBranch.BeginUpdate();
                cmbTargetBranch.Items.Clear();
                var src = _allBranches?.ToList() ?? new List<string>();
                var list = string.IsNullOrEmpty(currentText)? src : src.Where(b => b != null && b.IndexOf(currentText, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                foreach (var b in list.Take(500))
                    cmbTargetBranch.Items.Add(b);
                cmbTargetBranch.EndUpdate();
                cmbTargetBranch.Text = currentText;
                if (!string.IsNullOrEmpty(currentText)) {
                    cmbTargetBranch.SelectionStart = currentText.Length;
                }

                if (list.Count > 0 && cmbTargetBranch.Focused && !string.IsNullOrEmpty(currentText)) {
                    cmbTargetBranch.DroppedDown = true;
                    Cursor.Current = Cursors.Default;
                }
            } catch {
            }
        }

        private async Task SwitchAllAsync() {
            var target = cmbTargetBranch.Text.Trim();
            if (string.IsNullOrEmpty(target)) {
                MessageBox.Show("请输入分支名");
                return;
            }

            var items = lvRepos.Items.Cast<ListViewItem>().Where(i => i.Checked).ToList();
            if (!items.Any())
                return;

            var targetRepos = items.Select(i => (GitRepo)i.Tag).ToList();

            // UI 准备状态
            btnSwitchAll.Enabled = false;
            statusProgress.Visible = true;
            SetSwitchState(SwitchState.Switching);
            foreach (var i in items) {
                i.Text = "⏳";
                i.SubItems[1].Text = "...";
            }

            // 创建进度处理器
            var progressHandler = new Progress<RepoSwitchResult>(result => {
                // 这里已经在 UI 线程，直接更新控件
                // 找到对应的 ListViewItem (可以通过 Tag 或者 字典映射优化性能，这里简单演示)
                var item = items.FirstOrDefault(x => x.Tag == result.Repo);
                if (item != null) {
                    item.Text = (result.Success? "✅" : "❌") + $" {result.DurationSeconds:F1}s";
                    item.SubItems[1].Text = result.Repo.CurrentBranch;
                    // 确保 Log 方法线程安全（WinForms TextBox 本身需要 Invoke，但在 Progress 回调里通常安全）
                    Log($"[{result.Repo.Name}] {result.Message?.Replace("\n", " ")}");
                }

                statusLabel.Text = $"处理中 {result.ProgressIndex}/{result.TotalCount}";

                if (result.Success) {
                    // 播放闪烁动画逻辑...
                    ApplyImageTo(pbFlash, "flash_success", FLASH_BOX);
                    pbFlash.Visible = true;
                    flashTimer.Start();
                }
            });

            // 调用服务执行业务逻辑
            double totalSeconds = await _workflowService.SwitchReposAsync(targetRepos, target, _settings.StashOnSwitch, _settings.FastMode, progressHandler);

            // 完成后的处理
#if !BOSS_MODE && !PURE_MODE
            if (!string.IsNullOrEmpty(_settings.LeaderboardPath)) {
                var (nc, nt, ns) = await LeaderboardService.UploadMyScoreAsync(totalSeconds, 0);
                UpdateStatsUi(nc, nt, ns);
            }
#endif

            SetSwitchState(SwitchState.Done);
            statusProgress.Visible = false;
            btnSwitchAll.Enabled = true;
            statusLabel.Text = "完成";
            Log("🏁 全部完成");
        }

        private void TrySetRuntimeIcon() {
            try {
                var icon = ImageHelper.LoadIconFromResource("appicon");
                if (icon != null)
                    this.Icon = icon;
            } catch {
            }
        }

        private void ApplyImageTo(PictureBox pb, string key, int s) {
#if BOSS_MODE
            pb.Image = null;
#else
            if (pb.Image != null) {
                var o = pb.Image;
                pb.Image = null;
                o.Dispose();
            }

            var img = ImageHelper.LoadRandomImageFromResource(key);
            if (img != null) {
                pb.SizeMode = (img.Width > s || img.Height > s)? PictureBoxSizeMode.Zoom : PictureBoxSizeMode.CenterImage;
                pb.Image = img;
            }
#endif
        }

        private void LoadStateImagesRandom() {
            ApplyImageTo(pbState, "state_notstarted", TARGET_BOX);
            ApplyImageTo(pbFlash, "flash_success", FLASH_BOX);
        }

        private void SetSwitchState(SwitchState st) {
            if (st == SwitchState.NotStarted) {
                ApplyImageTo(pbState, "state_notstarted", TARGET_BOX);
                lblStateText.Text = "未开始";
            }

            if (st == SwitchState.Switching) {
                ApplyImageTo(pbState, "state_switching", TARGET_BOX);
                lblStateText.Text = "切线中...";
            }

            if (st == SwitchState.Done) {
                ApplyImageTo(pbState, "state_done", TARGET_BOX);
                lblStateText.Text = "搞定!";
            }
        }

        private void UpdateStatsUi(int totalCount = -1, double totalSeconds = -1, long totalSpace = -1) {
            if (statusStats != null) {
                int c = totalCount >= 0? totalCount : _settings.TodaySwitchCount;
                double t = totalSeconds >= 0? totalSeconds : _settings.TodayTotalSeconds;
                long s = totalSpace >= 0? totalSpace : 0;
                statusStats.Text = $"📅 累计：切线 {c} 次 | 摸鱼 {FormatDuration(t)} | 瘦身 {FormatSize(s)}";
            }
        }

        private string FormatDuration(double seconds) {
            var ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours}小时{ts.Minutes}分{ts.Seconds}秒";
            if (ts.TotalMinutes >= 1)
                return $"{ts.Minutes}分{ts.Seconds}秒";
            return $"{ts.Seconds}秒";
        }

        private string ShowInputBox(string title, string prompt, string defaultVal) {
            Form promptForm = new Form() {
                Width = 500,
                Height = 150,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                Text = title,
                StartPosition = FormStartPosition.CenterParent
            };
            Label textLabel = new Label() {
                Left = 20, Top = 20, Text = prompt, AutoSize = true
            };
            TextBox textBox = new TextBox() {
                Left = 20, Top = 50, Width = 440, Text = defaultVal
            };
            Button confirmation = new Button() {
                Text = "确定",
                Left = 360,
                Width = 100,
                Top = 80,
                DialogResult = DialogResult.OK
            };
            promptForm.Controls.Add(textLabel);
            promptForm.Controls.Add(textBox);
            promptForm.Controls.Add(confirmation);
            promptForm.AcceptButton = confirmation;
            return promptForm.ShowDialog() == DialogResult.OK? textBox.Text : "";
        }

        private string FormatSize(long bytes) {
            if (bytes == 0)
                return "0B";
            string prefix = bytes < 0? "-" : "";
            long absBytes = Math.Abs(bytes);
            if (absBytes < 1024)
                return $"{prefix}{absBytes}B";
            long gb = absBytes / (1024 * 1024 * 1024);
            long rem = absBytes % (1024 * 1024 * 1024);
            long mb = rem / (1024 * 1024);
            rem = rem % (1024 * 1024);
            long kb = rem / 1024;
            var sb = new StringBuilder();
            sb.Append(prefix);
            if (gb > 0)
                sb.Append($"{gb}GB ");
            if (mb > 0)
                sb.Append($"{mb}MB ");
            if (kb > 0)
                sb.Append($"{kb}KB");
            return sb.ToString().Trim();
        }

        private async void ShowLeaderboard() {
            if (string.IsNullOrEmpty(_settings.LeaderboardPath)) {
                string input = ShowInputBox("设置", "请输入共享文件路径:", _settings.LeaderboardPath);
                if (string.IsNullOrWhiteSpace(input))
                    return;
                _settings.LeaderboardPath = input;
                _settings.Save();
                LeaderboardService.SetPath(input);
            }

            var form = new Form {
                Text = "👑 卷王 & 摸鱼王 & 瘦身王 排行榜", Width = 1000, Height = 500, StartPosition = FormStartPosition.CenterParent
            };
            var table = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            var listCount = new ListView {
                Dock = DockStyle.Fill, View = View.Details, GridLines = true, FullRowSelect = true
            };
            listCount.Columns.Add("排名", 40);
            listCount.Columns.Add("用户", 180);
            listCount.Columns.Add("次数", 60);
            var listDuration = new ListView {
                Dock = DockStyle.Fill, View = View.Details, GridLines = true, FullRowSelect = true
            };
            listDuration.Columns.Add("排名", 40);
            listDuration.Columns.Add("用户", 180);
            listDuration.Columns.Add("时长", 80);
            var listSpace = new ListView {
                Dock = DockStyle.Fill, View = View.Details, GridLines = true, FullRowSelect = true
            };
            listSpace.Columns.Add("排名", 40);
            listSpace.Columns.Add("用户", 180);
            listSpace.Columns.Add("瘦身", 100);
            table.Controls.Add(listCount, 0, 0);
            table.Controls.Add(listDuration, 1, 0);
            table.Controls.Add(listSpace, 2, 0);
            var lblMy = new Label {
                Dock = DockStyle.Bottom,
                Height = 40,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(DefaultFont, FontStyle.Bold),
                Text = "正在加载数据..."
            };
            form.Controls.Add(table);
            form.Controls.Add(lblMy);
            form.Shown += async (_, __) => {
                var data = await LeaderboardService.GetLeaderboardAsync();
                var sortedCount = data.OrderByDescending(x => x.TotalSwitches).ToList();
                for (int i = 0; i < sortedCount.Count; i++) {
                    var u = sortedCount[i];
                    string name = u.Name;
                    if (i == 0)
                        name = $"🥇 {u.Name} (🌭切线王)";
                    else if (i == 1)
                        name = $"🥈 {u.Name}";
                    else if (i == 2)
                        name = $"🥉 {u.Name}";
                    listCount.Items.Add(new ListViewItem(new[] {
                        (i + 1).ToString(), name, u.TotalSwitches.ToString()
                    }));
                }

                var sortedTime = data.OrderByDescending(x => x.TotalDuration).ToList();
                for (int i = 0; i < sortedTime.Count; i++) {
                    var u = sortedTime[i];
                    string name = u.Name;
                    if (i == 0)
                        name = $"👑 {u.Name} (🐟摸鱼王)";
                    else if (i == 1)
                        name = $"🥈 {u.Name}";
                    else if (i == 2)
                        name = $"🥉 {u.Name}";
                    listDuration.Items.Add(new ListViewItem(new[] {
                        (i + 1).ToString(), name, FormatDuration(u.TotalDuration)
                    }));
                }

                var sortedSpace = data.OrderByDescending(x => x.TotalSpaceCleaned).ToList();
                int rankSpace = 1;
                foreach (var u in sortedSpace) {
                    if (u.TotalSpaceCleaned <= 0)
                        continue;
                    string name = u.Name;
                    if (rankSpace == 1)
                        name = $"💪 {u.Name} (🥦瘦身王)";
                    else if (rankSpace == 2)
                        name = $"🥈 {u.Name}";
                    else if (rankSpace == 3)
                        name = $"🥉 {u.Name}";
                    listSpace.Items.Add(new ListViewItem(new[] {
                        rankSpace.ToString(), name, FormatSize(u.TotalSpaceCleaned)
                    }));
                    rankSpace++;
                }

                var me = data.FirstOrDefault(x => x.Name == Environment.UserName);
                if (me != null) {
                    lblMy.Text = $"我：切线{me.TotalSwitches}次 | 摸鱼{FormatDuration(me.TotalDuration)} | 瘦身{FormatSize(me.TotalSpaceCleaned)}";
                } else {
                    lblMy.Text = "暂无数据";
                }
            };
            form.ShowDialog(this);
        }

        private async void StartSuperSlimProcess() {
            if (MessageBox.Show("【一键瘦身】将执行深度 GC，非常耗时。\n建议下班挂机执行。是否继续？", "确认 (1/2)", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            if (MessageBox.Show("CPU 将会满载。\n真的要继续吗？", "确认 (2/2)", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            var selectedParents = ShowParentSelectionDialog();
            if (selectedParents.Count == 0)
                return;
            this.Enabled = false;
            long totalSavedBytes = 0;
            int totalRepos = 0;
            foreach (var parent in selectedParents) {
                var cache = _settings.RepositoryCache.FirstOrDefault(x => string.Equals(x.ParentPath, parent, StringComparison.OrdinalIgnoreCase));
                if (cache == null || cache.Children.Count == 0)
                    continue;
                Log($"=== 清理父节点: {Path.GetFileName(parent)} ===");
                foreach (var repoInfo in cache.Children) {
                    totalRepos++;
                    Log($" >>> [清理中] {repoInfo.Name} ...");
                    statusLabel.Text = $"正在瘦身: {repoInfo.Name}";
                    var (ok, log, sizeStr, saved) = await Task.Run(() => GitHelper.GarbageCollect(repoInfo.FullPath, true));
                    if (ok) {
                        totalSavedBytes += saved;
                        Log($"[成功] {repoInfo.Name}: 减小 {sizeStr}");
                    } else {
                        string errorSummary = "未知错误";
                        if (!string.IsNullOrWhiteSpace(log)) {
                            var lines = log.Split(new[] {
                                '\r', '\n'
                            }, StringSplitOptions.RemoveEmptyEntries);
                            var errorLine = lines.LastOrDefault(l => l.Contains("❌") || l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("fatal", StringComparison.OrdinalIgnoreCase));
                            errorSummary = errorLine ?? lines.LastOrDefault() ?? "无日志返回";
                        }

                        Log($"[失败] {repoInfo.Name}: {errorSummary}");
                    }
                }
            }

            this.Enabled = true;
            statusLabel.Text = "清理完成";
#if !BOSS_MODE && !PURE_MODE
            if (!string.IsNullOrEmpty(_settings.LeaderboardPath)) {
                var stats = await LeaderboardService.UploadMyScoreAsync(0, totalSavedBytes);
                UpdateStatsUi(stats.totalCount, stats.totalTime, stats.totalSpace);
            }
#endif
            MessageBox.Show($"🎉 清理完毕！\n节省空间: {FormatSize(totalSavedBytes)}", "完成");
        }

        private List<string> ShowParentSelectionDialog() {
            var form = new Form {
                Text = "选择要清理的目录",
                Width = 400,
                Height = 300,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false
            };
            var clb = new CheckedListBox {
                Top = 10,
                Left = 10,
                Width = 360,
                Height = 200,
                CheckOnClick = true
            };
            var btnOk = new Button {
                Text = "开始", Top = 220, Left = 150, DialogResult = DialogResult.OK
            };
            foreach (var p in _settings.ParentPaths)
                clb.Items.Add(p, true);
            form.Controls.Add(clb);
            form.Controls.Add(btnOk);
            form.AcceptButton = btnOk;
            if (form.ShowDialog() == DialogResult.OK) {
                var r = new List<string>();
                foreach (var i in clb.CheckedItems)
                    r.Add(i.ToString());
                return r;
            }

            return new List<string>();
        }

        private void Log(string s) => txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\r\n");
    }
}