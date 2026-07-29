using System.Collections.Generic;

namespace CleanMaster.Core
{
    /// <summary>
    /// 风险等级。Excluded 级别的规则根本不会出现在扫描结果里(见 ScannerEngine),
    /// 而不是"出现但默认不勾选"——避免用户误操作勾选到高危项。
    /// </summary>
    public enum RiskLevel
    {
        Low,        // 可安全清理,默认勾选
        Medium,     // 建议用户逐项确认,默认不勾选
        Excluded    // 永不清理,不在候选列表中(仅用于"本工具不会触碰"展示)
    }

    /// <summary>
    /// 从 Rules.json 反序列化出的原始规则定义。
    /// 一条规则对应一类"垃圾"(如"Chrome 网页缓存"),可以包含多个候选路径
    /// (因为同一类缓存在不同 Windows 版本/用户名下路径可能不同)。
    /// </summary>
    public class CleanupRule
    {
        public string Id { get; set; } = "";
        public string Category { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public List<string> Paths { get; set; } = new();
        public RiskLevel Risk { get; set; } = RiskLevel.Medium;
        public string Explanation { get; set; } = "";
        public bool Reversible { get; set; } = false;

        /// <summary>
        /// 是否只清理目录下的"文件"而保留目录结构本身(多数缓存目录场景),
        /// 还是允许连目录一起删除(如整个临时安装包文件夹)。
        /// </summary>
        public bool DeleteDirectoryItself { get; set; } = false;
    }

    /// <summary>
    /// 扫描后生成的实际结果条目,绑定到 UI 上的每一行/每一个卡片。
    /// </summary>
    public class ScanResultItem
    {
        public CleanupRule Rule { get; set; } = null!;
        public string ResolvedPath { get; set; } = "";
        public long SizeBytes { get; set; }
        public int FileCount { get; set; }
        public bool IsChecked { get; set; }
        public bool AccessDenied { get; set; } // 权限不足导致无法扫描/清理

        public string SizeDisplay => FormatSize(SizeBytes);

        private static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unitIndex = 0;
            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }
            return $"{size:0.##} {units[unitIndex]}";
        }
    }
}
