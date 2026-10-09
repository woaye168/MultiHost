# MultiHost

Windows 应用多实例管理器，使用 .NET 10、WPF 和 WinForms 窗口宿主。将应用复制到独立目录，集中管理启动、停止、窗口内嵌和弹出，并通过 CLI 操作正在运行的界面。

## 构建与启动

需要 Windows 和 .NET 10 SDK。在仓库根目录运行：

```powershell
dotnet build src/MultiHost.csproj -c Release
dotnet run --project src/MultiHost.csproj
```

界面启动时会自动请求管理员权限，直接显示 Windows UAC 授权窗口，不增加应用确认弹窗；已是管理员时直接启动，取消 UAC 时退出。CLI 不自动提权，请在同一用户的管理员终端中操作已启动的界面；`cli help` 无需管理员权限。更新后请先关闭旧版界面再启动新版。

## 首次使用

1. 打开「设置」，选择专用工作区。默认位置为 `%LocalAppData%\MultiHost\Workspace`。
2. 选择模板目录，其中应包含应用及完整依赖文件。默认使用工作区中的 `template`，兼容旧目录名 `exe_template`。
3. 点击右上角「创建」，填写名称、文件夹名，确认启动程序后创建。存在多个 `.exe` 时，注意区分主程序与辅助程序。
4. 在实例卡片点击「配置」检查参数，然后启动。参数默认为空，按应用要求填写；无需窗口或不适合内嵌的程序可以关闭「启动后内嵌窗口」。

已有应用可放进工作区的独立子目录，关闭「设置」或运行 `cli refresh` 后发现。启动程序支持 `bin\Application.exe` 等相对路径，工作目录为该可执行文件所在目录。配置过的执行文件不会被扫描到的辅助程序替换；文件缺失时仍保留实例供修复。切换工作区或模板前需停止实例，并完成当前操作。

## 实例卡片

主界面全宽显示实例卡片；未启动或已停止的实例保留占位区域。卡片提供勾选、启动、停止、配置、激活、弹出和收回；右上角「启动勾选」「停止勾选」只操作勾选的卡片。创建、设置及实例配置分别使用独立窗口，关闭配置窗口不会保存未提交的修改。

内嵌窗口默认铺满卡片内容区，并随主窗口调整大小；弹出时恢复应用最初的客户区尺寸，收回后再次铺满。卡片数量较多时可滚动查看。启动尺寸参数只在显式勾选后传给目标程序，不控制卡片大小。

## CLI

先启动 GUI，再从同一用户的管理员 PowerShell 调用构建后的程序：

```powershell
$app = '.\bin\Release\net10.0-windows\MultiHost.exe'
& $app cli help
& $app cli status
& $app cli workspace 'C:\MultiHostWorkspace'
& $app cli template 'C:\PortableApps\MyApplication'
& $app cli create '应用 01' 'app-01' 'bin\Application.exe'
& $app cli config 'app-01' 800 600 '--profile profile-01' true false
& $app cli start 'app-01'
& $app cli popout 'app-01'
& $app cli embed 'app-01'
& $app cli stop 'app-01'
```

`config` 参数顺序为：文件夹、宽、高、启动参数、是否自动内嵌、是否附加尺寸参数，随后可选相对程序路径和名称。`stopall` 停止本次托管会话及待启动队列；`shutdown` 退出管理器并停止托管实例。CLI 输出 JSON，失败返回非零退出码；完整命令以 `cli help` 为准。

## 配置与迁移

工作区选择保存在 `%LocalAppData%\MultiHost\settings.json`。工作区中的 `instances.json` 保存实例，`shell_settings.json` 保存主题、启动间隔及模板路径。配置使用原子写入；损坏文件会显示错误并保留，不会静默清空。复制完成但配置保存失败时，复制文件保留，错误信息提供恢复路径。

旧版本用户请手动选择原工作区，保留原有实例和显式启动参数。新版本不默认添加 `-width`、`-height`；只有目标程序支持这些标志时，勾选尺寸参数选项（配置字段 `IncludeSizeArguments`）。

可用环境变量指定独立工作区；GUI 与 CLI 必须使用相同环境。设置该变量时，工作区切换只影响当前会话，不覆盖用户保存的工作区：

```powershell
$env:MULTIHOST_WORKSPACE = 'C:\MultiHostSandbox'
dotnet run --project src/MultiHost.csproj
```

## 适用范围

适合可从独立目录运行的 Windows `.exe` 应用。复制程序文件不等于操作系统沙箱，也不会自动绕过应用的单实例限制。使用共享 `%AppData%`、注册表或服务的应用，可能需要它自己的用户配置目录参数；例如支持该参数的应用可为不同实例指定不同的 `--user-data-dir`。

内嵌依赖应用提供可捕获的普通窗口；特殊渲染窗口可能无法内嵌，此时可使用独立窗口模式。停止或退出会终止本次托管的进程树，请先保存目标应用中的工作。管理器不接管外部已运行的同目录进程。

## 开发与验证

```powershell
dotnet test Tests/MultiHost.Tests.csproj -c Release
powershell -File .\Tests\IsolatedAcceptance.ps1
powershell -File .\Tests\IsolatedAcceptance.ps1 -ReportPath .\Tests\acceptance-latest.json
powershell -File .\Tests\IsolatedAcceptance.ps1 -InProcess -ReportPath .\Tests\acceptance-inprocess.json
powershell -File .\Tests\AppStartupAcceptance.ps1 -ReportPath .\Tests\startup-acceptance-report.json
```

xUnit 覆盖配置、路径边界、实例创建、生命周期和提权启动策略；提权测试使用替身，不实际触发 UAC。隔离验收会在临时目录生成无害的 WinForms 测试应用，以独立工作区验证实际启动、CLI、窗口操作和退出行为；脚本输出证据目录，并恢复环境变量。`AppStartupAcceptance.ps1` 必须从管理员终端运行，使用已构建的 Release 程序验证管理员启动、重复启动和 CLI 退出；脚本不会自动操作 UAC。`-Interactive` 可启动交互式测试界面。真实窗口测试需要交互式 Windows 桌面。

按 TDD 修改：先增加能复现问题的失败测试，再实现修复，最后运行相关测试和隔离验收。已有回归包括默认参数注入、环境隔离、复制期间配置更新以及批量停止后的待启动队列。不要让测试访问个人工作区或依赖特定业务应用。

手动 UI 验收由用户完成，自动检查以单元测试和编译后的隔离验收为主。如果运行环境禁止命名管道访问，可加 `-InProcess` 直接验证同一命令处理入口和实际实例进程；报告会标明跳过的跨进程 CLI 检查，此模式不能替代完整 IPC 验收。
