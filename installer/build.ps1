[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$installerDir = $PSScriptRoot
$root = Split-Path -Parent $installerDir
$sourceProject = Join-Path $root "src\CntlmGui.csproj"
$sourceDir = Join-Path $root "src"
$vendorDir = Join-Path $root "vendor"
$buildDir = Join-Path $root "build"
$publishDir = Join-Path $buildDir "publish"
$wxsPath = Join-Path $installerDir "CntlmGui.wxs"
$localePath = Join-Path $installerDir "en-us.wxl"
$wixobjPath = Join-Path $buildDir "CntlmGui.wixobj"
$msiPath = Join-Path $buildDir "CntlmGui.msi"

function Find-WixTools {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($env:WIXTOOLS)) { $candidates += $env:WIXTOOLS }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $candidates += (Join-Path $env:LOCALAPPDATA "Tauri\WixTools314")
    }
    $candidates += "C:\Program Files (x86)\WiX Toolset v3.14"
    $candidates += "C:\Program Files\WiX Toolset v3.14"

    foreach ($dir in $candidates) {
        if ([string]::IsNullOrWhiteSpace($dir)) { continue }
        if (-not (Test-Path $dir)) { continue }
        $candle = Join-Path $dir "candle.exe"
        $light = Join-Path $dir "light.exe"
        if ((Test-Path $candle) -and (Test-Path $light)) { return $dir }
    }

    throw "未找到 WiX Toolset 3.14。请安装 WiX 3.14，或设置环境变量 WIXTOOLS 指向包含 candle.exe / light.exe 的目录。"
}

function Invoke-Tool {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Description
    )

    Write-Host "==> $Description"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description 失败（退出码 $LASTEXITCODE）。"
    }
}

$wixDir = Find-WixTools
$candle = Join-Path $wixDir "candle.exe"
$light = Join-Path $wixDir "light.exe"
Write-Host "使用 WiX：$wixDir"

$vendorExe = Join-Path $vendorDir "cntlm-0.94.0-win64.exe"
if (-not (Test-Path $vendorExe)) {
    throw "缺少 vendor\cntlm-0.94.0-win64.exe。请把 cntlm 0.94.0 安装包放到 vendor 目录后重试。"
}

New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
}

Invoke-Tool -FilePath "dotnet" -Description "发布 CntlmGUI" -Arguments @(
    "publish", $sourceProject,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "false",
    "-o", $publishDir
)

foreach ($name in @("CntlmGUI.exe", "CntlmGUI.dll", "CntlmGUI.deps.json", "CntlmGUI.runtimeconfig.json")) {
    $file = Join-Path $publishDir $name
    if (-not (Test-Path $file)) { throw "发布输出缺少 $name。" }
}

Invoke-Tool -FilePath $candle -Description "编译安装包定义" -Arguments @(
    "-nologo",
    "-arch", "x64",
    "-ext", "WixUIExtension.dll",
    "-dPublishDir=$publishDir",
    "-dVendorDir=$vendorDir",
    "-dSourceDir=$sourceDir",
    "-dInstallerDir=$installerDir",
    "-out", $wixobjPath,
    $wxsPath
)

Invoke-Tool -FilePath $light -Description "链接 MSI" -Arguments @(
    "-nologo",
    "-ext", "WixUIExtension.dll",
    "-cultures:en-US",
    "-loc", $localePath,
    "-out", $msiPath,
    $wixobjPath
)

Write-Host "已生成 $msiPath"
