# No Windows capabilities, downloads, installers, or UAC are used in this test.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$source = Join-Path $PSScriptRoot '..\scripts\Install-Environment.ps1'
$installer = [scriptblock]::Create([IO.File]::ReadAllText($source, [Text.Encoding]::UTF8))
. $installer -Components EnglishOcr
$originalCapabilityOperation = ${function:Invoke-CapabilityOperation}

# All capability-install tests call only the local fake defined below, never DISM.
function Invoke-CapabilityOperation([string] $Name) { Add-WindowsCapability -Online -Name $Name }

$script:States = @{}
$script:Added = New-Object 'Collections.Generic.List[string]'
$script:VisualCppCalls = 0
$script:RestartNeeded = $false
$script:Checks = 0

function Assert([bool] $Passed, [string] $Description) {
    $script:Checks++
    if (-not $Passed) { throw "FAIL: $Description" }
}

function Get-WindowsCapability {
    param([switch] $Online, [string] $Name)
    $state = if ($script:States.ContainsKey($Name)) { $script:States[$Name] } else { 'NotPresent' }
    [pscustomobject]@{ Name = $Name; State = $state }
}

function Add-WindowsCapability {
    param([switch] $Online, [string] $Name)
    $script:Added.Add($Name)
    $script:States[$Name] = 'Installed'
    [pscustomobject]@{ RestartNeeded = $false }
}

function Install-VisualCpp { $script:VisualCppCalls++ }

Invoke-EnvironmentInstallation 'EnglishOcr,ChineseOcr,EnglishSpeech,VisualCpp,EnglishOcr'
$expected = @(
    'Language.Basic~~~en-US~0.0.1.0',
    'Language.Basic~~~zh-CN~0.0.1.0',
    'Language.OCR~~~en-US~0.0.1.0',
    'Language.OCR~~~zh-CN~0.0.1.0',
    'Language.TextToSpeech~~~en-US~0.0.1.0'
)
Assert (($script:Added -join ',') -eq ($expected -join ',')) 'Install Basic before OCR/TTS, without duplicates'
Assert ($script:VisualCppCalls -eq 1) 'Install Visual C++ only when requested'

$script:Added.Clear()
Invoke-EnvironmentInstallation 'EnglishOcr,ChineseOcr,EnglishSpeech'
Assert ($script:Added.Count -eq 0) 'Do not reinstall installed capabilities'
Assert ($script:VisualCppCalls -eq 1) 'Do not install unrelated components'

$script:States.Clear()
Invoke-EnvironmentInstallation 'EnglishSpeech'
Assert (($script:Added -join ',') -eq 'Language.Basic~~~en-US~0.0.1.0,Language.TextToSpeech~~~en-US~0.0.1.0') 'Speech requires only Basic and TTS'

$script:Added.Clear()
$script:States['Language.OCR~~~en-US~0.0.1.0'] = 'InstallPending'
Install-LanguageCapability 'Language.OCR~~~en-US~0.0.1.0'
Assert ($script:RestartNeeded) 'Report a pending reboot'
Assert ($script:Added.Count -eq 0) 'Do not reinstall a pending capability'

$rejected = $false
try { Invoke-EnvironmentInstallation 'VisualCpp,UnknownComponent' }
catch { $rejected = $true }
Assert $rejected 'Reject unknown components before any installation'
Assert ($script:VisualCppCalls -eq 1) 'An invalid request must not partially install'

function Add-WindowsCapability {
    param([switch] $Online, [string] $Name)
    [pscustomobject]@{ RestartNeeded = $false }
}
$script:States.Clear()
$rejected = $false
try { Install-LanguageCapability 'Language.OCR~~~en-US~0.0.1.0' }
catch { $rejected = $true }
Assert $rejected 'Fail if an installation leaves a capability unavailable'

$script:Policy = [pscustomobject]@{ KeyExisted = $true; Existed = $true; Value = 0; Kind = 'DWord'; Blocked = $false; UsesWsus = $false }
$script:SourceChanges = New-Object 'Collections.Generic.List[bool]'
$script:RestoredSnapshot = $null
$script:ApproveRetry = $false
$script:Confirmations = 0
$script:InstallCalls = 0

# Override policy access and confirmation before testing the WSUS path. Never edit the real registry.
function Get-OptionalContentPolicy { return $script:Policy }
function Set-OptionalContentPolicy([object] $Snapshot, [bool] $EnableDirectDownload) {
    $script:SourceChanges.Add($EnableDirectDownload)
    if (-not $EnableDirectDownload) { $script:RestoredSnapshot = $Snapshot }
}
function Confirm-DirectDownload {
    $script:Confirmations++
    return $script:ApproveRetry
}

