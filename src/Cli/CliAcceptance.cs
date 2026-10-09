using System.Text.Json;

namespace MultiHost;

public static class CliAcceptance
{
    public static async Task<CommandReply> RunAsync(string[] folders, Func<string[], Task<CommandReply>>? send = null)
    {
        if (folders.Length != 2 || folders.Any(string.IsNullOrWhiteSpace) || folders.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2)
            return new(2, Error: "用法: autopilot <实例文件夹1> <实例文件夹2>；必须明确指定两个不同且已停止的实例");
        var evidence = new List<object>();
        var owned = new List<string>();
        string? failure = null;
        async Task<JsonElement> Call(params string[] args)
        {
            var reply = await (send ?? GuiPipe.SendAsync)(args);
            evidence.Add(new { Command = args, Reply = reply });
            if (reply.Code != 0) throw new InvalidOperationException(reply.Error ?? $"命令失败: {args[0]}");
            return JsonSerializer.SerializeToElement(reply.Data);
        }
        void Check(bool ok, string name)
        {
            evidence.Add(new { Check = name, Passed = ok });
            if (!ok) throw new InvalidOperationException(name);
        }
        try
        {
            var status = await Call("status");
            foreach (var folder in folders)
            {
                var item = status.GetProperty("Instances").EnumerateArray().FirstOrDefault(i => i.GetProperty("Config").GetProperty("FolderName").GetString()!.Equals(folder, StringComparison.OrdinalIgnoreCase));
                Check(item.ValueKind != JsonValueKind.Undefined, $"实例存在: {folder}");
                Check(!item.GetProperty("Running").GetBoolean(), $"{folder} 必须未由 GUI 运行");
                Check(item.GetProperty("Config").GetProperty("EmbedWindow").GetBoolean(), $"{folder} 必须启用启动后内嵌");
            }
            // GUI refuses external processes and stop only affects its owned Job sessions.
            owned.AddRange(folders);
            var windows = await Call(["start", .. folders]);
            Check(windows[0].GetProperty("Hwnd").GetInt64() != windows[1].GetProperty("Hwnd").GetInt64(), "两个独立 HWND");
            foreach (var folder in folders)
            {
                var window = await Call("check-embed", folder);
                Check(window.GetProperty("Parent").GetInt64() == window.GetProperty("ExpectedParent").GetInt64(), "精确父窗口");
                await Call("fit", folder, "fill");
                window = await Call("fit", folder, "original");
                Check(window.GetProperty("Width").GetInt32() == window.GetProperty("OriginalWidth").GetInt32() &&
                    window.GetProperty("Height").GetInt32() == window.GetProperty("OriginalHeight").GetInt32(), "恢复原始客户区");
                window = await Call("popout", folder);
                Check(window.GetProperty("PoppedOut").GetBoolean() && window.GetProperty("Parent").GetInt64() == window.GetProperty("ExpectedParent").GetInt64(), "弹出独立宿主");
                await Call("close-popout", folder);
                window = await Call("check-embed", folder);
                Check(window.GetProperty("Embedded").GetBoolean() && !window.GetProperty("PoppedOut").GetBoolean(), "关闭弹出宿主后收回");
            }
            await Call("stop", folders[0]);
            owned.Remove(folders[0]);
            status = await Call("status");
            Check(status.GetProperty("Instances").EnumerateArray().Single(i => i.GetProperty("Config").GetProperty("FolderName").GetString()!.Equals(folders[1], StringComparison.OrdinalIgnoreCase)).GetProperty("Running").GetBoolean(), "停止一个，另一个存活");
        }
        catch (Exception ex) { failure = ex.Message; }
        finally
        {
            foreach (var folder in owned)
                try { await Call("stop", folder); } catch (Exception ex) { failure = $"{failure}; 清理失败: {ex.Message}"; }
        }
        return new(failure == null ? 0 : 1, evidence, failure);
    }
}
