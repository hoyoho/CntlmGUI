# CntlmGui MSI build script
# Requires: .NET SDK 8 + WiX 3.14 (candle.exe / light.exe)
$ErrorActionPreference = "Stop"

function Find-WixDir {
    if ($env:WIXTOOLS) { return $env:WIXTOOLS }
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Tauri\WixTools314"),
        "C:\Program Files (x86)\WiX Toolset v3.14",
        "C:\Program Files\WiX Toolset v3.14"
    )
    foreach ($dir in $candidates) {
        if (Test-Path (Join-Path $dir "candle.exe")) { return $dir }
    }
    throw "WiX toolset not found. Set WIXTOOLS to the folder containing candle.exe and light.exe."
}

$WixDir   = Find-WixDir
$Candle   = Join-Path $WixDir "candle.exe"
$Light    = Join-Path $WixDir "light.exe"
$WixExt   = Join-Path $WixDir "WixUIExtension.dll"
$Root     = Split-Path $PSScriptRoot -Parent
$Publish  = Join-Path $Root "build\publish"
$OutDir   = Join-Path $Root "build"
$Proj     = Join-Path $Root "src\CntlmGui.csproj"

# WiX resolves relative paths in the .wxs against the current directory.
Set-Location -LiteralPath $PSScriptRoot

# 1) Publish framework-dependent build
& dotnet publish $Proj -c Release -r win-x64 --self-contained false -o $Publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }

# 2) Compile wxs -> wixobj
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$wixObj = Join-Path $OutDir "CntlmGui.wixobj"
& $Candle -nologo -arch x64 -ext $WixExt -out $wixObj (Join-Path $PSScriptRoot "CntlmGui.wxs")
if ($LASTEXITCODE -ne 0) { throw "candle failed: $LASTEXITCODE" }

# 3) Link -> MSI
$msi = Join-Path $OutDir "CntlmGui.msi"
& $Light -nologo -ext $WixExt -cultures:en-US -out $msi $wixObj
if ($LASTEXITCODE -ne 0) { throw "light failed: $LASTEXITCODE" }

Write-Host "Built: $msi" -ForegroundColor Green
