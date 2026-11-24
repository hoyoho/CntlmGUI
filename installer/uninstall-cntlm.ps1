# CntlmGui 卸载伴随清理：
# - 升级时本脚本不会执行（MSI 条件里有 NOT UPGRADINGPRODUCTCODE）；
# - 主动卸载时由 MSI 传入 -Ask -UILevel [UILevel]，
#   交互式卸载弹窗询问是否同时卸载 cntlm，默认“否”；
# - 静默卸载默认保留 cntlm，可显式传 REMOVECNTLM=1 强制卸载；
# - 若选择卸载，则仅按 DisplayName 含 "cntlm" 的程序调用其自身卸载器，避免误伤其他软件。
param(
  [switch]$Ask,
  [int]$UILevel = 5,
  [string]$RemoveCntlm = ""
)

$ErrorActionPreference = "SilentlyContinue"

$doRemove = $false
if ($RemoveCntlm -eq "1") {
  $doRemove = $true
} elseif ($Ask -and $UILevel -ge 5) {
  try {
    Add-Type -AssemblyName System.Windows.Forms
    $r = [System.Windows.Forms.MessageBox]::Show(
      "是否同时卸载 cntlm？`n`n选择 '否' 将保留 cntlm，之后可单独卸载。",
      "卸载 CntlmGui",
      [System.Windows.Forms.MessageBoxButtons]::YesNo,
      [System.Windows.Forms.MessageBoxIcon]::Question,
      [System.Windows.Forms.MessageBoxDefaultButton]::Button2)
    $doRemove = ($r -eq [System.Windows.Forms.DialogResult]::Yes)
  } catch {
    $doRemove = $false
  }
}
if (-not $doRemove) { exit 0 }

$roots = @(
  "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
  "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
  "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
)

foreach ($root in $roots) {
  foreach ($key in (Get-ChildItem $root)) {
    $props = Get-ItemProperty -LiteralPath $key.PSPath
    $display = "$($props.DisplayName)"
    $uninstall = "$($props.UninstallString)"

    # 只认 DisplayName，且必须是 cntlm/cntl，避免把 unins000.exe 等误判为别家
    if ($display -notmatch "(?i)cntlm") { continue }

    # 按路径结束该包安装目录下运行中的 cntlm 进程，避免 Inno 卸载器删不掉在用文件。
    # 仅匹配 InstallLocation 前缀，不误杀用户手动运行的绿色版；
    # cygrunsrv 服务的子进程留给卸载器 --stop 优雅停止，避免服务"意外终止"日志。
    $loc = "$($props.InstallLocation)"
    if ($loc -and (Test-Path -LiteralPath $loc)) {
      $prefix = $loc.TrimEnd('\') + '\'
      foreach ($proc in (Get-Process -Name "cntlm" -ErrorAction SilentlyContinue)) {
        try {
          $path = $proc.Path
          if (-not $path -or -not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
          $parentName = ""
          $parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$($proc.Id)" -ErrorAction SilentlyContinue).ParentProcessId
          if ($parent) {
            try { $parentName = (Get-Process -Id $parent -ErrorAction Stop).ProcessName } catch { }
          }
          if ($parentName -ne "cygrunsrv") {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
          }
        } catch { }
      }
    }

    $exe = $null
    if ($uninstall -match '^\s*"([^"]+)"') {
      $exe = $matches[1]
    } elseif ($uninstall.Trim() -ne "") {
      $exe = ($uninstall.Trim() -replace '^["'']', '') -split '\s+' | Select-Object -First 1
    }
    if ($exe -and (Test-Path -LiteralPath $exe)) {
      Start-Process -FilePath $exe -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -Wait
    }
  }
}
