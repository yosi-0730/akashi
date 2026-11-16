$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sourceFile = Join-Path $PSScriptRoot 'RedSheet.cs'
$outputFile = Join-Path $PSScriptRoot 'RedSheet.exe'
& $compiler /nologo /target:winexe /optimize+ "/out:$outputFile" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.Linq.dll $sourceFile
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
