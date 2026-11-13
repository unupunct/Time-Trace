# Builds everything: tests -> single-file self-contained exe -> Inno Setup installer -> release\ folder.
# usage: powershell -File tools\build-release.ps1 [-SkipTests]
param([switch]$SkipTests)
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
Set-Location $root
$version = "1.0.0"

if (-not $SkipTests) {
    dotnet test tests\TimeTrace.Tests -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "tests failed" }
}

$pub = Join-Path $root "artifacts\publish"
if (Test-Path $pub) { Remove-Item -Recurse -Force $pub }
dotnet publish src\TimeTrace.App -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false -o $pub --nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

New-Item -ItemType Directory -Force release | Out-Null
Copy-Item "$pub\TimeTrace.exe" release\TimeTrace.exe -Force
Copy-Item LICENSE.txt release\LICENSE.txt -Force

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found" }
& $iscc "/DAppVersion=$version" installer\TimeTrace.iss
if ($LASTEXITCODE -ne 0) { throw "installer build failed" }

Get-ChildItem release | Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 2) } }
