using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiHost;

/// <summary>
/// 当前用户的工作区路径，可通过界面或 MULTIHOST_WORKSPACE 指定。
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<WorkspaceConfiguration> Configuration = new(() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiHost"),
        Environment.GetEnvironmentVariable("MULTIHOST_WORKSPACE")));

    public static string WorkspaceRoot => Configuration.Value.WorkspaceRoot;
    public static string? StartupWarning => Configuration.Value.StartupWarning;
    public static void ConfigureWorkspace(string path, bool persist = true) => Configuration.Value.ConfigureWorkspace(path, persist);
    public static void ConfigureTemplate(string path) => Configuration.Value.ConfigureTemplate(path);

    /// <summary>母版模板目录（内含完整 exe 及其子目录）</summary>
    public static string TemplateDir => Configuration.Value.TemplateDirectory;

    /// <summary>实例扫描时排除的保留文件夹名</summary>
    internal static readonly HashSet<string> ExcludedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "template",
        "exe_template",
    };

    /// <summary>实例配置统一存储文件</summary>
    public static string ConfigFile => Path.Combine(WorkspaceRoot, "instances.json");
}

/// <summary>
/// 单个多开实例的持久化配置（序列化到 instances.json）。
/// </summary>
public class InstanceConfig
{
    /// <summary>账号备注（仅用于界面显示）</summary>
    public string Remark { get; set; } = "";

    /// <summary>实例文件夹名（工作区下的子目录名）</summary>
    public string FolderName { get; set; } = "";

    /// <summary>可执行文件名（相对实例目录，如 bin\application.exe）</summary>
    public string ExeFileName { get; set; } = "";

    /// <summary>额外自定义参数（原样追加到命令行；如需指定窗口尺寸可自行写 -width/-height 等参数）</summary>
    public string ExtraArgs { get; set; } = "";

    /// <summary>启动后是否默认内嵌到集成容器（默认内嵌，可切换回独立窗口）</summary>
    public bool EmbedWindow { get; set; } = true;

    /// <summary>上次退出是否异常（运行中被外部杀掉/崩溃）</summary>
    public bool LastExitAbnormal { get; set; }

    /// <summary>实例目录绝对路径</summary>
    [JsonIgnore]
    public string FolderFullPath => Path.Combine(AppPaths.WorkspaceRoot, FolderName);

    /// <summary>可执行文件绝对路径</summary>
    [JsonIgnore]
    public string ExeFullPath => Path.Combine(FolderFullPath, ExeFileName);

    /// <summary>拼装完整启动参数（原样使用用户填写的启动参数）</summary>
    public string BuildArguments() => ExtraArgs ?? "";

    public void Validate()
    {
        if (Remark == null) throw new ArgumentException("实例显示名称不能为 null；不需要名称时请使用空字符串");
        ProcessManager.ValidateFolder(FolderName);
        if (string.IsNullOrWhiteSpace(ExeFileName) || Path.IsPathRooted(ExeFileName) ||
            !ExeFileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("程序路径必须为实例目录中的相对 .exe 路径，例如 bin\\application.exe");
        foreach (var segment in ExeFileName.Split(['\\', '/']))
        {
            if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.EndsWith('.') || segment.EndsWith(' ') ||
                System.Text.RegularExpressions.Regex.IsMatch(segment.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new ArgumentException("程序路径不能包含上级目录、空路径或无效文件名");
        }
        if (ExtraArgs?.Contains('\0') == true)
            throw new ArgumentException("启动参数不能包含空字符");
    }
}

/// <summary>
/// instances.json 的加载/保存 + 工作区实例扫描。
/// 识别规则：工作区下除 template 外、包含 exe 的子目录均视为实例。
/// </summary>
public static class InstanceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>从磁盘加载已保存的实例配置</summary>
    public static List<InstanceConfig> Load(string? workspaceRoot = null)
    {
        var file = Path.Combine(workspaceRoot ?? AppPaths.WorkspaceRoot, "instances.json");
        try
        {
            if (File.Exists(file))
            {
                var configs = JsonSerializer.Deserialize<List<InstanceConfig>>(File.ReadAllText(file), JsonOptions)
                    ?? throw new JsonException("实例配置应为 JSON 数组");
                ValidateConfigs(configs);
                return configs;
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException)
        {
            throw new InvalidDataException($"无法读取实例配置 {file}。请修复该文件或从备份恢复；原文件已保留。{ex.Message}", ex);
        }
        return new();
    }

    /// <summary>保存全部实例配置到 instances.json</summary>
    public static void Save(IEnumerable<InstanceConfig> configs, string? workspaceRoot = null)
    {
        var root = workspaceRoot ?? AppPaths.WorkspaceRoot;
        var list = configs.ToList();
        ValidateConfigs(list);
        _ = Load(root); // 损坏的原文件不可被一次保存静默覆盖。
        AtomicJsonFile.Write(Path.Combine(root, "instances.json"), JsonSerializer.Serialize(list, JsonOptions));
    }

    private static void ValidateConfigs(List<InstanceConfig> configs)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in configs)
        {
            if (config == null) throw new InvalidDataException("实例配置不能为 null");
            config.Validate();
            if (!folders.Add(config.FolderName)) throw new InvalidDataException($"实例文件夹重复：{config.FolderName}");
        }
    }

    /// <summary>
    /// 扫描工作区，返回实例列表（已有配置按文件夹名合并，保留备注/参数；新发现的目录自动生成默认配置）。
    /// </summary>
    public static List<InstanceConfig> Scan(List<InstanceConfig> stored, string? workspaceRoot = null)
    {
        var root = workspaceRoot ?? AppPaths.WorkspaceRoot;
        var result = new List<InstanceConfig>(stored);
        if (!Directory.Exists(root))
            return result;
        var template = WorkspaceConfiguration.ResolveTemplate(root);

        // 壳应用自身所在目录（若发布到工作区内）不作为实例
        string selfDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        foreach (var dir in Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            // 排除模板、临时复制目录及链接目录。
            if (AppPaths.ExcludedFolderNames.Contains(name) || name.StartsWith(".copy-", StringComparison.OrdinalIgnoreCase) ||
                WorkspaceConfiguration.PathsOverlap(dir, template) ||
                (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                continue;
            if (dir.Equals(selfDir, StringComparison.OrdinalIgnoreCase))
                continue;

            if (result.Any(c => c.FolderName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                continue;

            var exes = FindExecutables(dir);
            if (exes.Length == 0)
                continue;

            result.Add(new InstanceConfig
            {
                FolderName = name,
                Remark = name,
                ExeFileName = Path.GetRelativePath(dir, exes[0]),
            });
        }
        return result.OrderBy(c => c.FolderName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string[] FindExecutables(string directory) => Directory.GetFiles(directory, "*.exe",
        new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
        .OrderBy(path => Path.GetRelativePath(directory, path).Count(c => c is '\\' or '/'))
        .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>扫描模板目录可用的 exe 相对路径列表（供创建多开时选择）</summary>
    public static List<string> ScanTemplateExes(string? templateDirectory = null)
    {
        var template = templateDirectory ?? AppPaths.TemplateDir;
        if (!Directory.Exists(template))
            return new();
        return FindExecutables(template)
            .Select(path => Path.GetRelativePath(template, path)).ToList();
    }
}