$result = Invoke-DirectDownloadRetry { $script:InstallCalls++ }
Assert (-not $result) 'A declined retry returns cancellation'
Assert ($script:SourceChanges.Count -eq 0 -and $script:InstallCalls -eq 0) 'No policy changes or retry without confirmation'

$script:ApproveRetry = $true
$result = Invoke-DirectDownloadRetry { $script:InstallCalls++ }
Assert $result 'An approved retry can succeed'
Assert (($script:SourceChanges -join ',') -eq 'True,False') 'Enable the optional-content source and restore it'
Assert ($script:RestoredSnapshot -eq $script:Policy) 'Restore the exact captured settings'
Assert ($script:InstallCalls -eq 1) 'Retry installation once'

$script:SourceChanges.Clear()
$rejected = $false
try { Invoke-DirectDownloadRetry { throw 'Simulated download failure' } }
catch { $rejected = $true }
Assert $rejected 'A failed retry reports the error'
Assert (($script:SourceChanges -join ',') -eq 'True,False') 'Restore source settings even when installation throws'

$script:SourceChanges.Clear()
$script:Policy.Blocked = $true
$confirmationsBefore = $script:Confirmations
$rejected = $false
try { Invoke-DirectDownloadRetry { $script:InstallCalls++ } }
catch { $rejected = $true }
Assert $rejected 'Refuse retry when policy prohibits direct downloads'
Assert ($script:Confirmations -eq $confirmationsBefore -and $script:SourceChanges.Count -eq 0) 'Do not change an explicit restriction'

$script:Policy.Blocked = $false
$script:Policy.Value = 2
$rejected = $false
try { Invoke-DirectDownloadRetry { $script:InstallCalls++ } }
catch { $rejected = $true }
Assert $rejected 'Do not loop when optional content already uses Windows Update'
Assert ($script:SourceChanges.Count -eq 0) 'Do not change an already configured source'

# The WSUS confirmation must happen before the very first installation attempt.
$script:Policy.Value = 0
$script:Policy.UsesWsus = $true
$script:ApproveRetry = $false
$script:Confirmations = 0
$script:InstallCalls = 0
function Invoke-EnvironmentInstallation([string] $RequestedComponents) { $script:InstallCalls++ }
$result = Invoke-RequestedEnvironment 'EnglishOcr'
Assert (-not $result -and $script:InstallCalls -eq 0) 'Declining WSUS preflight makes no initial DISM attempt'
Assert ($script:Confirmations -eq 1 -and $script:SourceChanges.Count -eq 0) 'Ask before changing policy'
$script:ApproveRetry = $true
$result = Invoke-RequestedEnvironment 'EnglishOcr'
Assert ($result -and $script:InstallCalls -eq 1) 'An approved WSUS installation makes one attempt'
Assert (($script:SourceChanges -join ',') -eq 'True,False') 'Restore policy after the preflight installation'
$script:SourceChanges.Clear()
$script:Policy.UsesWsus = $false
$confirmationsBefore = $script:Confirmations
$result = Invoke-RequestedEnvironment 'EnglishOcr'
Assert ($result -and $script:Confirmations -eq $confirmationsBefore) 'Normal update sources need no extra confirmation'
$script:Policy.UsesWsus = $true
$result = Invoke-RequestedEnvironment 'VisualCpp'
Assert ($result -and $script:Confirmations -eq $confirmationsBefore) 'Visual C++ does not require a language-source change'

# Exercise the real asynchronous monitoring/cancellation code with harmless PowerShell pipelines.
$script:WorkerScript = ''
function New-CapabilityWorker([string] $Name) {
    $worker = [powershell]::Create()
    $null = $worker.AddScript($script:WorkerScript)
    return $worker
}
$script:WorkerScript = 'Write-Progress -Activity "test" -Status "working" -PercentComplete 25; Start-Sleep -Milliseconds 100; [pscustomobject]@{ RestartNeeded = $false }'
$result = & $originalCapabilityOperation 'TestOnly'
Assert (-not $result.RestartNeeded) 'Read the successful result from a monitored pipeline'
$script:WorkerScript = 'throw "Simulated error 0x800f0954"'
$rejected = $false
try { & $originalCapabilityOperation 'TestOnly' }
catch { $rejected = $_.Exception.ToString() -match '0x800f0954' }
Assert $rejected 'Preserve native installation errors for source fallback'
$script:WorkerScript = 'Start-Sleep -Seconds 30'
$timer = [Diagnostics.Stopwatch]::StartNew()
$rejected = $false
$OwnerProcessId = [int]::MaxValue
try { & $originalCapabilityOperation 'TestOnly' }
catch { $rejected = $true }
finally { $OwnerProcessId = 0 }
Assert $rejected 'Closing the app cancels its installation pipeline'
Assert ($timer.Elapsed.TotalSeconds -lt 5) 'Cancel a waiting pipeline without killing a process'

Write-Host "Installer tests passed: $script:Checks"
