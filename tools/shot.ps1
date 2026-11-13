# Renders one page of TimeTrace (with synthetic demo data in a throw-away data folder) to a PNG. Used for docs and visual checks.
# usage: tools\shot.ps1 -Page Dashboard -Name dashboard [-Preset 2] [-Exe path\to\TimeTrace.exe]
param(
    [Parameter(Mandatory)][string]$Page,
    [Parameter(Mandatory)][string]$Name,
    [int]$Preset = 2,
    [string]$Exe = "$PSScriptRoot\..\src\TimeTrace.App\bin\Debug\net10.0-windows\TimeTrace.exe",
    [string]$OutDir = "$PSScriptRoot\..\artifacts\shots"
)
New-Item -ItemType Directory -Force $OutDir | Out-Null
$data = Join-Path $env:TEMP ("tt_demo_" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$out = Join-Path (Resolve-Path $OutDir) "$Name.png"
$p = Start-Process -FilePath (Resolve-Path $Exe) -ArgumentList "--data-dir", "`"$data`"", "--demo-data", "--page", $Page, "--preset", "$Preset", "--screenshot", "`"$out`"" -PassThru
if (-not $p.WaitForExit(40000)) { $p.Kill(); Write-Error "timed out" }
Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
Write-Host $out
