param([string]$DownloadProxy='')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$destination=Join-Path $projectRoot 'licenses/upstream'
New-Item -ItemType Directory -Force -Path $destination,(Join-Path $projectRoot 'licenses/sources') | Out-Null
$sources=@(
 @{Name='RapidOCR-LICENSE';Url='https://raw.githubusercontent.com/RapidAI/RapidOCR/main/LICENSE'},
 @{Name='PaddleOCR-LICENSE';Url='https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/main/LICENSE'},
 @{Name='OpenCvSharp-LICENSE';Url='https://raw.githubusercontent.com/shimat/opencvsharp/master/LICENSE'},
 @{Name='OpenCV-LICENSE';Url='https://raw.githubusercontent.com/opencv/opencv/4.10.0/LICENSE'},
 @{Name='ClosedXML-LICENSE';Url='https://raw.githubusercontent.com/ClosedXML/ClosedXML/d15f6690886801980c0d83b7eb1d1b5d2171ad31/LICENSE'},
 @{Name='ClosedXML.Parser-LICENSE';Url='https://raw.githubusercontent.com/ClosedXML/ClosedXML.Parser/658973aeedc2fe289e0ccba2bb9959f67692ded5/LICENSE.txt'},
 @{Name='ExcelNumberFormat-LICENSE';Url='https://raw.githubusercontent.com/andersnm/ExcelNumberFormat/master/LICENSE'},
 @{Name='RBush-LICENSE';Url='https://raw.githubusercontent.com/viceroypenguin/RBush/b8690322abac35d835baa2fdca4a3918ed77a910/LICENSE'},
 @{Name='SixLabors.Fonts-LICENSE';Url='https://raw.githubusercontent.com/SixLabors/Fonts/32bef42997adb10268369ca149777f00e4241ce9/LICENSE'},
 @{Name='SQLitePCLRaw-LICENSE';Url='https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/master/LICENSE.TXT'},
 @{Name='OpenXML-LICENSE';Url='https://raw.githubusercontent.com/dotnet/Open-XML-SDK/v3.1.1/LICENSE'},
 @{Name='VisualCpp-runtime-license.html';Url='https://visualstudio.microsoft.com/license-terms/vs2022-cruntime/'},
 @{Name='../sources/geos-3.13.1.tar.bz2';Url='https://download.osgeo.org/geos/geos-3.13.1.tar.bz2'}
)
$records=@()
foreach($source in $sources){
 $target=[IO.Path]::GetFullPath((Join-Path $destination $source.Name))
 if(-not(Test-Path -LiteralPath $target)){
  $fetchArgs=@('-fL','--silent','--show-error','--connect-timeout','20','--max-time','90','-o',($target+'.part'))
  if($DownloadProxy){$fetchArgs=@('--proxy',$DownloadProxy)+$fetchArgs}
  & curl.exe @fetchArgs $source.Url
  if($LASTEXITCODE -ne 0){throw "License download failed: $($source.Url)"}
  Move-Item -LiteralPath ($target+'.part') -Destination $target
 }
 $records+=@{file=[IO.Path]::GetRelativePath((Join-Path $projectRoot 'licenses'),$target);url=$source.Url;sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$records | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $projectRoot 'licenses/source-downloads.json') -Encoding utf8
Write-Output 'License sources downloaded and hashed.'
