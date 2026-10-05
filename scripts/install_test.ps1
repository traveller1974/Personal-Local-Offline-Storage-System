$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$target=[IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/installer-test/中文 安装目录'))
if(-not $target.StartsWith($projectRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe installation test target'}
$installer=Join-Path $projectRoot 'dist/LocalStockManager-1.0.0-win-x64-Setup.exe'
$registry='HKCU:\Software\LocalStockManager'
if(Test-Path -LiteralPath $registry){throw 'A real installation is registered. Test aborted to preserve it.'}
if(Test-Path -LiteralPath $target){throw 'Test directory already exists. Use a fresh verified test target.'}
$data=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'LocalStockManager/Data'
New-Item -ItemType Directory -Force -Path $data | Out-Null
$sentinel=Join-Path $data ('installer-retention-'+[guid]::NewGuid().ToString('N')+'.txt')
$sentinelText=[guid]::NewGuid().ToString('N')
Set-Content -LiteralPath $sentinel -Value $sentinelText -Encoding utf8
$checks=[Collections.Generic.List[string]]::new()
$success=$false
$taskPathBefore=$env:PATH
$taskDotnetRootBefore=$env:DOTNET_ROOT
function Assert-Test([bool]$Condition,[string]$Label){if(-not $Condition){throw $Label};$checks.Add($Label)}
function Install-Test {
 $process=Start-Process -FilePath $installer -ArgumentList '/S',('/D='+$target) -WindowStyle Hidden -PassThru -Wait
 Assert-Test ($process.ExitCode -eq 0) 'Silent NSIS installation returns success'
 Assert-Test (Test-Path -LiteralPath (Join-Path $target 'LocalStockManager.exe')) 'Application exists in Chinese and space-containing installation directory'
 Assert-Test (Test-Path -LiteralPath (Join-Path $target 'Ocr/StockOcr.exe')) 'Offline OCR worker is installed'
 Assert-Test (Test-Path -LiteralPath (Join-Path $target 'licenses/sources/geos-3.13.1.tar.bz2')) 'Third-party notices and corresponding GEOS source are installed'
}
try {
 Install-Test
 $env:PATH=([Environment]::GetFolderPath('Windows'))+'\System32;'+[Environment]::GetFolderPath('Windows')
 $env:DOTNET_ROOT=Join-Path $projectRoot 'artifacts/no-dotnet-sdk'
 & (Join-Path $PSScriptRoot 'verify.ps1') -ApplicationPath (Join-Path $target 'LocalStockManager.exe') -OutputDirectory (Join-Path $projectRoot 'artifacts/installed-smoke')
 $checks.Add('Installed self-contained application and real OCR pass with developer tools removed from PATH')
 Install-Test
 Assert-Test ((Get-Content -LiteralPath $sentinel -Raw).Trim() -eq $sentinelText) 'Reinstallation upgrade preserves application data'
 $uninstall=Join-Path $target 'Uninstall.exe'
 $process=Start-Process -FilePath $uninstall -ArgumentList '/S' -WindowStyle Hidden -PassThru -Wait
 $deadline=[DateTime]::UtcNow.AddSeconds(45)
 while((Test-Path -LiteralPath (Join-Path $target 'LocalStockManager.exe')) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 300}
 Assert-Test (-not(Test-Path -LiteralPath (Join-Path $target 'LocalStockManager.exe'))) 'Uninstallation removes the application'
 Assert-Test ((Get-Content -LiteralPath $sentinel -Raw).Trim() -eq $sentinelText) 'Uninstallation retains application data'
 Assert-Test (-not(Test-Path -LiteralPath $registry)) 'Uninstallation removes installation registration'
 $success=$true
}
finally {
 $env:PATH=$taskPathBefore
 $env:DOTNET_ROOT=$taskDotnetRootBefore
 # Delete only the unique test-owned sentinel, never the user's data directory.
 if(Test-Path -LiteralPath $sentinel){Remove-Item -LiteralPath $sentinel}
 [ordered]@{success=$success;checks=@($checks);installDirectory=$target;dataDirectory=$data;cleanMachineVerified=$false} | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 (Join-Path $projectRoot 'artifacts/installer-test-results.json')
}
Write-Output "Installation checks passed: $($checks.Count)"
