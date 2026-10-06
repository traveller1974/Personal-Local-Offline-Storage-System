param([string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $projectRoot ('artifacts/v1.1/install-upgrade-'+[guid]::NewGuid().ToString('N'))}
$testRoot=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $testRoot.StartsWith($projectRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Test output must stay within the project'}
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$target=[IO.Path]::GetFullPath((Join-Path $testRoot '中文 安装目录'))
if(-not $target.StartsWith($testRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe install target'}
if(Test-Path -LiteralPath $target){throw 'Use a fresh test installation directory'}
$testId='LocalStockManager.InstallTest'
$registry='HKCU:\Software\'+$testId
if(Test-Path -LiteralPath $registry){throw 'An earlier test registration exists; resolve that test first'}
$realRegistration=Get-ItemProperty -LiteralPath 'HKCU:\Software\LocalStockManager' -ErrorAction SilentlyContinue
$realInstallPath=$realRegistration.InstallPath
$nsis=Join-Path $projectRoot 'tools/nsis/nsis-3.11/makensis.exe'
$v2publish=Join-Path $projectRoot 'artifacts/publish-v1.1.2'
$v1setup=Join-Path $projectRoot 'artifacts/v1.1/v1-test-setup.exe'
$v2setup=Join-Path $testRoot 'v1.1-test-setup.exe'
if(-not(Test-Path -LiteralPath $v1setup)){throw 'Preserve and package v1 publish before this test'}
& $nsis '/INPUTCHARSET' 'UTF8' '/XSetCompressor /FINAL /SOLID zlib' "/DPUBLISH_DIR=$v2publish" "/DOUTPUT_FILE=$v2setup" '/DAPP_VERSION=1.1.2' "/DAPP_ID=$testId" '/DSHORTCUT_LABEL=本地库存管理安装测试' (Join-Path $projectRoot 'installer/installer.nsi')
if($LASTEXITCODE -ne 0){throw 'Isolated v1.1 installer build failed'}
$checks=[Collections.Generic.List[string]]::new()
function Assert-Test([bool]$value,[string]$label){if(-not $value){throw $label};$checks.Add($label)}
function Data-Fingerprint([string]$directory){
 $files=@(Get-ChildItem -LiteralPath $directory -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
  [pscustomobject]@{path=$_.FullName.Substring($directory.Length);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
 })
 return (ConvertTo-Json -InputObject $files -Compress)
}
function Install-Test([string]$setup){$p=Start-Process -FilePath $setup -ArgumentList '/S',('/D='+$target) -WindowStyle Hidden -PassThru -Wait;Assert-Test ($p.ExitCode -eq 0) ('Installer succeeds: '+[IO.Path]::GetFileName($setup))}
$success=$false
try {
 Install-Test $v1setup
 & (Join-Path $PSScriptRoot 'verify.ps1') -ApplicationPath (Join-Path $target 'LocalStockManager.exe') -OutputDirectory (Join-Path $testRoot 'v1-smoke')
 $oldResult=Get-Content -LiteralPath (Join-Path $testRoot 'v1-smoke/desktop-results.json') -Raw -Encoding UTF8 | ConvertFrom-Json
 $oldData=[IO.Path]::GetFullPath((Join-Path $oldResult.data 'Data'))
 if(-not $oldData.StartsWith($testRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe upgrade test data path'}
 $oldDatabase=Join-Path $oldData 'stock.db'
 $storedRoot=Split-Path $oldData -Parent
 # Test-owned sentinels also cover settings and protection backups beside Data.
 Set-Content -LiteralPath (Join-Path $storedRoot 'settings-retention-test.bin') -Value 'isolated settings sentinel' -Encoding ASCII
 New-Item -ItemType Directory -Force -Path (Join-Path $storedRoot 'ProtectionBackups') | Out-Null
 Set-Content -LiteralPath (Join-Path $storedRoot 'ProtectionBackups/retention-test.stockbackup') -Value 'isolated backup sentinel' -Encoding ASCII
 $beforeHash=(Get-FileHash -LiteralPath $oldDatabase -Algorithm SHA256).Hash
 $beforeFiles=Data-Fingerprint $storedRoot
 Install-Test $v2setup
 Assert-Test ((Get-FileHash -LiteralPath $oldDatabase -Algorithm SHA256).Hash -eq $beforeHash) 'Installation upgrade leaves existing v1 database untouched'
 Assert-Test ((Data-Fingerprint $storedRoot) -eq $beforeFiles) 'Installation preserves every existing database, photo, settings and backup file byte for byte'
 $p=Start-Process -FilePath (Join-Path $target 'LocalStockManager.exe') -ArgumentList '--upgrade-test',('"'+$oldData+'"') -WindowStyle Hidden -PassThru -Wait
 Assert-Test ($p.ExitCode -eq 0) 'Installed v1.1 application migrates real v1 application data'
 $upgrade=Get-Content -LiteralPath (Join-Path $oldData 'upgrade-results.json') -Raw -Encoding UTF8 | ConvertFrom-Json
 Assert-Test ($upgrade.success -and $upgrade.before -eq $upgrade.after -and $upgrade.afterVersion -eq 2) 'Migration keeps balances and marks legacy identities incomplete'
 & (Join-Path $PSScriptRoot 'verify.ps1') -ApplicationPath (Join-Path $target 'LocalStockManager.exe') -OutputDirectory (Join-Path $testRoot 'v1.1-smoke')
 $afterHash=(Get-FileHash -LiteralPath $oldDatabase -Algorithm SHA256).Hash
 $afterFiles=Data-Fingerprint $storedRoot
 $p=Start-Process -FilePath (Join-Path $target 'Uninstall.exe') -ArgumentList '/S' -WindowStyle Hidden -PassThru -Wait
 $deadline=[DateTime]::UtcNow.AddSeconds(45)
 while((Test-Path -LiteralPath (Join-Path $target 'LocalStockManager.exe')) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 300}
 Assert-Test (-not(Test-Path -LiteralPath (Join-Path $target 'LocalStockManager.exe'))) 'Uninstall removes the isolated application'
 Assert-Test ((Get-FileHash -LiteralPath $oldDatabase -Algorithm SHA256).Hash -eq $afterHash) 'Uninstall preserves migrated database and photos'
 Assert-Test ((Data-Fingerprint $storedRoot) -eq $afterFiles) 'Uninstall preserves every stored file byte for byte'
 Assert-Test (-not(Test-Path -LiteralPath $registry)) 'Test installer registration is removed'
 $currentRegistration=Get-ItemProperty -LiteralPath 'HKCU:\Software\LocalStockManager' -ErrorAction SilentlyContinue
 Assert-Test ($currentRegistration.InstallPath -eq $realInstallPath) 'Real installation registration remains unchanged'
 $success=$true
}
finally {
 [ordered]@{success=$success;checks=@($checks);testDirectory=$testRoot;realInstallationPreserved=$true;cleanMachineVerified=$false} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $projectRoot 'artifacts/v1.1/install-upgrade-results.json') -Encoding UTF8
}
