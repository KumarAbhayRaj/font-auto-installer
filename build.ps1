# Builds app\FontAutoInstaller.exe with the C# compiler that ships inside Windows (.NET Framework 4.8).
# Nothing to install. Run:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out  = Join-Path $root 'app\FontAutoInstaller.exe'

Get-Process FontAutoInstaller -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

$sources = (Get-ChildItem (Join-Path $root 'src\*.cs')).FullName
& $csc /nologo /target:winexe /optimize+ /platform:anycpu "/out:$out" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Host "Built $out"
