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
        /// 从磁盘上的 JSON 规则文件加载规则定义(仅用于本地开发调试,
        /// 单文件发布场景请用 LoadRulesFromEmbeddedResource)。
        /// </summary>
        public List<CleanupRule> LoadRules(string rulesJsonPath)
        {
            var json = File.ReadAllText(rulesJsonPath);
            return DeserializeRules(json);
        }

        /// <summary>
        /// 从嵌入式资源加载规则定义。这是单文件发布模式下的正确用法——
        /// Rules.json 已经在编译时打进了程序集内部,不依赖 exe 旁边是否
        /// 存在这个文件,避免出现"exe 能跑但配置文件丢了"的分发问题。
        /// </summary>
        public List<CleanupRule> LoadRulesFromEmbeddedResource()
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();

            // 嵌入资源的名称格式是 "{根命名空间}.{相对路径,用 . 分隔}"
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("Rules.json", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
                throw new InvalidOperationException(
                    "未找到嵌入的 Rules.json 资源,请确认 csproj 中已将其声明为 EmbeddedResource。");

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"无法读取嵌入资源: {resourceName}");
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();

            return DeserializeRules(json);
        }

        private static List<CleanupRule> DeserializeRules(string json)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            };
            return JsonSerializer.Deserialize<List<CleanupRule>>(json, options)
                   ?? new List<CleanupRule>();
        }

        /// <summary>
        /// 展开环境变量(%TEMP%、%LOCALAPPDATA% 等)得到真实路径。
        /// </summary>
        private static string ExpandPath(string rawPath) =>
            Environment.ExpandEnvironmentVariables(rawPath);

        /// <summary>
        /// 启发式扫描:自动发现 AppData 下疑似缓存的目录并统计大小。
        /// 所有结果统一标记为 Medium 风险 —— 这是基于文件夹命名规律的猜测,
        /// 不是像 Rules.json 里那样经过人工验证的确定性判断,
        /// 因此无论如何都不能默认勾选,必须留给用户逐项确认。
        /// </summary>
        public async Task<List<ScanResultItem>> ScanHeuristicAsync(
            CancellationToken cancellationToken = default)
        {
            var heuristic = new HeuristicScanner();
            var discovered = await Task.Run(() => heuristic.DiscoverCacheFolders(), cancellationToken);

            var results = new List<ScanResultItem>();

            foreach (var folder in discovered)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 每一个发现的目录都合成一条"虚拟规则",复用现有的
                // ScanSinglePath 统计逻辑(内部同样会做保护清单二次校验)
                var syntheticRule = new CleanupRule
                {
                    Id = $"auto_{folder.AppName}_{Path.GetFileName(folder.Path)}",
                    Category = "自动发现的软件缓存",
                    DisplayName = $"{folder.AppName} · {Path.GetFileName(folder.Path)}",
                    Paths = new List<string> { folder.Path },
                    Risk = RiskLevel.Medium,
                    Explanation = $"在 {folder.AppName} 的数据目录下自动识别到疑似缓存文件夹," +
                                  "通常可以安全清理,但未经过人工验证,建议清理前确认这是您认识的软件",
                    Reversible = false,
                    DeleteDirectoryItself = false
                };

                OnProgress?.Invoke(folder.Path);

                var item = await Task.Run(
                    () => ScanSinglePath(syntheticRule, folder.Path), cancellationToken);

                if (item != null)
                    results.Add(item);
            }

            return results;
        }

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
