# Compile and run synthetic Framework tests; no real engine is loaded or launched.
[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$root=Split-Path -Parent $PSScriptRoot
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root 'artifacts\framework-regression'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
[void](New-Item -ItemType Directory -Path $OutputDirectory -Force)
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$hostExe=Join-Path $OutputDirectory 'EraBridgeHost.exe'
$testExe=Join-Path $OutputDirectory 'framework-regression.exe'
& $csc /nologo /target:exe /platform:x86 ('/out:'+$hostExe) /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll (Join-Path $root 'bridge\EraBridgeHost.cs')
if($LASTEXITCODE -ne 0){throw 'Framework host compilation failed'}
& $csc /nologo /target:exe /platform:x86 ('/out:'+$testExe) /reference:System.Windows.Forms.dll /reference:System.Drawing.dll (Join-Path $PSScriptRoot 'framework-regression.cs')
if($LASTEXITCODE -ne 0){throw 'Synthetic test compilation failed'}
& $testExe $hostExe $OutputDirectory
if($LASTEXITCODE -ne 0){throw 'Synthetic Framework regression failed'}
