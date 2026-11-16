# Requires Windows PowerShell 5.1. This resource is run elevated only after the app's confirmation dialog.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Components,
    [string] $ResultPath = '',
    [int] $OwnerProcessId = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function New-CapabilityWorker([string] $Name) {
    $worker = [powershell]::Create()
    $null = $worker.AddCommand('Add-WindowsCapability').AddParameter('Online').AddParameter('Name', $Name)
    return $worker
}

function Invoke-CapabilityOperation([string] $Name) {
    # A separate PowerShell pipeline exposes DISM's real progress and its StopProcessing cancellation.
    # Never terminate DismHost/TrustedInstaller: cancellation must be handled by Windows servicing.
    $worker = New-CapabilityWorker $Name
    $pending = $null
    $lastPercent = -1
    $cancelReason = ''
    try {
        $pending = $worker.BeginInvoke()
        while (-not $pending.IsCompleted) {
            if ($worker.Streams.Progress.Count -gt 0) {
                $progress = $worker.Streams.Progress[$worker.Streams.Progress.Count - 1]
                if ($progress.PercentComplete -ge 0 -and $progress.PercentComplete -ne $lastPercent) {
                    $lastPercent = $progress.PercentComplete
                    Write-Host "Windows 报告进度：$lastPercent%"
                    Write-Progress -Activity "安装语言组件：$Name" -Status "Windows 报告进度：$lastPercent%" -PercentComplete $lastPercent
                }
            }
            $ownerExited = $OwnerProcessId -gt 0 -and $null -eq (Get-Process -Id $OwnerProcessId -ErrorAction SilentlyContinue)
            if ([string]::IsNullOrEmpty($cancelReason)) {
                if ($ownerExited) {
                    $cancelReason = '屏幕翻译已退出，正在取消本次安装。'
                    Write-Host $cancelReason -ForegroundColor Yellow
                    $null = $worker.BeginStop($null, $null)
                }
            }
            Start-Sleep -Milliseconds 200
        }
        try { $output = @($worker.EndInvoke($pending)) }
        catch {
            if (-not [string]::IsNullOrEmpty($cancelReason)) { throw $cancelReason }
            throw
        }
        if ($worker.HadErrors) {
            if (-not [string]::IsNullOrEmpty($cancelReason)) { throw $cancelReason }
            throw $worker.Streams.Error[0]
        }
        # If Windows completed an uncancellable commit successfully, preserve that result.
        if ($output.Count -eq 0) {
            if (-not [string]::IsNullOrEmpty($cancelReason)) { throw $cancelReason }
            throw 'Windows 未返回组件安装结果，请检查 DISM 日志。'
        }
        return $output[$output.Count - 1]
    }
    finally {
        if ($null -ne $pending -and -not $pending.IsCompleted) {
            Write-Host '正在通知 Windows 取消并等待当前操作结束……'
            $worker.Stop()
        }
        Write-Progress -Activity "安装语言组件：$Name" -Completed
        $worker.Dispose()
    }
}

function Install-LanguageCapability([string] $Name) {
    Write-Host "正在检查本地组件：$Name"
    $capability = Get-WindowsCapability -Online -Name $Name
    if ($null -eq $capability -or [string]::IsNullOrEmpty($capability.Name)) {
        throw "Windows 不提供此语言组件：$Name"
    }
    if ($capability.State -eq 'Installed') {
        Write-Host "已安装，跳过：$Name"
        return
    }
    if ($capability.State -eq 'InstallPending') {
        $script:RestartNeeded = $true
        return
    }
    Write-Host "正在通过 Windows Update 安装：$Name（可能需要几分钟）"
    $result = Invoke-CapabilityOperation $Name
    if ($result.RestartNeeded) { $script:RestartNeeded = $true }
    $state = (Get-WindowsCapability -Online -Name $Name).State
    if ($state -eq 'InstallPending') { $script:RestartNeeded = $true }
    elseif ($state -ne 'Installed') { throw "语言组件未安装完成：$Name ($state)" }
}

