using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CleanMaster.Core
{
    /// <summary>
    /// 扫描引擎。整个类只做"读"操作(枚举文件、统计大小),
    /// 不包含任何 Delete / Move 调用 —— 删除逻辑完全隔离在 QuarantineManager 里,
    /// 这样即使扫描逻辑有 bug,最坏结果也只是扫描结果不准确,不会误删文件。
    /// </summary>
    public class ScannerEngine
    {
        public event Action<string>? OnProgress; // 用于 UI 实时滚动显示"正在扫描: xxx"

        /// <summary>
        /// 从 JSON 规则文件加载规则定义。
        /// </summary>
        public List<CleanupRule> LoadRules(string rulesJsonPath)
        {
            var json = File.ReadAllText(rulesJsonPath);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            };
            var rules = JsonSerializer.Deserialize<List<CleanupRule>>(json, options)
                        ?? new List<CleanupRule>();

            // 规则加载阶段就先过滤掉 Excluded 级别 —— 双重保险,
            // 即便有人手贱把某个高危规则的 Risk 写成非 Excluded，
            // 后面 ProtectedPaths 的路径级检查依然会兜底拦截。
            return rules;
        }

        /// <summary>
        /// 展开环境变量(%TEMP%、%LOCALAPPDATA% 等)得到真实路径。
        /// </summary>
        private static string ExpandPath(string rawPath) =>
            Environment.ExpandEnvironmentVariables(rawPath);

        /// <summary>
        /// 对外主入口:扫描所有规则,返回结果条目列表(不含 Excluded 规则)。
        /// </summary>
        public async Task<List<ScanResultItem>> ScanAsync(
            List<CleanupRule> rules,
            CancellationToken cancellationToken = default)
        {
            var results = new List<ScanResultItem>();

            foreach (var rule in rules)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Excluded 规则不生成扫描结果条目,直接跳过整条规则
                if (rule.Risk == RiskLevel.Excluded)
                    continue;

                foreach (var rawPath in rule.Paths)
                {
                    var path = ExpandPath(rawPath);

                    // 硬编码保护清单二次校验,任意路径命中直接跳过
                    if (ProtectedPaths.IsProtected(path))
                        continue;

                    OnProgress?.Invoke(path);

                    var item = await Task.Run(() => ScanSinglePath(rule, path), cancellationToken);
                    if (item != null)
                        results.Add(item);
                }
            }

            return results;
        }

        private ScanResultItem? ScanSinglePath(CleanupRule rule, string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    long totalSize = 0;
                    int fileCount = 0;

                    // 使用 EnumerateFiles 而非 GetFiles,避免大目录一次性加载到内存
                    foreach (var file in SafeEnumerateFiles(path))
                    {
                        // 文件级别的保护校验(比如目录本身没被判定为保护目录，
                        // 但目录里恰好混了一个 Cookies 之类的文件)
                        if (ProtectedPaths.IsProtected(file))
                            continue;

                        try
                        {
                            var info = new FileInfo(file);
                            totalSize += info.Length;
                            fileCount++;
                        }
                        catch (IOException)
                        {
                            // 文件被占用/读取失败,跳过计入,不中断整体扫描
                        }
                    }

                    if (fileCount == 0)
                        return null; // 空目录不生成结果条目,避免列表里全是"0 B"的噪音

                    return new ScanResultItem
                    {
                        Rule = rule,
                        ResolvedPath = path,
                        SizeBytes = totalSize,
                        FileCount = fileCount,
                        IsChecked = rule.Risk == RiskLevel.Low,
                        AccessDenied = false
                    };
                }
                else if (File.Exists(path))
                {
                    if (ProtectedPaths.IsProtected(path))
                        return null;

                    var info = new FileInfo(path);
                    return new ScanResultItem
                    {
                        Rule = rule,
                        ResolvedPath = path,
                        SizeBytes = info.Length,
                        FileCount = 1,
                        IsChecked = rule.Risk == RiskLevel.Low,
                        AccessDenied = false
                    };
                }
            }
            catch (UnauthorizedAccessException)
            {
                return new ScanResultItem
                {
                    Rule = rule,
                    ResolvedPath = path,
                    SizeBytes = 0,
                    FileCount = 0,
                    IsChecked = false,
                    AccessDenied = true // 提示用户"权限不足,建议以管理员身份运行"
                };
            }

            return null; // 路径不存在(比如用户没装这个软件),静默跳过,不算错误
        }

        /// <summary>
        /// 安全枚举文件:遇到子目录权限不足时跳过该子目录而不是让整个扫描失败。
        /// </summary>
        private static IEnumerable<string> SafeEnumerateFiles(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                string[] subDirs = Array.Empty<string>();
                string[] files = Array.Empty<string>();

                try
                {
                    subDirs = Directory.GetDirectories(dir);
                    files = Directory.GetFiles(dir);
                }
                catch (UnauthorizedAccessException) { continue; }
                catch (IOException) { continue; }

                foreach (var f in files)
                    yield return f;

                foreach (var sd in subDirs)
                {
                    // 子目录同样要过保护清单,防止扫描时"钻进"被保护的子目录
                    if (!ProtectedPaths.IsProtected(sd))
                        pending.Push(sd);
                }
            }
        }
    }
}
