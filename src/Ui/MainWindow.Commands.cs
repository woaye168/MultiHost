namespace MultiHost;

public partial class MainWindow
{
    public async Task<CommandReply> ExecuteCommandAsync(string[] args)
    {
        try
        {
            if (_closing) throw new InvalidOperationException("应用正在关闭");
            if (args.Length == 0) throw new ArgumentException("缺少命令；使用 cli help 查看用法");
            string command = args[0].ToLowerInvariant();
            void Count(int minimum, int maximum, string usage)
            {
                if (args.Length < minimum || args.Length > maximum) throw new ArgumentException(usage);
            }
            InstanceConfig Require() => args.Length > 1 && FindConfig(args[1]) is { } cfg ? cfg : throw new ArgumentException("实例不存在或缺少文件夹参数");
            InstanceConfig RequireRunning()
            {
                var cfg = Require();
                if (!_processManager.IsRunning(cfg.FolderName)) throw new InvalidOperationException("实例未运行，请先启动：" + cfg.FolderName);
                return cfg;
            }
            EmbedSlot RequireWindow()
            {
                var cfg = RequireRunning();
                var slot = _embedController.GetSlot(cfg.FolderName);
                if (slot == null || !Win32Api.IsWindow(slot.LastHwnd)) throw new InvalidOperationException("实例没有可操作的内嵌窗口，请先使用 embed：" + cfg.FolderName);
                return slot;
            }
            switch (command)
            {
                case "list": case "status": case "gui-status":
                    Count(1, 1, "status");
                    RefreshStatuses();
                    return new(0, new { Pid = Environment.ProcessId, IsAdmin = App.IsAdministrator(), Theme = ThemeManager.Mode.ToString(),
                        Workspace = AppPaths.WorkspaceRoot, Template = AppPaths.TemplateDir, ThemeManager.EmbedWidth, ThemeManager.EmbedHeight,
                        Instances = _instances.Select(i => new { i.Config, i.StatusText, i.IsSelected, Running = _processManager.IsRunning(i.Config.FolderName), Window = _embedController.GetSlot(i.Config.FolderName)?.Snapshot() }).ToArray() });
                case "workspace":
                    Count(1, 2, "workspace [绝对目录路径]");
                    if (args.Length == 2) ChangeWorkspace(args[1]);
                    return new(0, AppPaths.WorkspaceRoot);
                case "template":
                    Count(1, 2, "template [绝对目录路径]");
                    if (args.Length == 2) ChangeTemplate(args[1]);
                    return new(0, AppPaths.TemplateDir);
                case "activate":
                    Count(1, 1, "activate");
                    if (WindowState == System.Windows.WindowState.Minimized) WindowState = System.Windows.WindowState.Normal;
                    Show(); Activate();
                    return new(0);
                case "start":
                    Count(2, int.MaxValue, "start <文件夹...>");
                    var configs = args.Skip(1).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(key => FindConfig(key) ?? throw new ArgumentException($"实例不存在: {key}")).ToArray();
                    await Task.WhenAll(configs.Select(StartInstanceAsync));
                    if (configs.Any(c => !_processManager.IsRunning(c.FolderName) || (c.EmbedWindow && !_embedController.IsAttached(c.FolderName))))
                        throw new InvalidOperationException("应用已退出或尚未内嵌。请检查启动参数；无窗口应用可关闭启动后内嵌。");
                    return new(0, configs.Select(c => _embedController.GetSlot(c.FolderName)?.Snapshot()).ToArray());
                case "stop":
                    Count(2, 2, "stop <文件夹>");
                    await StopInstanceAsync(Require().FolderName); return new(0);
                case "stopall":
                    Count(1, 1, "stopall");
                    _queuedStarts.Clear();
                    _batchDelay?.Cancel();
                    foreach (var key in _busy.ToArray()) _stopRequested.Add(key);
                    foreach (var key in _processManager.RunningFolders) await StopInstanceAsync(key);
                    return new(0);
                case "embed": case "reembed":
                    Count(2, 2, "embed <文件夹>");
                    var embed = RequireRunning();
                    await _embedController.ReEmbedAsync(embed.FolderName);
                    RefreshStatuses();
                    if (!_embedController.IsAttached(embed.FolderName)) throw new InvalidOperationException("未能内嵌窗口，请查看实例状态或日志");
                    return new(0, _embedController.GetSlot(embed.FolderName)?.Snapshot());
                case "popout":
                    Count(2, 2, "popout <文件夹>");
                    var popout = RequireWindow();
                    _embedController.PopOut(args[1]);
                    RefreshStatuses();
                    return new(0, popout.Snapshot());
                case "close-popout":
                    Count(2, 2, "close-popout <文件夹>");
                    var popup = RequireWindow();
                    await _embedController.ReEmbedAsync(args[1]);
                    RefreshStatuses();
                    return new(0, popup.Snapshot());
                case "diag":
                    Count(2, 2, "diag <文件夹>");
                    var diagSlot = RequireWindow();
                    return new(0, EmbedDiagnostics.Capture(diagSlot, args[1]));
                case "capture": case "check-embed":
                    Count(2, 2, "check-embed <文件夹>");
                    var slot = RequireWindow();
                    if (!slot.IsAttached) throw new InvalidOperationException("实例窗口当前未内嵌");
                    return new(0, slot.Snapshot());
                case "fit":
                    Count(3, 3, "fit <文件夹> <fill|original>");
                    if (args[2] is not ("fill" or "original")) throw new ArgumentException("fit <文件夹> <fill|original>");
                    var fit = RequireWindow();
                    if (!fit.IsAttached) throw new InvalidOperationException("请先收回窗口，再调整内嵌尺寸");
                    fit.SetFill(args[2] == "fill");
                    return new(0, fit.Snapshot());
                case "theme":
                    Count(1, 2, "theme [system|light|dark]");
                    if (args.Length == 2)
                    {
                        if (!Enum.TryParse<AppThemeMode>(args[1], true, out var mode) || !Enum.IsDefined(mode)) throw new ArgumentException("theme <system|light|dark>");
                        ThemeManager.Apply(mode);
                        _settingsWindow?.RefreshValues();
                    }
                    return new(0, ThemeManager.Mode.ToString());
                case "resolution":
                    if (args.Length != 1 && args.Length != 3) throw new ArgumentException("resolution [宽 高]");
                    if (args.Length == 3)
                    {
                        if (!int.TryParse(args[1], out var embedWidth) || !int.TryParse(args[2], out var embedHeight))
                            throw new ArgumentException("内嵌分辨率必须为整数像素");
                        ChangeEmbedResolution(embedWidth, embedHeight);
                        _settingsWindow?.RefreshValues();
                    }
                    return new(0, new { ThemeManager.EmbedWidth, ThemeManager.EmbedHeight });
                case "config":
                    Count(6, 9, "config <文件夹> <宽> <高> <启动参数> <内嵌true|false> [附加尺寸true|false] [相对exe路径] [名称]");
                    if (!int.TryParse(args[2], out var width) || !int.TryParse(args[3], out var height) || !bool.TryParse(args[5], out var shouldEmbed))
                        throw new ArgumentException("宽高必须是整数，内嵌选项必须是 true 或 false");
                    bool? includeSize = null;
                    if (args.Length > 6)
                    {
                        if (!bool.TryParse(args[6], out var include)) throw new ArgumentException("附加尺寸选项必须是 true 或 false");
                        includeSize = include;
                    }
                    SaveConfig(Require(), width, height, args[4], shouldEmbed, includeSize, args.Length > 7 ? args[7] : null, args.Length > 8 ? args[8] : null);
                    return new(0, Require());
                case "create":
                    Count(3, 4, "create <名称> <文件夹> [相对exe路径]");
                    var programs = InstanceStore.ScanTemplateExes();
                    if (args.Length == 3 && programs.Count != 1) throw new ArgumentException("模板包含多个程序或没有程序，请明确指定相对 exe 路径");
                    await CreateInstanceAsync(args[1], args[2], args.Length > 3 ? args[3] : programs[0]);
                    return new(0);
                case "logs": Count(1, 1, "logs"); return new(0, LogService.Entries);
                case "refresh": Count(1, 1, "refresh"); ReloadInstances(); RefreshSettings(); return new(0);
                case "shutdown":
                    Count(1, 1, "shutdown");
                    _confirmedShutdown = true;
                    _ = Dispatcher.BeginInvoke(new Action(Close)); return new(0);
                default: throw new ArgumentException($"未知命令: {command}；使用 cli help 查看用法");
            }
        }
        catch (ArgumentException ex) { ShowStatus(ex.Message, true); return new(2, Error: ex.Message); }
        catch (OperationCanceledException) { ShowStatus("操作已取消"); return new(3, Error: "操作已取消"); }
        catch (Exception ex) { ShowStatus(ex.Message, true); return new(1, Error: ex.Message); }
    }
}
