using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CleanMaster.Core
{
    public class ScannerEngine
    {
        public event Action<string>? OnProgress;

        public List<CleanupRule> LoadRules(string rulesJsonPath)
        {
            var json = File.ReadAllText(rulesJsonPath);
            return DeserializeRules(json);
        }

        public List<CleanupRule> LoadRulesFromEmbeddedResource()
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
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

        private static string ExpandPath(string rawPath) =>
            Environment.ExpandEnvironmentVariables(rawPath);

        public async Task<List<ScanResultItem>> ScanAsync(
            List<CleanupRule> rules,
            CancellationToken cancellationToken = default)
        {
            var results = new List<ScanResultItem>();

            foreach (var rule in rules)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (rule.Risk == RiskLevel.Excluded)
                    continue;

                foreach (var rawPath in rule.Paths)
                {
                    var path = ExpandPath(rawPath);

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

                    foreach (var file in SafeEnumerateFiles(path))
                    {
                        if (ProtectedPaths.IsProtected(file))
                            continue;

                        try
                        {
                            var info = new FileInfo(file);
                            totalSize += info.Length;
                            fileCount++;
                        }
                        catch (IOException) { }
                    }

                    if (fileCount == 0)
                        return null;

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
                    AccessDenied = true
                };
            }

            return null;
        }

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
                    if (!ProtectedPaths.IsProtected(sd))
                        pending.Push(sd);
                }
            }
        }
    }
}
