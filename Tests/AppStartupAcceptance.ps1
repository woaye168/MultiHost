param(
    [string]$ApplicationPath = (Join-Path $PSScriptRoot '..\src\bin\Release\net10.0-windows\MultiHost.exe'),
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
$ApplicationPath = [IO.Path]::GetFullPath($ApplicationPath)
if (!(Test-Path -LiteralPath $ApplicationPath -PathType Leaf)) { throw "Build the Release application first: $ApplicationPath" }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $parentIsAdmin = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
finally { $identity.Dispose() }
if (!$parentIsAdmin) { throw 'Run AppStartupAcceptance.ps1 from an administrator terminal. This test does not launch or automate UAC.' }
$startupFixture = Join-Path ([IO.Path]::GetTempPath()) ('MultiHostStartup-' + [guid]::NewGuid().ToString('N'))
$startupWorkspace = Join-Path $startupFixture 'workspace'
New-Item -ItemType Directory -Path $startupWorkspace | Out-Null
$startupChecks = [System.Collections.Generic.List[object]]::new()
$startupCommands = [System.Collections.Generic.List[object]]::new()
$userSettings = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MultiHost\settings.json'
$originalSettings = if (Test-Path -LiteralPath $userSettings) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($userSettings)) } else { $null }

function Add-StartupCheck([string]$Name, [bool]$Passed) {
    $startupChecks.Add([ordered]@{ Check = $Name; Passed = $Passed })
    Write-Host ((@{ $true = 'PASS '; $false = 'FAIL ' })[$Passed] + $Name)
}
function New-StartupProcessInfo {
    $info = [Diagnostics.ProcessStartInfo]::new($ApplicationPath)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WorkingDirectory = Split-Path $ApplicationPath
    $info.Environment['MULTIHOST_WORKSPACE'] = $startupWorkspace
    return $info
}
function Invoke-StartupCli([string[]]$CommandArgs) {
    $info = New-StartupProcessInfo
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.ArgumentList.Add('cli')
    foreach ($argument in $CommandArgs) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(10000)) { $process.Kill($true); throw 'Application CLI did not exit within 10 seconds.' }
        $text = $output.GetAwaiter().GetResult()
        if ([string]::IsNullOrWhiteSpace($text)) { throw ('Application CLI returned no JSON: ' + $errors.GetAwaiter().GetResult()) }
        $reply = $text | ConvertFrom-Json
        $startupCommands.Add([ordered]@{ Command = $CommandArgs; ExitCode = $process.ExitCode; Code = $reply.Code; Error = $reply.Error })
        if ($process.ExitCode -ne $reply.Code) { throw 'Application CLI process exit code disagrees with its JSON reply.' }
        return $reply
    } finally { $process.Dispose() }
}

$application = $null
$duplicate = $null
try {
    $application = [Diagnostics.Process]::Start((New-StartupProcessInfo))
    $status = $null
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        $status = Invoke-StartupCli @('status')
        if ($status.Code -eq 0) { break }
        if ($application.HasExited) { throw 'Application exited before exposing its CLI.' }
        Start-Sleep -Milliseconds 100
    }
    Add-StartupCheck 'actual application entrypoint accepts CLI status' ($status.Code -eq 0)
    if ($status.Code -ne 0) { throw 'Application failed to start in the isolated workspace.' }
    Add-StartupCheck 'already elevated application does not relaunch' ($status.Data.Pid -eq $application.Id)
    Add-StartupCheck 'actual application runs with administrator privileges' ($status.Data.IsAdmin -eq $true)
    Add-StartupCheck 'actual application honors isolated workspace' ($status.Data.Workspace -eq $startupWorkspace)
    Add-StartupCheck 'actual first run has no existing instances' (@($status.Data.Instances).Count -eq 0)
    $duplicate = [Diagnostics.Process]::Start((New-StartupProcessInfo))
    Add-StartupCheck 'duplicate launch exits after activating existing application' ($duplicate.WaitForExit(10000) -and $duplicate.ExitCode -eq 0)
    $afterDuplicate = Invoke-StartupCli @('status')
    Add-StartupCheck 'duplicate launch retains original GUI PID' ($afterDuplicate.Code -eq 0 -and $afterDuplicate.Data.Pid -eq $application.Id)
    Add-StartupCheck 'actual application CLI shutdown succeeds' ((Invoke-StartupCli @('shutdown')).Code -eq 0)
    Add-StartupCheck 'actual application exits cleanly after shutdown' ($application.WaitForExit(10000) -and $application.ExitCode -eq 0)
} catch {
    $startupChecks.Add([ordered]@{ Check = 'application entrypoint scenario completed'; Passed = $false; Error = $_.Exception.Message })
    Write-Warning $_.Exception.Message
} finally {
    if ($null -ne $duplicate) {
        if (!$duplicate.HasExited) { $duplicate.Kill($true); $duplicate.WaitForExit() }
        $duplicate.Dispose()
    }
    if ($null -ne $application) {
        if (!$application.HasExited) {
            try { $null = Invoke-StartupCli @('shutdown') } catch { }
            if (!$application.WaitForExit(5000)) { $application.Kill($true); $application.WaitForExit() }
        }
        $application.Dispose()
    }
    $currentSettings = if (Test-Path -LiteralPath $userSettings) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($userSettings)) } else { $null }
    Add-StartupCheck 'entrypoint acceptance preserves real user settings' ($originalSettings -eq $currentSettings)
    $report = [ordered]@{ Harness = 'Tests/AppStartupAcceptance.ps1'; Scope = 'Real application entrypoint from an administrator terminal in a fresh isolated workspace; UAC interaction excluded'; Checks = $startupChecks.Count; Failures = @($startupChecks | Where-Object { !$_.Passed }).Count; Assertions = @($startupChecks.ToArray()); Commands = @($startupCommands.ToArray()) }
    $json = $report | ConvertTo-Json -Depth 8
    $json | Set-Content -Encoding utf8 (Join-Path $startupFixture 'startup-acceptance.json')
    if ($ReportPath) { $json | Set-Content -Encoding utf8 -LiteralPath $ReportPath }
    Write-Host "Startup evidence retained at: $startupFixture"
}
if ($report.Failures -gt 0) { throw "Application startup acceptance failed: $($report.Failures) checks." }
