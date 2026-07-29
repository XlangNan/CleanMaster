using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CleanMaster.Core
{
    /// <summary>
    /// 启发式扫描:不依赖 Rules.json 里预先写死的路径,而是自动遍历
    /// %LOCALAPPDATA% 和 %APPDATA% 下每个软件的目录,按文件夹命名规律
    /// (cache / temp / logs 等)识别出"看起来像缓存"的子目录。
    ///
    /// 重要:这里发现的所有结果,在 ScannerEngine 里一律标记为 Medium 风险,
    /// 不允许默认勾选 —— 因为这是基于命名规律的"猜测",不是像 Rules.json
    /// 里那样经过人工验证的确定性判断,必须留给用户自己确认。
    /// </summary>
    public class HeuristicScanner
    {
        // 文件夹名字命中这些关键词(不区分大小写,允许部分包含)就判定为"疑似缓存"
        private static readonly string[] CacheFolderNamePatterns =
        {
            "cache", "caches", "cachestorage", "gpucache", "diskcache",
            "temp", "tmp", "logs", "log", "crashdumps", "cached_data",
            "webcache", "thumbnails", "downloadcache", "codecache"
        };

        // 整个软件目录直接跳过,不进入启发式扫描——
        // 主要是聊天/IM类软件,因为它们的"缓存"目录里经常混有用户实际数据
        // (聊天图片、语音、接收的文件),不能用文件夹名字简单判断。
        private static readonly string[] SkipEntireAppFolder =
        {
            "wechat", "tencent", "dingtalk", "wxwork", "aliim", "feishu",
            "weixin", "im", "tim"
        };

        // 单个"疑似缓存"目录最多往下钻的层级,避免扫描耗时过长
        private const int MaxSearchDepth = 4;

        /// <summary>
        /// 遍历 AppData 下所有软件目录,返回发现的疑似缓存文件夹列表。
        /// 每一项只做路径发现,不在这里计算大小(交给 ScannerEngine 统一处理,
        /// 保持"发现"和"统计"两个职责分开)。
        /// </summary>
        public List<DiscoveredCacheFolder> DiscoverCacheFolders()
        {
            var results = new List<DiscoveredCacheFolder>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            };

            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;

                foreach (var appDir in SafeGetDirectories(root))
                {
                    var appName = Path.GetFileName(appDir);

                    if (SkipEntireAppFolder.Any(s =>
                            appName.Contains(s, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    if (ProtectedPaths.IsProtected(appDir))
                        continue;

                    foreach (var found in FindCacheLikeFolders(appDir, appName, depth: 0))
                    {
                        // 同一个路径可能从 Local 和 Roaming 下各遍历到一次同名软件的情况,
                        // 用绝对路径去重,避免结果列表出现重复条目
                        if (seenPaths.Add(found.Path))
                            results.Add(found);
                    }
                }
            }

            return results;
        }

        private IEnumerable<DiscoveredCacheFolder> FindCacheLikeFolders(
            string dir, string appName, int depth)
        {
            if (depth > MaxSearchDepth) yield break;

            var subDirs = SafeGetDirectories(dir).ToArray();

            foreach (var sub in subDirs)
            {
                if (ProtectedPaths.IsProtected(sub))
                    continue;

                var name = Path.GetFileName(sub);
                bool looksLikeCache = CacheFolderNamePatterns.Any(p =>
                    name.Contains(p, StringComparison.OrdinalIgnoreCase));

                if (looksLikeCache)
                {
                    yield return new DiscoveredCacheFolder
                    {
                        AppName = appName,
                        Path = sub
                    };
                    // 命中缓存目录后不再往里钻,避免把缓存目录内部的子结构
                    // 又拆成好几条重复条目;整个目录作为一个统计单元即可
                    continue;
                }

                // 没命中的目录继续往下找,软件可能把 cache 目录嵌套得比较深
                foreach (var found in FindCacheLikeFolders(sub, appName, depth + 1))
                    yield return found;
            }
        }

        private static IEnumerable<string> SafeGetDirectories(string path)
        {
            try { return Directory.GetDirectories(path); }
            catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
            catch (IOException) { return Array.Empty<string>(); }
        }
    }

    public class DiscoveredCacheFolder
    {
        public string AppName { get; set; } = "";
        public string Path { get; set; } = "";
    }
}
