using System.IO;

namespace MultiHost;

/// <summary>模板复制后合并最新实例配置；保存失败时保留文件并提供恢复路径。</summary>
public sealed class InstanceCreationService(
    string workspaceRoot,
    Func<string, IProgress<string>?, CancellationToken, Task> copyTemplate)
{
    public static Task CreateAsync(InstanceConfig config, ProcessManager manager,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        new InstanceCreationService(AppPaths.WorkspaceRoot, manager.CopyTemplateAsync)
            .CreateAsync(config, progress, cancellationToken);

    public async Task CreateAsync(InstanceConfig config, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        config.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var initial = InstanceStore.Load(workspaceRoot);
        if (initial.Any(c => c.FolderName.Equals(config.FolderName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("实例文件夹名已存在，请使用新的名称");
        var target = Path.Combine(Path.GetFullPath(workspaceRoot), config.FolderName);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException($"实例目录已存在：{target}");

        await copyTemplate(config.FolderName, progress, cancellationToken);

        try
        {
            // 复制结束后重新读取，保留复制期间用户对其他实例所做的修改。
            var latest = InstanceStore.Load(workspaceRoot);
            if (latest.Any(c => c.FolderName.Equals(config.FolderName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("复制期间已出现同名实例配置，请确认其启动程序");
            var executable = Path.Combine(target, config.ExeFileName);
            if (!File.Exists(executable)) throw new FileNotFoundException("复制完成后找不到所选启动程序", executable);
            latest.Add(config);
            InstanceStore.Save(latest, workspaceRoot);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // 不能证明目录中没有外部新增文件，因此不在失败时递归删除。
            throw new IOException($"应用已复制到 {target}，但未能保存新实例配置。文件已保留。请修复 instances.json 后刷新，并确认启动程序为 {config.ExeFileName}。{ex.Message}", ex);
        }
    }
}
