using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CleanMaster.Core
{
    /// <summary>
    /// "清理"分两阶段:
    ///   1) 将用户勾选的文件/目录移动到隐藏的隔离区(可逆)
    ///   2) 隔离区内容超过保留期(默认 24 小时)后才真正物理删除
    ///
    /// 这样即使用户勾选失误、或工具本身逻辑有 bug,在保留期内都还有机会
    /// 从"最近清理记录"里找回文件,而不是一步到位不可逆地 Delete。
    /// </summary>
    public class QuarantineManager
    {
        private readonly string _quarantineRoot;
        private readonly TimeSpan _retention;

        public QuarantineManager(TimeSpan? retention = null)
        {
            _quarantineRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CleanMaster", "Quarantine");

            Directory.CreateDirectory(_quarantineRoot);
            _retention = retention ?? TimeSpan.FromHours(24);
        }

        /// <summary>
        /// 将一批扫描结果条目移入隔离区。每次清理生成一个带时间戳的批次目录，
        /// 并写一份 manifest.json 记录原始路径,用于"撤销"或后续排查。
        /// </summary>
        public async Task<QuarantineBatch> MoveToQuarantineAsync(IEnumerable<ScanResultItem> items)
        {
            var batchId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var batchDir = Path.Combine(_quarantineRoot, batchId);
            Directory.CreateDirectory(batchDir);

            var manifestEntries = new List<QuarantineEntry>();
            int index = 0;

            foreach (var item in items)
            {
                // 双重保险:移动前再校验一次保护清单,任何理由都不应该越过这一层
                if (ProtectedPaths.IsProtected(item.ResolvedPath))
                    continue;

                if (!Directory.Exists(item.ResolvedPath) && !File.Exists(item.ResolvedPath))
                    continue; // 扫描和清理之间路径可能已经消失(比如用户手动清过了)

                index++;
                var targetName = $"item_{index:D4}";
                var targetPath = Path.Combine(batchDir, targetName);

                try
                {
                    await Task.Run(() =>
                    {
                        if (Directory.Exists(item.ResolvedPath))
                        {
                            if (item.Rule.DeleteDirectoryItself)
                            {
                                Directory.Move(item.ResolvedPath, targetPath);
                            }
                            else
                            {
                                // 只清空目录内容,保留目录结构本身(很多软件假设该目录一直存在,
                                // 比如浏览器的 Cache 目录,删掉目录本身可能导致软件下次写入报错)
                                Directory.CreateDirectory(targetPath);
                                MoveDirectoryContents(item.ResolvedPath, targetPath);
                            }
                        }
                        else if (File.Exists(item.ResolvedPath))
                        {
                            File.Move(item.ResolvedPath, targetPath);
                        }
                    });

                    manifestEntries.Add(new QuarantineEntry
                    {
                        OriginalPath = item.ResolvedPath,
                        QuarantinePath = targetPath,
                        Category = item.Rule.Category,
                        SizeBytes = item.SizeBytes,
                        MovedAt = DateTime.Now
                    });
                }
                catch (Exception)
                {
                    // 单个条目移动失败(占用/权限)不影响其他条目继续处理
                }
            }

            var manifestPath = Path.Combine(batchDir, "manifest.json");
            var json = System.Text.Json.JsonSerializer.Serialize(manifestEntries,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(manifestPath, json);

            return new QuarantineBatch
            {
                BatchId = batchId,
                BatchDirectory = batchDir,
                Entries = manifestEntries,
                ExpiresAt = DateTime.Now + _retention
            };
        }

        /// <summary>
        /// 撤销:把某个批次的文件从隔离区移回原路径。
        /// </summary>
        public void Restore(QuarantineBatch batch)
        {
            foreach (var entry in batch.Entries)
            {
                try
                {
                    var parent = Path.GetDirectoryName(entry.OriginalPath);
                    if (parent != null) Directory.CreateDirectory(parent);

                    if (Directory.Exists(entry.QuarantinePath))
                        Directory.Move(entry.QuarantinePath, entry.OriginalPath);
                    else if (File.Exists(entry.QuarantinePath))
                        File.Move(entry.QuarantinePath, entry.OriginalPath);
                }
                catch (Exception)
                {
                    // 恢复失败(比如原路径已被占用)记录但不中断其他条目恢复
                }
            }
        }

        /// <summary>
        /// 清空所有已超过保留期的批次(建议在程序启动时调用一次)。
        /// </summary>
        public void PurgeExpiredBatches()
        {
            if (!Directory.Exists(_quarantineRoot)) return;

            foreach (var batchDir in Directory.GetDirectories(_quarantineRoot))
            {
                var manifestPath = Path.Combine(batchDir, "manifest.json");
                if (!File.Exists(manifestPath)) continue;

                var dirInfo = new DirectoryInfo(batchDir);
                if (DateTime.Now - dirInfo.CreationTime > _retention)
                {
                    try { Directory.Delete(batchDir, recursive: true); }
                    catch (IOException) { /* 下次启动再试 */ }
                }
            }
        }

        /// <summary>
        /// 立即清空某个批次(用户主动点击"立即清空隔离区"时调用)。
        /// </summary>
        public void PurgeNow(QuarantineBatch batch)
        {
            if (Directory.Exists(batch.BatchDirectory))
                Directory.Delete(batch.BatchDirectory, recursive: true);
        }

        private static void MoveDirectoryContents(string sourceDir, string destDir)
        {
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var dest = Path.Combine(destDir, Path.GetFileName(file));
                File.Move(file, dest);
            }
            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var dest = Path.Combine(destDir, Path.GetFileName(dir));
                Directory.Move(dir, dest);
            }
        }
    }

    public class QuarantineEntry
    {
        public string OriginalPath { get; set; } = "";
        public string QuarantinePath { get; set; } = "";
        public string Category { get; set; } = "";
        public long SizeBytes { get; set; }
        public DateTime MovedAt { get; set; }
    }

    public class QuarantineBatch
    {
        public string BatchId { get; set; } = "";
        public string BatchDirectory { get; set; } = "";
        public List<QuarantineEntry> Entries { get; set; } = new();
        public DateTime ExpiresAt { get; set; }

        public long TotalSizeBytes => Entries.Sum(e => e.SizeBytes);
    }
}
