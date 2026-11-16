param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.0', [switch]$IncludeDemo)
$ErrorActionPreference = 'Stop'
$releaseName = "redsheet-v$Version-windows"
$releaseDir = Join-Path $PSScriptRoot "dist/$releaseName"
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
foreach ($variant in @('RedSheet.exe', 'RedSheet-Recording.exe')) {
    $outputPath = Join-Path $releaseDir $variant
    & "$PSScriptRoot/build.ps1" -OutputName $outputPath -Recording:($variant -eq 'RedSheet-Recording.exe')
    $resultPath = Join-Path $releaseDir 'self-test-result.txt'
    if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath }
    $testProcess = Start-Process -FilePath $outputPath -ArgumentList '--self-test' -WorkingDirectory $releaseDir -WindowStyle Hidden -PassThru -Wait
    if ($testProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $resultPath)) { throw "Test failed: $variant" }
    $result = Get-Content -LiteralPath $resultPath -Raw
    if (!$result.StartsWith('PASS:')) { throw "$variant : $result" }
    Write-Output "$variant : $result"
}
Copy-Item -LiteralPath "$PSScriptRoot/README.md", "$PSScriptRoot/LICENSE" -Destination $releaseDir -Force
$fileNames = @('RedSheet.exe', 'RedSheet-Recording.exe', 'README.md', 'LICENSE')
if ($IncludeDemo) {
    Copy-Item -LiteralPath "$PSScriptRoot/output/pdf/redsheet-demo.pdf" -Destination $releaseDir -Force
    $fileNames += 'redsheet-demo.pdf'
}
$files = $fileNames | ForEach-Object { Join-Path $releaseDir $_ }
$zipPath = Join-Path $PSScriptRoot "dist/$releaseName.zip"
Compress-Archive -LiteralPath $files -DestinationPath $zipPath -Force
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zipPath.sha256" -Value "$hash  $releaseName.zip" -Encoding ASCII
Write-Output "Package: $zipPath"
