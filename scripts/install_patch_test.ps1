param([Parameter(Mandatory=$true)][string]$BaselinePublish,[string]$BaselineVersion='1.1.1',
 [string]$TargetVersion='1.1.2',[string]$TargetPublish='',[string]$OutputDirectory='',[switch]$EmbeddedRecognition,[switch]$CleanupPreviousTest)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
foreach($version in @($BaselineVersion,$TargetVersion)){if($version -notmatch '^\d+\.\d+\.\d+$'){throw 'Versions must use major.minor.patch'}}
if(-not $TargetPublish){$TargetPublish=Join-Path $projectRoot ('artifacts/publish-v'+$TargetVersion)}
if(-not $OutputDirectory){
 # Windows process working directories remain limited even when managed IO accepts long paths.
 $testName=if($EmbeddedRecognition){'i-'+[guid]::NewGuid().ToString('N').Substring(0,8)}else{'install-patch-'+[guid]::NewGuid().ToString('N')}
 $OutputDirectory=Join-Path $projectRoot ('artifacts/v'+$TargetVersion+'/'+$testName)
}
$testRoot=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $testRoot.StartsWith($projectRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Test output must stay within the project'}
if(Test-Path -LiteralPath $testRoot){throw 'Use a fresh test directory'}
$baseline=[IO.Path]::GetFullPath($BaselinePublish)
if(-not(Test-Path -LiteralPath (Join-Path $baseline 'LocalStockManager.exe'))){throw 'A published baseline is required'}
$baselineProductVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $baseline 'LocalStockManager.dll')).ProductVersion
if($baselineProductVersion.Split('+')[0] -ne $BaselineVersion){throw 'Baseline version does not match the requested version'}
$targetPublishPath=[IO.Path]::GetFullPath($TargetPublish)
if(-not(Test-Path -LiteralPath (Join-Path $targetPublishPath 'LocalStockManager.exe'))){throw 'A published target is required'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $targetPublishPath 'LocalStockManager.dll')).ProductVersion.Split('+')[0] -ne $TargetVersion){throw 'Target version does not match the requested version'}
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$target=[IO.Path]::GetFullPath((Join-Path $testRoot '中文 安装目录'))
if(-not $target.StartsWith($testRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe test installation path'}
$testId='LocalStockManager.PatchInstallTest'
$registry='HKCU:\Software\'+$testId
if(Test-Path -LiteralPath $registry){
 if(-not $CleanupPreviousTest){throw 'An earlier patch test is registered; resolve that test first'}
 $previousResult=Get-Content -Raw -LiteralPath (Join-Path $projectRoot ('artifacts/v'+$TargetVersion+'/install-patch-results.json')) | ConvertFrom-Json
 $previousRoot=[IO.Path]::GetFullPath($previousResult.testDirectory)
 $previousTarget=[IO.Path]::GetFullPath((Join-Path $previousRoot '中文 安装目录'))
 $registeredTarget=[IO.Path]::GetFullPath((Get-ItemProperty -LiteralPath $registry).InstallPath)
 $allowedPreviousRoot=[IO.Path]::GetFullPath((Join-Path $projectRoot ('artifacts/v'+$TargetVersion)))
 if($previousResult.success -or $previousResult.targetVersion -ne $TargetVersion -or
    -not $previousRoot.StartsWith($allowedPreviousRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
    $registeredTarget -ne $previousTarget -or
    (Get-Content -Raw -LiteralPath (Join-Path $previousTarget 'installed.marker')).Trim() -ne 'LocalStockManager-1b2162c4-34c7-4c1c-b124-550f11084150'){
  throw 'Previous test registration does not match a known failed isolated installation'
 }
 $cleanup=Start-Process -FilePath (Join-Path $previousTarget 'Uninstall.exe') -ArgumentList '/S' -WindowStyle Hidden -PassThru -Wait
 $cleanupDeadline=[DateTime]::UtcNow.AddSeconds(45)
 while((Test-Path -LiteralPath $registry) -and [DateTime]::UtcNow -lt $cleanupDeadline){Start-Sleep -Milliseconds 300}
 if($cleanup.ExitCode -ne 0 -or (Test-Path -LiteralPath $registry)){throw 'Previous isolated test could not be cleaned up'}
}
$realBefore=Get-ItemProperty -LiteralPath 'HKCU:\Software\LocalStockManager' -ErrorAction SilentlyContinue
$realInstallPath=$realBefore.InstallPath
$nsis=Join-Path $projectRoot 'tools/nsis/nsis-3.11/makensis.exe'
$checks=[Collections.Generic.List[string]]::new()
function Assert-Test([bool]$value,[string]$label){if(-not $value){throw $label};$checks.Add($label)}
function Fingerprint([string]$directory){
 $files=@(Get-ChildItem -LiteralPath $directory -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
  [pscustomobject]@{path=$_.FullName.Substring($directory.Length);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
 })
 return (ConvertTo-Json -InputObject $files -Compress)
}
function Package([string]$publish,[string]$version,[string]$output){
 & $nsis '/INPUTCHARSET' 'UTF8' '/XSetCompressor /FINAL /SOLID zlib' "/DPUBLISH_DIR=$publish" "/DOUTPUT_FILE=$output" "/DAPP_VERSION=$version" "/DAPP_ID=$testId" '/DSHORTCUT_LABEL=本地库存管理补丁安装测试' (Join-Path $projectRoot 'installer/installer.nsi')
 if($LASTEXITCODE -ne 0){throw 'Isolated installer packaging failed'}
}
function Install([string]$setup){$process=Start-Process -FilePath $setup -ArgumentList '/S',('/D='+$target) -WindowStyle Hidden -PassThru -Wait;Assert-Test ($process.ExitCode -eq 0) ('Installer succeeds: '+[IO.Path]::GetFileName($setup))}
$oldSetup=Join-Path $testRoot ('v'+$BaselineVersion+'-test-setup.exe')
$newSetup=Join-Path $testRoot ('v'+$TargetVersion+'-test-setup.exe')
Package $baseline $BaselineVersion $oldSetup
Package $targetPublishPath $TargetVersion $newSetup
$success=$false;$storedFileCount=0
try {
 Install $oldSetup
 & (Join-Path $PSScriptRoot 'verify.ps1') -ApplicationPath (Join-Path $target 'LocalStockManager.exe') -OutputDirectory (Join-Path $testRoot 'baseline-smoke')
 $oldResult=Get-Content -LiteralPath (Join-Path $testRoot 'baseline-smoke/desktop-results.json') -Raw -Encoding UTF8 | ConvertFrom-Json
 $storedRoot=[IO.Path]::GetFullPath($oldResult.data)
 if(-not $storedRoot.StartsWith($testRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe stored data directory'}
 $oldData=Join-Path $storedRoot 'Data'
 $retainedPhoto=Join-Path $oldData 'Photos'
 New-Item -ItemType Directory -Force -Path $retainedPhoto,(Join-Path $storedRoot 'ProtectionBackups') | Out-Null
 Copy-Item -LiteralPath (Join-Path $target 'Samples/示例货单.png') -Destination (Join-Path $retainedPhoto 'retention-test.png')
 Set-Content -LiteralPath (Join-Path $storedRoot 'ProtectionBackups/retention-test.stockbackup') -Value 'isolated backup sentinel' -Encoding ASCII
 # Auxiliary settings are opaque to the installer, including settings introduced after the baseline.
 $settingsRoot=Join-Path $storedRoot 'Settings'
 New-Item -ItemType Directory -Force -Path $settingsRoot | Out-Null
 Set-Content -LiteralPath (Join-Path $settingsRoot 'deepseek.dpapi') -Value 'isolated encrypted-setting sentinel' -Encoding ASCII
 Set-Content -LiteralPath (Join-Path $settingsRoot 'recognition-provider.txt') -Value 'DeepSeek' -Encoding ASCII
 Copy-Item -LiteralPath (Join-Path $oldData 'stock.db') -Destination (Join-Path $oldData 'recognition-memory.db')
 $before=Fingerprint $storedRoot
 $storedFileCount=@(Get-ChildItem -LiteralPath $storedRoot -File -Recurse -Force).Count
 $beforeDatabase=(Get-FileHash -LiteralPath (Join-Path $oldData 'stock.db') -Algorithm SHA256).Hash
 Install $newSetup
 Assert-Test ((Get-FileHash -LiteralPath (Join-Path $oldData 'stock.db') -Algorithm SHA256).Hash -eq $beforeDatabase) 'Patch installation leaves the existing v2 database unchanged before application startup'
 Assert-Test ((Fingerprint $storedRoot) -eq $before) 'Patch installation preserves every stored database, photo, DPAPI setting and backup byte for byte'
 Assert-Test ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $target 'LocalStockManager.dll')).ProductVersion.Split('+')[0] -eq $TargetVersion) ('Installed application reports version '+$TargetVersion)
 & (Join-Path $PSScriptRoot 'verify.ps1') -ApplicationPath (Join-Path $target 'LocalStockManager.exe') -OutputDirectory (Join-Path $testRoot 'patched-smoke') -RecognitionIntegration:$EmbeddedRecognition
 Assert-Test ((Fingerprint $storedRoot) -eq $before) 'Independent installed regression checks do not touch existing baseline data'
 Install $newSetup
 Assert-Test ((Fingerprint $storedRoot) -eq $before) 'Reinstalling the patch preserves every previously stored file'
 $process=Start-Process -FilePath (Join-Path $target 'Uninstall.exe') -ArgumentList '/S' -WindowStyle Hidden -PassThru -Wait
 $deadline=[DateTime]::UtcNow.AddSeconds(45)
 while((Test-Path -LiteralPath (Join-Path $target 'LocalStockManager.exe')) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 300}
 Assert-Test (-not(Test-Path -LiteralPath (Join-Path $target 'LocalStockManager.exe'))) 'Uninstall removes the isolated application'
 Assert-Test ((Fingerprint $storedRoot) -eq $before) 'Uninstall preserves every stored file byte for byte'
 Assert-Test (-not(Test-Path -LiteralPath $registry)) 'Patch test registration is removed'
 $realAfter=Get-ItemProperty -LiteralPath 'HKCU:\Software\LocalStockManager' -ErrorAction SilentlyContinue
 Assert-Test ($realAfter.InstallPath -eq $realInstallPath) 'Real installation registration remains unchanged'
 $success=$true
}
finally {
 [ordered]@{success=$success;checks=@($checks);storedFileCount=$storedFileCount;baselineVersion=$BaselineVersion;targetVersion=$TargetVersion;testDirectory=$testRoot;realInstallationPreserved=$success;cleanMachineVerified=$false} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $projectRoot ('artifacts/v'+$TargetVersion+'/install-patch-results.json')) -Encoding UTF8
}
Write-Output "Patch installation checks passed: $($checks.Count)"
