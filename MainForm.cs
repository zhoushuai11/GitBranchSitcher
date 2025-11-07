using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GitBranchSwitcher
{
    public partial class MainForm : Form {
        // 顶部：父目录区
        private TableLayoutPanel tlTop;
        private CheckedListBox lbParents;
        private TextBox txtSearch;
        private Button btnAddParent;
        private Button btnRemoveParent;
        private Button btnSelectAllParents;
        private Button btnClearParents;
        private Label lblHintParents;

        // 中部：上（左仓库/右操作） 下（日志）
        private SplitContainer splitMain;
        private SplitContainer splitUpper;
        private ListView lvRepos;
        private FlowLayoutPanel repoToolbar;
        private Panel panelLeft;
        private Panel pnlRight;

        // 右侧操作
        private Label lblTargetBranch;
        private ComboBox cmbTargetBranch;
        private Button btnSwitchAll;
        private Button btnUseCurrentBranch;
        private CheckBox chkStashOnSwitch;
        private Button btnConfigImages; // 配置目录

        // 状态图（按钮下方）：常驻 + 成功闪图
        private FlowLayoutPanel statePanel;
        private PictureBox pbState; // 常驻状态图
        private Label lblStateText;
        private PictureBox pbFlash; // 成功闪图
        private System.Windows.Forms.Timer flashTimer;

        // 底部日志 + 状态条
        private TextBox txtLog;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel statusLabel;
        private ToolStripProgressBar statusProgress;

        // 数据
        private readonly BindingList<GitRepo> _repos = new BindingList<GitRepo>();
        private List<string> _allBranches = new List<string>();
        private AppSettings _settings;

        // 并发控制
        private System.Threading.CancellationTokenSource? _loadCts;
        private int _loadSeq = 0;
        private HashSet<string> _checkedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private const int TARGET_BOX = 500; // 显示盒子大小
        private const int FLASH_BOX = 300;

        private enum SwitchState {
            NotStarted,
            Switching,
            Done
        }

        public MainForm() {
            _settings = AppSettings.Load();
            TrySetRuntimeIcon(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "AppIcon.jpg"));
            InitializeComponent();
            InitUi();
            LoadStateImagesRandom(); // 默认进入随机一张“未切线”
            SetSwitchState(SwitchState.NotStarted);
            SeedParentsToUi();
        }

        private void InitializeComponent() {
            Text = "很牛逼的切线工具";
            Width = 1400;
            Height = 900;
            StartPosition = FormStartPosition.CenterScreen;
        }

        private void InitUi() {
            // ===== 顶部：父目录 + 工具 =====
            tlTop = new TableLayoutPanel {
                Dock = DockStyle.Top, Height = 120, ColumnCount = 6, Padding = new Padding(8)
            };
            tlTop.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tlTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tlTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tlTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tlTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tlTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tlTop.RowCount = 2;
            tlTop.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tlTop.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            lbParents = new CheckedListBox {
                Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false
            };

            btnAddParent = new Button {
                Text = "添加父目录…"
            };
            btnRemoveParent = new Button {
                Text = "移除选中"
            };

            var lblSearch = new Label {
                Text = "过滤：", AutoSize = true, Anchor = AnchorStyles.Left
            };
            txtSearch = new TextBox {
                Width = 220, Anchor = AnchorStyles.Left
            };

            var parentOps = new FlowLayoutPanel {
                FlowDirection = FlowDirection.TopDown, AutoSize = true
            };
            btnSelectAllParents = new Button {
                Text = "全选父目录", AutoSize = true
            };
            btnClearParents = new Button {
                Text = "全不选父目录", AutoSize = true
            };
            parentOps.Controls.Add(btnSelectAllParents);
            parentOps.Controls.Add(btnClearParents);

            lblHintParents = new Label {
                Text = "提示：勾选要使用的父目录；支持过滤；Delete 可删除；右键可添加/移除。", AutoSize = true, ForeColor = SystemColors.GrayText
            };

            tlTop.Controls.Add(lbParents, 0, 0);
            tlTop.Controls.Add(btnAddParent, 1, 0);
            tlTop.Controls.Add(btnRemoveParent, 2, 0);
            tlTop.Controls.Add(lblSearch, 3, 0);
            tlTop.Controls.Add(txtSearch, 4, 0);
            tlTop.Controls.Add(parentOps, 5, 0);
            tlTop.Controls.Add(lblHintParents, 0, 1);
            tlTop.SetColumnSpan(lblHintParents, 6);

            var cm = new ContextMenuStrip();
            cm.Items.Add("添加父目录…", null, (_, __) => btnAddParent.PerformClick());
            cm.Items.Add("移除选中", null, (_, __) => btnRemoveParent.PerformClick());
            lbParents.ContextMenuStrip = cm;

            btnAddParent.Click += (_, __) => {
                using var fbd = new FolderBrowserDialog();
                if (fbd.ShowDialog(this) == DialogResult.OK) {
                    var path = fbd.SelectedPath.Trim();
                    if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                        return;
                    if (!_settings.ParentPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) {
                        _settings.ParentPaths.Add(path);
                        _settings.Save();
                    }

                    RefilterParentsList();
                }
            };

            btnRemoveParent.Click += async (_, __) => {
                var selected = lbParents.SelectedItems.Cast<object>().Select(o => o?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s));
                var checkedOnes = lbParents.CheckedItems.Cast<object>().Select(o => o?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s));
                var toRemove = new HashSet<string>(selected.Concat(checkedOnes), StringComparer.OrdinalIgnoreCase);
                if (toRemove.Count == 0)
                    return;
                foreach (var p in toRemove) {
                    _settings.ParentPaths.RemoveAll(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));
                    _checkedParents.Remove(p);
                }

                _settings.Save();
                RefilterParentsList();
                await LoadReposForCheckedParentsAsync();
            };

            txtSearch.TextChanged += (_, __) => RefilterParentsList();

            lbParents.ItemCheck += async (_, e) => {
                var path = lbParents.Items[e.Index]?.ToString() ?? "";
                if (string.IsNullOrEmpty(path))
                    return;
                if (e.NewValue == CheckState.Checked)
                    _checkedParents.Add(path);
                else
                    _checkedParents.Remove(path);
                await LoadReposForCheckedParentsAsync();
            };

            btnSelectAllParents.Click += async (_, __) => {
                _checkedParents = new HashSet<string>(_settings.ParentPaths, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < lbParents.Items.Count; i++)
                    lbParents.SetItemChecked(i, true);
                await LoadReposForCheckedParentsAsync();
            };
            btnClearParents.Click += async (_, __) => {
                _checkedParents.Clear();
                for (int i = 0; i < lbParents.Items.Count; i++)
                    lbParents.SetItemChecked(i, false);
                await LoadReposForCheckedParentsAsync();
            };

            lbParents.KeyDown += async (_, e) => {
                if (e.KeyCode != Keys.Delete)
                    return;
                var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var it in lbParents.SelectedItems)
                    targets.Add(it?.ToString() ?? "");
                foreach (var it in lbParents.CheckedItems)
                    targets.Add(it?.ToString() ?? "");
                targets.RemoveWhere(string.IsNullOrEmpty);
                if (targets.Count == 0)
                    return;
                foreach (var p in targets) {
                    _settings.ParentPaths.RemoveAll(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));
                    _checkedParents.Remove(p);
                }

                _settings.Save();
                RefilterParentsList();
                await LoadReposForCheckedParentsAsync();
            };

            // ===== 中部：列表/操作 =====
            splitMain = new SplitContainer {
                Dock = DockStyle.Fill, Orientation = Orientation.Horizontal
            };

            splitUpper = new SplitContainer {
                Dock = DockStyle.Fill, Orientation = Orientation.Vertical
            };

            Shown += (_, __) => {
                splitMain.SplitterDistance = (int)(ClientSize.Height * 0.58);
                splitUpper.SplitterDistance = (int)(ClientSize.Width * 0.52);
            };
            Resize += (_, __) => {
                var minRight = Math.Max(600, TARGET_BOX + 120);
                splitUpper.SplitterDistance = Math.Max(400, ClientSize.Width - minRight);
            };

            lvRepos = new ListView {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false,
                CheckBoxes = true
            };
            lvRepos.Columns.Add("仓库名", 260);
            lvRepos.Columns.Add("路径", 560);
            lvRepos.Columns.Add("当前分支", 200);
            lvRepos.Columns.Add("结果", 120);

            repoToolbar = new FlowLayoutPanel {
                Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(6, 6, 6, 6)
            };
            var btnRepoCancel = new Button {
                Text = "取消"
            };
            var btnRepoSelectAll = new Button {
                Text = "全选仓库"
            };
            var btnRepoSelectNone = new Button {
                Text = "全不选"
            };
            repoToolbar.Controls.Add(btnRepoCancel);
            repoToolbar.Controls.Add(btnRepoSelectAll);
            repoToolbar.Controls.Add(btnRepoSelectNone);

            btnRepoCancel.Click += (_, __) => {
                foreach (ListViewItem it in lvRepos.Items)
                    it.Checked = false;
            };
            btnRepoSelectAll.Click += (_, __) => {
                foreach (ListViewItem it in lvRepos.Items)
                    it.Checked = true;
            };
            btnRepoSelectNone.Click += (_, __) => {
                foreach (ListViewItem it in lvRepos.Items)
                    it.Checked = false;
            };

            panelLeft = new Panel {
                Dock = DockStyle.Fill
            };
            panelLeft.Controls.Add(lvRepos);
            panelLeft.Controls.Add(repoToolbar);

            // 右侧：操作区
            pnlRight = new Panel {
                Dock = DockStyle.Fill, Padding = new Padding(10)
            };
            var rightLayout = new TableLayoutPanel {
                Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true
            };
            rightLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rightLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rightLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var hintLabel = new Label {
                Text = "勾选父目录后自动扫描其固定子目录（含根目录）；左侧仓库列表勾选要切换的仓库；输入目标分支支持子串/多关键字匹配。", AutoSize = true, MaximumSize = new Size(700, 0)
            };
            rightLayout.Controls.Add(hintLabel, 0, 0);
            rightLayout.SetColumnSpan(hintLabel, 2);

            lblTargetBranch = new Label {
                Text = "目标分支：", AutoSize = true
            };
            cmbTargetBranch = new ComboBox {
                Width = 420,
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.None,
                AutoCompleteSource = AutoCompleteSource.None,
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
            };

            btnUseCurrentBranch = new Button {
                Text = "使用当前分支", AutoSize = true
            };
            btnUseCurrentBranch.Click += (_, __) => {
                var any = lvRepos.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Checked);
                if (any == null) {
                    MessageBox.Show("请先勾选一个仓库");
                    return;
                }

                var repo = (GitRepo)any.Tag;
                var cur = GitHelper.GetFriendlyBranch(repo.Path) ?? repo.CurrentBranch;
                if (!string.IsNullOrWhiteSpace(cur))
                    cmbTargetBranch.Text = cur!;
                else
                    MessageBox.Show("无法读取当前分支");
            };
            cmbTargetBranch.TextUpdate += (_, __) => UpdateBranchDropdown();


            chkStashOnSwitch = new CheckBox {
                Text = "切线时贮存改动（stash）", AutoSize = true, Checked = _settings.StashOnSwitch
            };
            chkStashOnSwitch.CheckedChanged += (_, __) => {
                _settings.StashOnSwitch = chkStashOnSwitch.Checked;
                _settings.Save();
            };
            btnSwitchAll = new Button {
                Text = "一键切线并 Pull", Width = 260, Height = 36, Anchor = AnchorStyles.Left | AnchorStyles.Top
            };
            btnSwitchAll.Click += async (_, __) => await SwitchAllAsync();

            btnConfigImages = new Button {
                Text = "状态图目录…", AutoSize = true
            };
            btnConfigImages.Click += (_, __) => ConfigureImageDirs();

            // 状态区
            statePanel = new FlowLayoutPanel {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(4),
                Margin = new Padding(0, 8, 0, 0),
                WrapContents = true
            };
            pbState = new PictureBox {
                Width = TARGET_BOX,
                Height = TARGET_BOX,
                SizeMode = PictureBoxSizeMode.CenterImage, // 小图不放大
                Margin = new Padding(0, 0, 12, 0)
            };

            lblStateText = new Label {
                AutoSize = true, Text = "未切线", Margin = new Padding(0, 10, 0, 0)
            };
            lblStateText.Font = new Font(lblStateText.Font, FontStyle.Bold);

            pbFlash = new PictureBox {
                Width = FLASH_BOX,
                Height = FLASH_BOX,
                SizeMode = PictureBoxSizeMode.CenterImage, // 小图不放大
                Visible = false,
                Margin = new Padding(30, 0, 0, 0)
            };
            flashTimer = new System.Windows.Forms.Timer {
                Interval = 800
            };
            flashTimer.Tick += (_, __) => {
                pbFlash.Visible = false;
                flashTimer.Stop();
            };

            statePanel.Controls.Add(pbState);
            statePanel.Controls.Add(lblStateText);
            statePanel.Controls.Add(pbFlash);

            rightLayout.Controls.Add(lblTargetBranch, 0, 1);
            rightLayout.Controls.Add(cmbTargetBranch, 1, 1);
            rightLayout.Controls.Add(btnUseCurrentBranch, 2, 1);
            rightLayout.Controls.Add(btnSwitchAll, 0, 2);
            rightLayout.Controls.Add(btnConfigImages, 1, 2);
            rightLayout.Controls.Add(chkStashOnSwitch, 0, 3);
            rightLayout.SetColumnSpan(chkStashOnSwitch, 3);
            rightLayout.Controls.Add(statePanel, 0, 4);
            rightLayout.SetColumnSpan(statePanel, 2);

            pnlRight.Controls.Add(rightLayout);

            splitUpper.Panel1.Controls.Add(panelLeft);
            splitUpper.Panel2.Controls.Add(pnlRight);
            splitMain.Panel1.Controls.Add(splitUpper);

            // 底部日志
            txtLog = new TextBox {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                ReadOnly = true,
                Font = new Font("Consolas", 9)
            };
            splitMain.Panel2.Controls.Add(txtLog);

            // 底部状态条
            statusStrip = new StatusStrip();
            statusLabel = new ToolStripStatusLabel("就绪");
            statusProgress = new ToolStripProgressBar {
                Visible = false, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Width = 120
            };
            statusStrip.Items.Add(statusLabel);
            statusStrip.Items.Add(new ToolStripStatusLabel {
                Spring = true
            });
            statusStrip.Items.Add(statusProgress);

            Controls.Add(splitMain);
            Controls.Add(tlTop);
            Controls.Add(statusStrip);
        }

        private void SeedParentsToUi() {
            if (lbParents == null || lbParents.IsDisposed)
                return;

            // 兜底
            _settings ??= new AppSettings();
            _settings.ParentPaths ??= new List<string>();
            _checkedParents ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            lbParents.BeginUpdate();
            try {
                lbParents.Items.Clear();
                // 做个快照，避免并发修改引发异常
                var parents = _settings.ParentPaths?.ToList() ?? new List<string>();
                foreach (var p in parents) {
                    if (string.IsNullOrWhiteSpace(p))
                        continue;
                    int idx = lbParents.Items.Add(p);
                    if (_checkedParents.Contains(p))
                        lbParents.SetItemChecked(idx, true);
                }
            } finally {
                lbParents.EndUpdate();
            }
        }

        private void RefilterParentsList() {
            if (lbParents == null || lbParents.IsDisposed)
                return;

            var kw = txtSearch?.Text?.Trim() ?? "";
            _settings ??= new AppSettings();
            _settings.ParentPaths ??= new List<string>();
            _checkedParents ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var src = _settings.ParentPaths.ToList(); // 快照

            lbParents.BeginUpdate();
            try {
                lbParents.Items.Clear();
                foreach (var p in src) {
                    if (string.IsNullOrWhiteSpace(p))
                        continue;
                    if (string.IsNullOrEmpty(kw) || p.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) {
                        int idx = lbParents.Items.Add(p);
                        if (_checkedParents.Contains(p))
                            lbParents.SetItemChecked(idx, true);
                    }
                }
            } finally {
                lbParents.EndUpdate();
            }
        }

        // ===============  图片逻辑（多目录随机 + 500 规则）  ===============
        private static readonly string[] _exts = new[] {
            ".png", ".jpg", ".jpeg", ".gif"
        };

        private static string? PickRandomImage(string dir) {
            try {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    return null;
                var files = Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly).Where(f => _exts.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
                if (files.Count == 0)
                    return null;
                return files[new Random().Next(files.Count)];
            } catch {
                return null;
            }
        }

        private static Image? LoadImageKeepOrDownscale(string file, out bool isLarge, out Size sz) {
            isLarge = false;
            sz = Size.Empty;
            try {
                var bytes = File.ReadAllBytes(file);
                using (var probe = Image.FromStream(new MemoryStream(bytes), true, true)) {
                    sz = probe.Size;
                }

                isLarge = !(sz.Width <= 500 && sz.Height <= 500);
                // 直接从内存流构造，GIF 能保持动画
                return Image.FromStream(new MemoryStream(bytes), true, true);
            } catch {
                return null;
            }
        }

        private void ApplyImageTo(PictureBox pb, string dirKey, int box) {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string defaultDir = Path.Combine(baseDir, "Assets", dirKey);

            string targetDir = defaultDir;
            if (dirKey == "state_notstarted" && !string.IsNullOrEmpty(_settings.DirNotStarted))
                targetDir = _settings.DirNotStarted;
            if (dirKey == "state_switching" && !string.IsNullOrEmpty(_settings.DirSwitching))
                targetDir = _settings.DirSwitching;
            if (dirKey == "state_done" && !string.IsNullOrEmpty(_settings.DirDone))
                targetDir = _settings.DirDone;
            if (dirKey == "flash_success" && !string.IsNullOrEmpty(_settings.DirFlash))
                targetDir = _settings.DirFlash;

            string? path = PickRandomImage(targetDir) ?? PickRandomImage(defaultDir);
            if (path == null) {
                pb.Image = null;
                return;
            }

            var img = LoadImageKeepOrDownscale(path, out bool isLarge, out _);
            if (img == null) {
                pb.Image = null;
                return;
            }

            pb.SizeMode = isLarge? PictureBoxSizeMode.Zoom : PictureBoxSizeMode.CenterImage;
            pb.Image = img;
        }

        private void LoadStateImagesRandom() {
            ApplyImageTo(pbState, "state_notstarted", TARGET_BOX);
            ApplyImageTo(pbFlash, "flash_success", FLASH_BOX);
        }

        private void SetSwitchState(SwitchState st) {
            switch (st) {
                case SwitchState.NotStarted:
                    ApplyImageTo(pbState, "state_notstarted", TARGET_BOX);
                    lblStateText.Text = "未切线";
                    break;
                case SwitchState.Switching:
                    ApplyImageTo(pbState, "state_switching", TARGET_BOX);
                    lblStateText.Text = "切线中";
                    break;
                case SwitchState.Done:
                    ApplyImageTo(pbState, "state_done", TARGET_BOX);
                    lblStateText.Text = "切完了";
                    break;
            }
        }

        private void ConfigureImageDirs() {
            using var fbd = new FolderBrowserDialog();
            if (MessageBox.Show(this, "选择“未切线”图片目录？（可多图随机）", "状态图目录", MessageBoxButtons.OKCancel) == DialogResult.OK) {
                if (fbd.ShowDialog(this) == DialogResult.OK)
                    _settings.DirNotStarted = fbd.SelectedPath;
            }

            if (MessageBox.Show(this, "选择“切线中”图片目录？（可多图随机）", "状态图目录", MessageBoxButtons.OKCancel) == DialogResult.OK) {
                if (fbd.ShowDialog(this) == DialogResult.OK)
                    _settings.DirSwitching = fbd.SelectedPath;
            }

            if (MessageBox.Show(this, "选择“切完了”图片目录？（可多图随机）", "状态图目录", MessageBoxButtons.OKCancel) == DialogResult.OK) {
                if (fbd.ShowDialog(this) == DialogResult.OK)
                    _settings.DirDone = fbd.SelectedPath;
            }

            if (MessageBox.Show(this, "选择“成功闪图”目录？（可多图随机）", "状态图目录", MessageBoxButtons.OKCancel) == DialogResult.OK) {
                if (fbd.ShowDialog(this) == DialogResult.OK)
                    _settings.DirFlash = fbd.SelectedPath;
            }

            _settings.Save();
            SetSwitchState(SwitchState.NotStarted);
        }

        // ================== 仓库扫描 / 分支 / 切线逻辑 ==================
        private async Task LoadReposForCheckedParentsAsync() {
            _loadCts?.Cancel();
            _loadCts = new System.Threading.CancellationTokenSource();
            var token = _loadCts.Token;
            var mySeq = System.Threading.Interlocked.Increment(ref _loadSeq);

            lvRepos.Items.Clear();
            _repos.Clear();
            _allBranches.Clear();
            cmbTargetBranch.Items.Clear();

            var checkedParents = _checkedParents.Where(Directory.Exists).ToList();
            if (checkedParents.Count == 0) {
                Log("ℹ️ 未勾选父目录。");
                statusLabel.Text = "就绪";
                statusProgress.Visible = false;
                SetSwitchState(SwitchState.NotStarted);
                return;
            }

            statusLabel.Text = "正在扫描仓库…";
            statusProgress.Visible = true;
            SetSwitchState(SwitchState.NotStarted);

            var targets = GetTargetSubdirsFromParents(checkedParents).OrderBy(t => t.parent, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.name, StringComparer.OrdinalIgnoreCase).ToList();

            var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, path, parent) in targets) {
                if (token.IsCancellationRequested)
                    return;
                if (!Directory.Exists(path)) {
                    Log($"⚠️ 跳过：{name} -> 路径不存在：{path}");
                    continue;
                }

                var repoRoot = GitHelper.FindGitRoot(path);
                if (repoRoot == null) {
                    Log($"⚠️ 跳过：{name} -> 非 git 仓库：{path}");
                    continue;
                }

                if (!seenRoots.Add(repoRoot))
                    continue;

                var repo = new GitRepo(name, repoRoot);
                _repos.Add(repo);

                var displayName = $"[{Path.GetFileName(parent)}] {name}";
                var lvi = new ListViewItem(new[] {
                    displayName, repoRoot, "⏳", "—"
                }) {
                    Tag = repo, Checked = true
                };
                lvRepos.Items.Add(lvi);
            }

            var tasks = new List<Task>();
            foreach (ListViewItem item in lvRepos.Items) {
                var repo = (GitRepo)item.Tag;
                tasks.Add(Task.Run(() => {
                    if (token.IsCancellationRequested)
                        return;
                    var branch = GitHelper.GetFriendlyBranch(repo.Path);
                    repo.CurrentBranch = branch;
                }, token));
            }

            try {
                await Task.WhenAll(tasks);
            } catch (OperationCanceledException) {
                return;
            }

            if (token.IsCancellationRequested || mySeq != _loadSeq)
                return;

            foreach (ListViewItem item in lvRepos.Items) {
                var repo = (GitRepo)item.Tag;
                item.SubItems[2].Text = repo.CurrentBranch ?? "—";
            }

            Log($"✅ 加载完成，共 {lvRepos.Items.Count} 个仓库。");
            statusLabel.Text = "加载完成";
            statusProgress.Visible = false;

            await RefreshBranchesAsync();
        }

        private static IEnumerable<(string name, string path, string parent)> GetTargetSubdirsFromParents(IEnumerable<string> parents) {
            var list = new List<(string, string, string)>();
            foreach (var parent in parents) {
                if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                    continue;

                list.Add(("client_root", parent, parent));

                var bundle_path = Path.Combine(parent, "Assets", "ToBundle");
                var script_path = Path.Combine(parent, "Assets", "Script");
                var gold_dash_path = Path.Combine(script_path, "GoldDash");
                var script_biubiubiu2_path = Path.Combine(parent, "Assets", "Script", "Biubiubiu2");
                var art_path = Path.Combine(parent, "Assets", "Art");
                var scene_path = Path.Combine(parent, "Assets", "Scenes");
                var csv_path = Path.Combine(parent, "Library", "ConfigCache");
                var audio_path = Path.Combine(parent, "Assets", "Audio");

                list.Add(("bundle_path", bundle_path, parent));
                list.Add(("script_path", script_path, parent));
                list.Add(("gold_dash_path", gold_dash_path, parent));
                list.Add(("script_biubiubiu2_path", script_biubiubiu2_path, parent));
                list.Add(("art_path", art_path, parent));
                list.Add(("scene_path", scene_path, parent));
                list.Add(("csv_path", csv_path, parent));
                list.Add(("audio_path", audio_path, parent));
            }

            return list;
        }

        private async Task RefreshBranchesAsync() {
            if (lvRepos.Items.Count == 0) {
                Log("❌ 无仓库可读取分支。");
                return;
            }

            statusLabel.Text = "正在读取分支…";
            statusProgress.Visible = true;

            var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tasks = new List<Task<IEnumerable<string>>>();

            foreach (ListViewItem item in lvRepos.Items) {
                var repo = (GitRepo)item.Tag;
                tasks.Add(Task.Run(() => GitHelper.GetAllBranches(repo.Path)));
            }

            var results = await Task.WhenAll(tasks);
            foreach (var list in results)
                foreach (var b in list)
                    all.Add(b);

            all.RemoveWhere(n => string.Equals(n, "origin", StringComparison.OrdinalIgnoreCase));

            _allBranches = all.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            SeedBranchItems(_allBranches);

            Log($"✅ 分支读取完成，去重后共 {_allBranches.Count} 个。");
            statusLabel.Text = "就绪";
            statusProgress.Visible = false;
        }

        private void SeedBranchItems(List<string> items) {
            cmbTargetBranch.BeginUpdate();
            try {
                cmbTargetBranch.Items.Clear();
                foreach (var it in items)
                    cmbTargetBranch.Items.Add(it);
            } finally {
                cmbTargetBranch.EndUpdate();
            }
        }

        private void UpdateBranchDropdown() {
            var text = cmbTargetBranch.Text ?? string.Empty;
            var tokens = text.Split(new[] {
                ' ', '/', '-', '_'
            }, StringSplitOptions.RemoveEmptyEntries);

            IEnumerable<string> filtered;
            if (tokens.Length == 0) {
                filtered = _allBranches;
            } else {
                filtered = _allBranches.Where(b => {
                    foreach (var t in tokens) {
                        if (b?.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0)
                            return false;
                    }

                    return true;
                });
            }

            var list = filtered.OrderByDescending(b => tokens.Length > 0 && b.StartsWith(tokens[0], StringComparison.OrdinalIgnoreCase)).ThenBy(b => b, StringComparer.OrdinalIgnoreCase).Take(800).ToList();

            var selStart = cmbTargetBranch.SelectionStart;
            var selLen = cmbTargetBranch.SelectionLength;

            cmbTargetBranch.BeginUpdate();
            try {
                cmbTargetBranch.Items.Clear();
                foreach (var it in list)
                    cmbTargetBranch.Items.Add(it);
            } finally {
                cmbTargetBranch.EndUpdate();
            }

            cmbTargetBranch.DroppedDown = true;
            cmbTargetBranch.IntegralHeight = true;
            cmbTargetBranch.SelectionStart = Math.Min(selStart, cmbTargetBranch.Text.Length);
            cmbTargetBranch.SelectionLength = selLen;
            Cursor.Current = Cursors.Default;
        }

        private async Task SwitchAllAsync() {
            var target = cmbTargetBranch.Text.Trim();
            if (string.IsNullOrEmpty(target)) {
                Log("❌ 请输入要切换的分支名。");
                return;
            }

            var candidates = lvRepos.Items.Cast<ListViewItem>().Where(i => i.Checked).ToList();
            int total = candidates.Count;
            if (total == 0) {
                Log("ℹ️ 未勾选任何仓库，已取消本次操作。");
                return;
            }

            btnSwitchAll.Enabled = false;
            statusProgress.Visible = true;
            statusLabel.Text = "正在切线并拉取…";
            SetSwitchState(SwitchState.Switching);

            try {
                Log($">>> 开始一键切线：{target}（共 {total} 个仓库）");

                foreach (ListViewItem item in candidates) {
                    item.SubItems[3].Text = "⏳"; // 结果列
                    item.SubItems[2].Text = "⏳"; // 分支列（可视化反馈）
                }

                int done = 0;
                var tasks = new List<Task>();
                var sem = new System.Threading.SemaphoreSlim(Math.Max(1, _settings.MaxParallel));
                foreach (ListViewItem item in candidates) {
                    var lvi = item;
                    var repo = (GitRepo)lvi.Tag;
                    tasks.Add(Task.Run(async () => {
                        await sem.WaitAsync();
                        try {
                            var (ok, message) = GitHelper.SwitchAndPull(repo.Path, target, _settings.StashOnSwitch);
                            repo.SwitchOk = ok;
                            repo.LastMessage = message;
                            repo.CurrentBranch = GitHelper.GetFriendlyBranch(repo.Path) ?? repo.CurrentBranch;
                        } finally {
                            sem.Release();
                        }
                    }).ContinueWith(_ => {
                        BeginInvoke((Action)(() => {
                            var r = (GitRepo)lvi.Tag;
                            lvi.SubItems[2].Text = r.CurrentBranch ?? "—";
                            lvi.SubItems[3].Text = r.SwitchOk? "✅" : "❌";
                            Log($"[{r.Name}] {Shorten(r.LastMessage, 800)}");
                            lvRepos.Invalidate(lvi.Bounds);
                            done++;
                            statusLabel.Text = $"正在处理 {done}/{total}";

                            // 子节点成功时播放闪图（随机挑一张）
                            if (r.SwitchOk) {
                                ApplyImageTo(pbFlash, "flash_success", FLASH_BOX);
                                pbFlash.Visible = true;
                                pbFlash.BringToFront();
                                flashTimer.Stop();
                                flashTimer.Start();
                            }
                        }));
                    }));
                }

                await Task.WhenAll(tasks);
                Log("🏁 一键切线完成。详情见底部日志。");
                statusLabel.Text = "完成";
                SetSwitchState(SwitchState.Done);
            } finally {
                btnSwitchAll.Enabled = true;
                statusProgress.Visible = false;
            }
        }

        private static string Shorten(string? s, int max) {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Length <= max? s : s.Substring(0, max) + "...";
        }

        private void Log(string line) {
            if (txtLog.TextLength > 0)
                txtLog.AppendText(Environment.NewLine);
            txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}");
        }


        private static System.Drawing.Image MakePlaceholder(string text) {
            int w = 240, h = 160;
            var bmp = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) {
                g.Clear(System.Drawing.Color.DimGray);
                using var br = new System.Drawing.SolidBrush(System.Drawing.Color.White);
                using var f = new System.Drawing.Font("Segoe UI", 10, System.Drawing.FontStyle.Bold);
                var sf = new System.Drawing.StringFormat {
                    Alignment = System.Drawing.StringAlignment.Center, LineAlignment = System.Drawing.StringAlignment.Center
                };
                g.DrawString(text ?? "NO IMAGE", f, br, new System.Drawing.RectangleF(0, 0, w, h), sf);
            }

            return bmp;
        }

        private void TrySetRuntimeIcon(string jpgPath)
        {
            try
            {
                if (!System.IO.File.Exists(jpgPath)) return;
                using var bmp = new System.Drawing.Bitmap(jpgPath);
                IntPtr hIcon = bmp.GetHicon();
                this.Icon = System.Drawing.Icon.FromHandle(hIcon);
            }
            catch { /* ignore */ }
        }
    }

}