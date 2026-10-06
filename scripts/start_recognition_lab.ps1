$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$application = Join-Path $projectRoot 'dist/Stock.RecognitionLab.exe'
if (-not (Test-Path -LiteralPath $application)) { throw '请先运行 scripts/build_recognition_lab.ps1 构建独立测试程序。' }
Start-Process -FilePath $application -WorkingDirectory (Split-Path $application -Parent) -WindowStyle Normal
