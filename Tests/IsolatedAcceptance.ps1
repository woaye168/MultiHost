param([switch]$Interactive, [switch]$InProcess, [string]$ReportPath)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('MultiHostAcceptance-' + [guid]::NewGuid().ToString('N'))
$hostSource = Join-Path $fixture 'host'
$childSource = Join-Path $fixture 'child'
$workspace = Join-Path $fixture 'workspace'
New-Item -ItemType Directory -Path $hostSource, $childSource, $workspace | Out-Null
Get-ChildItem $root -Recurse -File | Where-Object { $_.Extension -in '.cs', '.xaml', '.manifest' -and $_.FullName -notmatch '\\(bin|obj|Tests)\\' } | ForEach-Object {
    $destination = Join-Path $hostSource ($_.FullName.Substring($root.Length).TrimStart('\'))
    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
    Copy-Item $_.FullName $destination
}
Copy-Item (Join-Path $PSScriptRoot 'IsolatedAcceptance.cs.fixture') (Join-Path $hostSource 'Acceptance.cs')
Copy-Item (Join-Path $PSScriptRoot 'IsolatedAcceptance.Interaction.cs.fixture') (Join-Path $hostSource 'Acceptance.Interaction.cs')
@'
<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><UseWindowsForms>true</UseWindowsForms><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><StartupObject>MultiHost.Acceptance</StartupObject><NoWarn>WPF0001</NoWarn></PropertyGroup>
</Project>
'@ | Set-Content (Join-Path $hostSource 'Acceptance.csproj')
@'
<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>WinExe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWindowsForms>true</UseWindowsForms><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>FixtureApp</AssemblyName></PropertyGroup>
</Project>
'@ | Set-Content (Join-Path $childSource 'FixtureApp.csproj')
@'
using System.Text.Json;
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "started.json"), JsonSerializer.Serialize(new { Args = args, Pid = Environment.ProcessId }));
        if (args.Contains("--no-window")) { Application.Run(); return; }
        var window = new Form { Text = "MultiHost harmless fixture", ClientSize = new System.Drawing.Size(431, 277) };
        var input = new TextBox { Left = 16, Top = 16, Width = 360, AccessibleName = "Fixture input" };
        var button = new Button { Left = 16, Top = 56, Width = 360, Text = "Confirm input" };
        button.Click += (_, _) => button.Text = "Confirmed: " + input.Text;
        window.Controls.Add(input);
        window.Controls.Add(button);
        Application.Run(window);
    }
}
'@ | Set-Content (Join-Path $childSource 'Program.cs')

$previousWorkspace = $env:MULTIHOST_WORKSPACE
$previousFixture = $env:MULTIHOST_FIXTURE_APP
$previousInProcess = $env:MULTIHOST_ACCEPTANCE_INPROCESS
try {
    $env:MULTIHOST_WORKSPACE = $workspace
    $env:MULTIHOST_FIXTURE_APP = Join-Path $fixture 'template-source'
    $env:MULTIHOST_ACCEPTANCE_INPROCESS = if ($InProcess) { '1' } else { '0' }
    dotnet build (Join-Path $childSource 'FixtureApp.csproj') -o $env:MULTIHOST_FIXTURE_APP --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Fixture application build failed.' }
    Write-Output "Isolated fixture: $fixture"
    $runArgs = @('run', '--project', (Join-Path $hostSource 'Acceptance.csproj'))
    if ($Interactive) { $runArgs += @('--', '--interactive') }
    & dotnet @runArgs
    if ($LASTEXITCODE -ne 0) { throw "Acceptance failed. Evidence retained at: $fixture" }
    if ($ReportPath -and !$Interactive) {
        $events = Get-Content (Join-Path $hostSource 'bin\Debug\net10.0-windows\acceptance.json') -Raw | ConvertFrom-Json
        $checks = @($events | Where-Object { $_.PSObject.Properties.Name -contains 'Check' })
        $commands = @($events | Where-Object { $_.PSObject.Properties.Name -contains 'Command' } | ForEach-Object {
            [ordered]@{ Command = @($_.Command | ForEach-Object { $_.Replace($fixture, '<temporary fixture>') }); ExitCode = $_.Reply.Code; Error = $_.Reply.Error }
        })
        $skipped = @($events | Where-Object { $_.PSObject.Properties.Name -contains 'Skipped' })
        [ordered]@{ Harness = 'Tests/IsolatedAcceptance.ps1'; Scope = 'Generated WinForms applications in an isolated workspace; real user settings unchanged'; CommandTransport = $(if ($InProcess) { 'In-process handler; external CLI transport not tested' } else { 'External CLI processes' }); Checks = $checks.Count; Failures = @($checks | Where-Object { !$_.Passed }).Count; Skipped = $skipped; Assertions = $checks; Commands = $commands } |
            ConvertTo-Json -Depth 12 | Set-Content -Encoding utf8 -LiteralPath $ReportPath
    }
    Write-Output "Acceptance evidence retained at: $fixture"
} finally {
    $env:MULTIHOST_WORKSPACE = $previousWorkspace
    $env:MULTIHOST_FIXTURE_APP = $previousFixture
    $env:MULTIHOST_ACCEPTANCE_INPROCESS = $previousInProcess
}
