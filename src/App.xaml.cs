using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace MultiHost;

public partial class App : System.Windows.Application
{
    private Mutex? _single;
    private bool _ownsMutex;
    protected override async void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (e.Args.FirstOrDefault()?.Equals("cli", StringComparison.OrdinalIgnoreCase) == true)
        {
            Shutdown(await CliRunner.RunAsync(e.Args.Skip(1).ToArray()));
            return;
        }
        try
        {
            if (GuiElevation.ReadWorkspaceOverride(e.Args) is { } workspaceOverride)
                Environment.SetEnvironmentVariable("MULTIHOST_WORKSPACE", workspaceOverride);
        }
        catch (ArgumentException ex) { LogService.Log(ex.Message); Shutdown(2); return; }
        var elevation = GuiElevation.Ensure(e.Args, IsAdministrator(), Environment.ProcessPath!,
            Environment.GetEnvironmentVariable("MULTIHOST_WORKSPACE"));
        if (!elevation.ContinueStartup)
        {
            if (elevation.Error != null) LogService.Log(elevation.Error);
            Shutdown(elevation.ExitCode);
            return;
        }
        _single = new Mutex(true, "Local\\" + GuiPipe.Name, out _ownsMutex);
        if (!_ownsMutex)
        {
            try { await GuiPipe.SendAsync(["activate"]); }
            catch (Exception ex) { System.Windows.MessageBox.Show($"MultiHost 已在运行，但无法连接。请检查其权限是否与当前进程一致。\n{ex.Message}", "MultiHost"); }
            Shutdown();
            return;
        }
        string? warning = null;
        try { ThemeManager.Initialize(); }
        catch (Exception ex) { warning = ex.Message; }
        base.OnStartup(e);
        var window = new MainWindow(warning);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _single?.ReleaseMutex();
        _single?.Dispose();
        base.OnExit(e);
    }
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
