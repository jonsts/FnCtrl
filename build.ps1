# Builds FnCtrl.exe using the C# compiler that ships with Windows.
# No SDK, no NuGet, no toolchain install required.
param([string]$OutDir = "$PSScriptRoot\build")

$ErrorActionPreference = 'Stop'

$csc = Get-ChildItem 'C:\Windows\Microsoft.NET\Framework64\v*\csc.exe',
                     'C:\Windows\Microsoft.NET\Framework\v*\csc.exe' -ErrorAction SilentlyContinue |
       Sort-Object FullName -Descending | Select-Object -First 1

if (-not $csc) {
    throw "Could not find csc.exe. The .NET Framework 4.x compiler ships with Windows; " +
          "if it is missing, enable '.NET Framework 3.5/4.x' in Windows Features."
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$exe = Join-Path $OutDir 'FnCtrl.exe'

& $csc.FullName /nologo /target:winexe /optimize+ /out:$exe `
    /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "$PSScriptRoot\src\FnCtrl.cs"

if ($LASTEXITCODE -ne 0) { throw "Compilation failed (exit $LASTEXITCODE)." }
Write-Host "Built $exe" -ForegroundColor Green
