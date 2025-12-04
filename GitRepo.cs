namespace GitBranchSwitcher {
    // GitRepo 类定义 (确保字段完整)
    public class GitRepo {
        public string Name { get; set; }
        public string Path { get; set; }
        public string CurrentBranch { get; set; } = "—";
        public bool SwitchOk { get; set; }
        public string LastMessage { get; set; }

        // 同步状态缓存
        public int Incoming { get; set; } = 0;
        public int Outgoing { get; set; } = 0;
        public bool HasUpstream { get; set; } = true;
        public bool IsSyncChecked { get; set; } = false;
        
        // [新增] 本地是否有修改
        public bool IsDirty { get; set; } = false;

        public GitRepo(string name, string path) {
            Name = name;
            Path = path;
        }
    }

    // [新增] 文件变更结构
    public class FileChangeItem {
        public string Status { get; set; } = ""; // "M", "A", "??"
        public string FilePath { get; set; } = "";
    }
}