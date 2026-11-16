param([string]$OutputName = 'akashi.exe', [switch]$Recording)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sourceFile = Join-Path $PSScriptRoot 'Akashi.cs'
$outputFile = if ([IO.Path]::IsPathRooted($OutputName)) { $OutputName } else { Join-Path $PSScriptRoot $OutputName }
$defines = if ($Recording) { '/define:RECORDING_BUILD' } else { '/define:NORMAL_BUILD' }
& $compiler /nologo /target:winexe /optimize+ /unsafe+ $defines "/out:$outputFile" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.Linq.dll $sourceFile
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
