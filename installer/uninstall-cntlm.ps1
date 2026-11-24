[CmdletBinding()]
param(
    [switch]$Ask,
    [int]$UILevel = 5,
    [string]$RemoveCntlm = ""
)

$ErrorActionPreference = "SilentlyContinue"

function Write-Step([string]$Message) {
    Write-Host "[CntlmGUI] $Message"
}

function Test-Truthy([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    return @("1", "yes", "true", "y") -contains $Value.Trim().ToLowerInvariant()
}

function Get-CntlmUninstallEntries {
    $roots = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
    )

    $result = @()
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        foreach ($item in Get-ChildItem -Path $root -ErrorAction SilentlyContinue) {
            $props = Get-ItemProperty -Path $item.PSPath -ErrorAction SilentlyContinue
            if ($null -eq $props) { continue }

            $displayName = [string]$props.DisplayName
            if ([string]::IsNullOrWhiteSpace($displayName)) { continue }
            if ($displayName -notmatch "cntlm") { continue }
            if ($displayName -match "CntlmGUI") { continue }

            $command = [string]$props.QuietUninstallString
            if ([string]::IsNullOrWhiteSpace($command)) { $command = [string]$props.UninstallString }
            if ([string]::IsNullOrWhiteSpace($command)) { continue }

            $result += [pscustomobject]@{
                DisplayName     = $displayName
                Command         = $command.Trim()
                InstallLocation = [string]$props.InstallLocation
            }
        }
    }

    return $result
}

function Split-UninstallCommand([string]$Command) {
    if ([string]::IsNullOrWhiteSpace($Command)) { return $null }

    $exe = ""
    $tail = ""
    $command = $Command.Trim()

    if ($command.StartsWith('"')) {
        $end = $command.IndexOf('"', 1)
        if ($end -gt 0) {
            $exe = $command.Substring(1, $end - 1)
            if ($end + 1 -lt $command.Length) { $tail = $command.Substring($end + 1).Trim() }
        }
    }
    elseif ($command -match '^(?<exe>.+?\.exe)\b(?<tail>.*)$') {
        $exe = $matches['exe'].Trim()
        $tail = $matches['tail'].Trim()
    }
    else {
        $parts = $command -split '\s+', 2
        $exe = $parts[0]
        if ($parts.Count -gt 1) { $tail = $parts[1] }
    }

    if ([string]::IsNullOrWhiteSpace($exe)) { return $null }
    return [pscustomobject]@{ Exe = $exe; Tail = $tail }
}

function Stop-CntlmProcesses([string]$InstallLocation) {
    if ([string]::IsNullOrWhiteSpace($InstallLocation)) { return }
    $root = $InstallLocation.Trim().TrimEnd('\')
    if ($root.Length -eq 0) { return }

    $processes = Get-CimInstance Win32_Process -Filter "Name = 'cntlm.exe'"
    foreach ($process in $processes) {
        $path = [string]$process.ExecutablePath
        if ([string]::IsNullOrWhiteSpace($path)) { continue }
        if (-not $path.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) { continue }

        $parent = Get-CimInstance Win32_Process -Filter "ProcessId = $($process.ParentProcessId)"
        if ($parent -and [string]$parent.Name -match "cygrunsrv") {
            Write-Step "cntlm 由 cygrunsrv 托管，交给卸载器停止。"
            continue
        }

        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        Write-Step "已结束 cntlm 进程 $($process.ProcessId)。"
    }
}

function Invoke-CntlmUninstaller([string]$Command) {
    $target = Split-UninstallCommand -Command $Command
    if ($null -eq $target) { return }

    $name = [System.IO.Path]::GetFileName($target.Exe).ToLowerInvariant()
    if ($name -eq "msiexec.exe") {
        $arguments = ($target.Tail + " /qn /norestart").Trim()
    }
    else {
        $arguments = ($target.Tail + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART").Trim()
    }

    Write-Step "正在卸载 cntlm：$($target.Exe) $arguments"
    Start-Process -FilePath $target.Exe -ArgumentList $arguments -Wait -NoNewWindow
}

$entries = @(Get-CntlmUninstallEntries)
if ($entries.Count -eq 0) {
    Write-Step "未检测到 cntlm 安装，跳过。"
    exit 0
}

$shouldRemove = $false
if (Test-Truthy -Value $RemoveCntlm) {
    $shouldRemove = $true
}
elseif ($Ask -and $UILevel -ge 5) {
    Add-Type -AssemblyName System.Windows.Forms
    $names = ($entries | ForEach-Object { $_.DisplayName }) -join "、"
    $answer = [System.Windows.Forms.MessageBox]::Show(
        "检测到已安装的 cntlm：$names`n`n是否同时卸载 cntlm？",
        "卸载 CntlmGUI",
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Question,
        [System.Windows.Forms.MessageBoxDefaultButton]::Button2)
    $shouldRemove = ($answer -eq [System.Windows.Forms.DialogResult]::Yes)
}

if (-not $shouldRemove) {
    Write-Step "已保留 cntlm。"
    exit 0
}

foreach ($entry in $entries) {
    Stop-CntlmProcesses -InstallLocation $entry.InstallLocation
    Invoke-CntlmUninstaller -Command $entry.Command
}

Write-Step "cntlm 卸载完成。"
exit 0
