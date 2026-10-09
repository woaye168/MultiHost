namespace MultiHost;

/// <summary>主题模式：跟随系统 / 浅色 / 深色</summary>
public enum AppThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// 主题管理：基于 .NET 10 WPF 官方 Fluent 主题（Application.ThemeMode），
/// 获得 Win11 原生控件风格、深色标题栏与 Mica 质感。
/// 用户选择持久化到工作区 shell_settings.json。
/// </summary>
public static class ThemeManager
{
    /// <summary>当前主题模式</summary>
    public static AppThemeMode Mode { get; private set; } = AppThemeMode.System;

    /// <summary>批量启动间隔（秒），默认连续启动。</summary>
    public static int BatchIntervalSeconds { get; private set; } = 0;

    public static int EmbedWidth { get; private set; } = 800;
    public static int EmbedHeight { get; private set; } = 360;

    /// <summary>应用启动时调用（必须在主窗口创建前）：加载设置并应用</summary>
    public static void Initialize()
    {
        LoadSettings();
        Apply(Mode, save: false);
    }

    /// <summary>应用主题。Fluent 主题的 System 模式由 WPF 自动跟随系统配色，无需手动监听</summary>
    public static void Apply(AppThemeMode mode, bool save = true)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException("主题必须为 System、Light 或 Dark", nameof(mode));
        if (save) new ShellSettingsStore(AppPaths.WorkspaceRoot).Save(mode, BatchIntervalSeconds);
        Mode = mode;

        if (System.Windows.Application.Current is { } app)
        {
            app.ThemeMode = mode switch
            {
                AppThemeMode.Light => System.Windows.ThemeMode.Light,
                AppThemeMode.Dark => System.Windows.ThemeMode.Dark,
                _ => System.Windows.ThemeMode.System,
            };
        }
    }

    /// <summary>设置批量启动间隔（0-30 秒）并持久化</summary>
    public static void SetBatchInterval(int seconds)
    {
        var interval = Math.Clamp(seconds, 0, 30);
        new ShellSettingsStore(AppPaths.WorkspaceRoot).Save(Mode, interval);
        BatchIntervalSeconds = interval;
    }

    public static void SetEmbedResolution(int width, int height)
    {
        new ShellSettingsStore(AppPaths.WorkspaceRoot).SaveEmbedResolution(width, height);
        EmbedWidth = width;
        EmbedHeight = height;
    }

    #region 设置持久化

    private static void LoadSettings()
    {
        var settings = new ShellSettingsStore(AppPaths.WorkspaceRoot).Load();
        Mode = settings.Theme;
        BatchIntervalSeconds = settings.BatchIntervalSeconds;
        EmbedWidth = settings.EmbedWidth;
        EmbedHeight = settings.EmbedHeight;
    }

    #endregion
}
