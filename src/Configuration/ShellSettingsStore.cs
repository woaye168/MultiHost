using System.IO;

namespace MultiHost;

public sealed record ShellSettings(AppThemeMode Theme = AppThemeMode.System, int BatchIntervalSeconds = 0,
    int EmbedWidth = 800, int EmbedHeight = 360);

/// <summary>主题设置与模板设置共享文件，保存时保留其他配置项。</summary>
public sealed class ShellSettingsStore(string workspaceRoot)
{
    private string SettingsFile => Path.Combine(workspaceRoot, "shell_settings.json");

    public ShellSettings Load()
    {
        var json = AtomicJsonFile.ReadObject(SettingsFile);
        try
        {
            var theme = AppThemeMode.System;
            if (json["Theme"] is { } value && (!Enum.TryParse(value.GetValue<string>(), true, out theme) || !Enum.IsDefined(theme)))
                throw new ArgumentException("Theme 必须为 System、Light 或 Dark");
            var interval = json["BatchIntervalSeconds"]?.GetValue<int>() ?? 0;
            var width = json["EmbedWidth"]?.GetValue<int>() ?? 800;
            var height = json["EmbedHeight"]?.GetValue<int>() ?? 360;
            ValidateEmbedResolution(width, height);
            return new(theme, Math.Clamp(interval, 0, 30), width, height);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            throw new InvalidDataException($"设置无效：{SettingsFile}。请修复 Theme、BatchIntervalSeconds 或 EmbedWidth / EmbedHeight；原文件已保留。{ex.Message}", ex);
        }
    }

    public void Save(AppThemeMode theme, int batchIntervalSeconds)
    {
        if (!Enum.IsDefined(theme)) throw new ArgumentException("主题必须为 System、Light 或 Dark", nameof(theme));
        if (batchIntervalSeconds is < 0 or > 30) throw new ArgumentOutOfRangeException(nameof(batchIntervalSeconds), "批量启动间隔必须在 0 到 30 秒之间");
        _ = Load();
        var json = AtomicJsonFile.ReadObject(SettingsFile);
        json["Theme"] = theme.ToString();
        json["BatchIntervalSeconds"] = batchIntervalSeconds;
        AtomicJsonFile.Write(SettingsFile, json.ToJsonString(AtomicJsonFile.Options));
    }

    public void SaveEmbedResolution(int width, int height)
    {
        ValidateEmbedResolution(width, height);
        _ = Load();
        var json = AtomicJsonFile.ReadObject(SettingsFile);
        json["EmbedWidth"] = width;
        json["EmbedHeight"] = height;
        AtomicJsonFile.Write(SettingsFile, json.ToJsonString(AtomicJsonFile.Options));
    }

    public static void ValidateEmbedResolution(int width, int height)
    {
        if (width is < 1 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(width), "内嵌窗口宽度必须在 1 到 16384 像素之间");
        if (height is < 1 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(height), "内嵌窗口高度必须在 1 到 16384 像素之间");
    }
}
