using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiHost;

/// <summary>可独立构造的用户设置；测试无需访问真实用户配置。</summary>
public sealed class WorkspaceConfiguration
{
    private readonly string _settingsFile;
    private readonly bool _hasOverride;
    public string WorkspaceRoot { get; private set; }
    public string? StartupWarning { get; private set; }
    public string TemplateDirectory => ResolveTemplate(WorkspaceRoot);

    public WorkspaceConfiguration(string settingsDirectory, string? workspaceOverride = null)
    {
        _settingsFile = Path.Combine(Path.GetFullPath(settingsDirectory), "settings.json");
        _hasOverride = !string.IsNullOrWhiteSpace(workspaceOverride);
        WorkspaceRoot = Path.Combine(Path.GetFullPath(settingsDirectory), "Workspace");
        try
        {
            // 自动化覆盖不读取或修改真实用户设置。
            if (_hasOverride)
                WorkspaceRoot = NormalizeDirectory(workspaceOverride!);
            else if (AtomicJsonFile.ReadObject(_settingsFile)["WorkspaceRoot"] is { } configured)
                WorkspaceRoot = NormalizeDirectory(configured.GetValue<string>());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            StartupWarning = $"无法加载工作区设置 {_settingsFile}：{ex.Message}。请修复配置或重新选择工作区；原文件已保留。";
        }
    }

    public void ConfigureWorkspace(string path, bool persist = true)
    {
        var candidate = NormalizeDirectory(path);
        _ = InstanceStore.Scan(InstanceStore.Load(candidate), candidate);
        _ = ResolveTemplate(candidate);
        _ = new ShellSettingsStore(candidate).Load();
        var settings = persist && !_hasOverride ? AtomicJsonFile.ReadObject(_settingsFile) : null;
        Directory.CreateDirectory(candidate);
        if (settings != null)
        {
            settings["WorkspaceRoot"] = candidate;
            AtomicJsonFile.Write(_settingsFile, settings.ToJsonString(AtomicJsonFile.Options));
        }
        WorkspaceRoot = candidate;
        StartupWarning = null;
    }

    public void ConfigureTemplate(string path)
    {
        var template = NormalizeDirectory(path);
        if (IsSameOrChild(WorkspaceRoot, template))
            throw new ArgumentException("模板目录不能是工作区或工作区的上级目录，以免复制递归包含实例");
        if (!Directory.Exists(template)) throw new DirectoryNotFoundException($"模板目录不存在：{template}");
        if ((File.GetAttributes(template) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("模板目录不能为目录链接");
        var settingsFile = Path.Combine(WorkspaceRoot, "shell_settings.json");
        var settings = AtomicJsonFile.ReadObject(settingsFile);
        settings["TemplateDirectory"] = template;
        AtomicJsonFile.Write(settingsFile, settings.ToJsonString(AtomicJsonFile.Options));
    }

    internal static string ResolveTemplate(string workspaceRoot)
    {
        var file = Path.Combine(workspaceRoot, "shell_settings.json");
        try
        {
            if (AtomicJsonFile.ReadObject(file)["TemplateDirectory"] is { } configured)
            {
                var template = NormalizeDirectory(configured.GetValue<string>());
                if (IsSameOrChild(workspaceRoot, template))
                    throw new ArgumentException("模板目录不能包含工作区");
                return template;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException($"模板配置无效：{file}。请修复 TemplateDirectory；原文件已保留。{ex.Message}", ex);
        }
        var preferred = Path.Combine(workspaceRoot, "template");
        var legacy = Path.Combine(workspaceRoot, "exe_template");
        return !Directory.Exists(preferred) && Directory.Exists(legacy) ? legacy : preferred;
    }

    internal static bool PathsOverlap(string first, string second) => IsSameOrChild(first, second) || IsSameOrChild(second, first);

    private static bool IsSameOrChild(string candidate, string parent)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return normalizedCandidate.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
            path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("请选择有效的绝对目录路径");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择专用工作目录，不能使用磁盘或共享根目录");
        foreach (var segment in full[Path.GetPathRoot(full)!.Length..].Split(['\\', '/']))
        {
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.EndsWith('.') || segment.EndsWith(' '))
                throw new ArgumentException("目录路径包含无效文件夹名");
        }
        if (File.Exists(full)) throw new ArgumentException("所选路径是文件，请选择目录");
        return full;
    }
}

/// <summary>同目录临时文件加原子替换；无效的已有 JSON 始终保留。</summary>
internal static class AtomicJsonFile
{
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    internal static JsonObject ReadObject(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new JsonException("配置必须为 JSON 对象");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"无法读取设置 {path}。请修复该文件或从备份恢复；原文件已保留。{ex.Message}", ex);
        }
    }

    internal static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