function Install-VisualCpp {
    $path = Join-Path ([IO.Path]::GetTempPath()) ('ScreenTranslator-VC-' + [Guid]::NewGuid().ToString('N') + '.exe')
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile $path -TimeoutSec 300
        $signature = Get-AuthenticodeSignature -FilePath $path
        if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
            $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
            throw 'Visual C++ 安装包没有有效的 Microsoft 签名，已停止安装。'
        }
        $process = Start-Process -FilePath $path -ArgumentList '/install', '/passive', '/norestart' -Wait -PassThru
        if ($process.ExitCode -eq 3010) { $script:RestartNeeded = $true }
        elseif ($process.ExitCode -ne 0) { throw "Visual C++ 安装失败，退出码：$($process.ExitCode)" }
    }
    finally {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-EnvironmentInstallation([string] $RequestedComponents) {
    $requested = @($RequestedComponents.Split(',') | Select-Object -Unique)
    $allowed = @('EnglishOcr', 'ChineseOcr', 'EnglishSpeech', 'VisualCpp')
    foreach ($component in $requested) {
        if ($component -notin $allowed) { throw "未知的运行环境组件：$component" }
    }
    # Basic is required by OCR/TextToSpeech. Install each prerequisite first and only once.
    $capabilities = @()
    if ('EnglishOcr' -in $requested -or 'EnglishSpeech' -in $requested) {
        $capabilities += 'Language.Basic~~~en-US~0.0.1.0'
    }
    if ('ChineseOcr' -in $requested) { $capabilities += 'Language.Basic~~~zh-CN~0.0.1.0' }
    if ('EnglishOcr' -in $requested) { $capabilities += 'Language.OCR~~~en-US~0.0.1.0' }
    if ('ChineseOcr' -in $requested) { $capabilities += 'Language.OCR~~~zh-CN~0.0.1.0' }
    if ('EnglishSpeech' -in $requested) { $capabilities += 'Language.TextToSpeech~~~en-US~0.0.1.0' }
    if ('VisualCpp' -in $requested) { Install-VisualCpp }
    foreach ($name in $capabilities) { Install-LanguageCapability $name }
}

function Get-OptionalContentPolicy {
    $machine = [Microsoft.Win32.Registry]::LocalMachine
    $servicing = $machine.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Servicing')
    $updates = $machine.OpenSubKey('SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate')
    $automaticUpdates = $machine.OpenSubKey('SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU')
    try {
        $exists = $null -ne $servicing -and 'RepairContentServerSource' -in $servicing.GetValueNames()
        $value = if ($exists) { $servicing.GetValue('RepairContentServerSource') } else { $null }
        $kind = if ($exists) { $servicing.GetValueKind('RepairContentServerSource') } else { 'DWord' }
        $blocked = $null -ne $servicing -and $servicing.GetValue('UseWindowsUpdate') -eq 2
        if ($null -ne $updates) {
            $blocked = $blocked -or $updates.GetValue('DoNotConnectToWindowsUpdateInternetLocations') -eq 1
            if ([Environment]::OSVersion.Version.Build -lt 22621) {
                $blocked = $blocked -or $updates.GetValue('SetPolicyDrivenUpdateSourceForFeatureUpdates') -eq 1 `
                    -or $updates.GetValue('SetPolicyDrivenUpdateSourceForQualityUpdates') -eq 1
            }
        }
        [pscustomobject]@{
            KeyExisted = $null -ne $servicing
            Existed = $exists
            Value = $value
            Kind = $kind
            Blocked = $blocked -or [Environment]::OSVersion.Version.Build -ge 26100
            UsesWsus = $null -ne $automaticUpdates -and $automaticUpdates.GetValue('UseWUServer') -eq 1
        }
    }
    finally {
        if ($null -ne $servicing) { $servicing.Dispose() }
        if ($null -ne $updates) { $updates.Dispose() }
        if ($null -ne $automaticUpdates) { $automaticUpdates.Dispose() }
    }
}

function Set-OptionalContentPolicy([object] $Snapshot, [bool] $EnableDirectDownload) {
    $path = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Servicing'
    $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($path)
    try {
        if ($EnableDirectDownload) {
            $key.SetValue('RepairContentServerSource', 2, [Microsoft.Win32.RegistryValueKind]::DWord)
        }
        elseif ($Snapshot.Existed) {
            $key.SetValue('RepairContentServerSource', $Snapshot.Value, $Snapshot.Kind)
        }
        else { $key.DeleteValue('RepairContentServerSource', $false) }
        $removeEmptyKey = -not $EnableDirectDownload -and -not $Snapshot.KeyExisted `
            -and $key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0
    }
    finally { $key.Dispose() }
    if ($removeEmptyKey) { [Microsoft.Win32.Registry]::LocalMachine.DeleteSubKey($path, $false) }
}

function Confirm-DirectDownload {
    Add-Type -AssemblyName System.Windows.Forms
    $prompt = "检测到企业更新来源，或当前更新源无法提供语言组件。`n`n是否仅本次允许从 Microsoft Windows Update 下载语言组件？`n`n" +
        "这会临时调整可选组件的下载来源，安装完成或报错后恢复原配置。常规 Windows 更新来源保持不变。`n`n" +
        '选择“否”将取消安装。'
    $answer = [System.Windows.Forms.MessageBox]::Show(
        $prompt, '语言组件下载来源',
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Question,
        [System.Windows.Forms.MessageBoxDefaultButton]::Button2)
    return $answer -eq [System.Windows.Forms.DialogResult]::Yes
}

function Invoke-DirectDownloadRetry([scriptblock] $Install) {
    $snapshot = Get-OptionalContentPolicy
    if ($snapshot.Blocked) {
        throw '系统策略不允许本次自动切换语言组件来源。请让管理员允许从 Windows Update 下载可选组件，或提供匹配此 Windows 版本的 Features on Demand 离线安装源。'
    }
    if ($snapshot.Value -eq 2) {
        throw '语言组件已配置为从 Windows Update 下载，仍返回 0x800f0954。请检查企业更新策略或让管理员提供 Features on Demand 离线安装源。'
    }
    if (-not (Confirm-DirectDownload)) { return $false }
    try {
        Write-Host '已确认：临时从 Windows Update 下载语言组件。'
        Set-OptionalContentPolicy $snapshot $true
        & $Install
    }
    finally {
        Set-OptionalContentPolicy $snapshot $false
        Write-Host '已恢复原来的可选组件下载配置。'
    }
    return $true
}

function Invoke-RequestedEnvironment([string] $RequestedComponents) {
    $requested = $RequestedComponents.Split(',')
    $hasLanguage = @($requested | Where-Object { $_ -in @('EnglishOcr', 'ChineseOcr', 'EnglishSpeech') }).Count -gt 0
    if ($hasLanguage) {
        $policy = Get-OptionalContentPolicy
        if ($policy.UsesWsus -and $policy.Value -ne 2) {
            # Ask before the first DISM install so an unreachable WSUS cannot delay this confirmation.
            return Invoke-DirectDownloadRetry { Invoke-EnvironmentInstallation $RequestedComponents }
        }
    }
    try { Invoke-EnvironmentInstallation $RequestedComponents }
    catch {
        if ($_.Exception.ToString() -notmatch '(?i)0x800f0954') { throw }
        return Invoke-DirectDownloadRetry { Invoke-EnvironmentInstallation $RequestedComponents }
    }
    return $true
}

function Main {
    $script:RestartNeeded = $false
    $transcribing = $false
    $log = ''
    $message = ''
    $mutex = $null
    $ownsMutex = $false
    try {
        $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw '安装需要管理员权限。请重新启动屏幕翻译，并确认安装及管理员权限提示。'
        }
        $mutex = [Threading.Mutex]::new($false, 'Global\ScreenTranslator.EnvironmentInstallation')
        try { $ownsMutex = $mutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
        if (-not $ownsMutex) { throw '已有一个屏幕翻译环境安装正在运行，请先取消或等待它结束，不要重复启动安装。' }
        $logs = Join-Path $env:ProgramData 'ScreenTranslator\Logs'
        New-Item -ItemType Directory -Path $logs -Force | Out-Null
        $log = Join-Path $logs ('environment-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $PID + '.log')
        Start-Transcript -Path $log | Out-Null
        $transcribing = $true
        $Host.UI.RawUI.WindowTitle = '屏幕翻译 - 正在安装运行环境'
        Write-Host '正在安装您确认的组件。请保持此窗口打开，完成后会自动关闭。'
        Write-Host "安装日志：$log"
        if (-not (Invoke-RequestedEnvironment $Components)) { return 1223 }
        if ($script:RestartNeeded) { return 3010 }
        return 0
    }
    catch {
        $message = $_.Exception.Message
        if ($_.Exception.ToString() -match '(?i)0x800f0954') {
            $message = '语言组件下载失败（0x800f0954）。当前 Windows 更新来源无法提供所需文件。请让管理员检查 WSUS/可选组件下载策略，或提供匹配系统版本的 Features on Demand 离线安装源。'
        }
        Write-Host $message -ForegroundColor Red
        return 1
    }
    finally {
        if ($ownsMutex) { $mutex.ReleaseMutex() }
        if ($null -ne $mutex) { $mutex.Dispose() }
        if ($transcribing) { Stop-Transcript | Out-Null }
        if (-not [string]::IsNullOrEmpty($ResultPath)) {
            [IO.File]::WriteAllText($ResultPath, (@{ Message = $message; LogPath = $log } | ConvertTo-Json),
                (New-Object Text.UTF8Encoding($false)))
        }
    }
}

# Dot sourcing exposes functions for tests without installing or requesting elevation.
if ($MyInvocation.InvocationName -ne '.') { exit (Main) }
