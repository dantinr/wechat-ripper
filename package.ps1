[CmdletBinding()]
param([switch]$Standalone)

$ErrorActionPreference = 'Stop'
$wechatNotesPackageRoot = Join-Path $PSScriptRoot 'artifacts\packages'
$wechatNotesStage = Join-Path $wechatNotesPackageRoot ('staging-' + [Guid]::NewGuid().ToString('N'))
$wechatNotesApp = Join-Path $wechatNotesStage 'Ripper'
$wechatNotesVersion = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'VERSION') -Raw).Trim()
if ($wechatNotesVersion -notmatch '^\d+\.\d+\.\d+([-.][A-Za-z0-9.]+)?$') { throw '项目版本号无效。' }
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $wechatNotesApp -SkipShortcut -Standalone:$Standalone
$wechatNotesEntry = if ($Standalone) { 'Ripper.exe' } else { 'Ripper.dll' }
$wechatNotesReportName = if ($Standalone) { 'raw-tests-standalone.json' } else { 'raw-tests.json' }
& (Join-Path $PSScriptRoot 'test.ps1') -Application (Join-Path $wechatNotesApp $wechatNotesEntry) -Report (Join-Path $wechatNotesPackageRoot $wechatNotesReportName)

# Fresh staging only: never zip an existing user's installation or data directory.
$wechatNotesForbidden = @(Get-ChildItem -LiteralPath $wechatNotesApp -Recurse -File | Where-Object {
    $_.Extension -in @('.db', '.sqlite3', '.lnk', '.dmp', '.log') -or $_.Name -match '(?i)(^keys.*\.json$|raw-probe|诊断|本机只读|\.db-(wal|shm)$|^ggml-.*\.bin$|^speech-(model|settings)\.json$|^focus-(settings|restore)\.json$)'
})
if ($wechatNotesForbidden.Count -gt 0) { throw '发布目录含本机数据、模型文件或诊断文件，已停止打包。' }
$wechatNotesBinarySuffix = if ($Standalone) { 'win-x64-standalone' } else { 'win-x64' }
$wechatNotesBinaryZip = Join-Path $wechatNotesPackageRoot "Ripper-$wechatNotesVersion-$wechatNotesBinarySuffix.zip"
$wechatNotesSourceZip = Join-Path $wechatNotesPackageRoot "Ripper-$wechatNotesVersion-source.zip"
Compress-Archive -LiteralPath $wechatNotesApp -DestinationPath $wechatNotesBinaryZip -Force
Compress-Archive -LiteralPath (Join-Path $wechatNotesApp 'source') -DestinationPath $wechatNotesSourceZip -Force
$wechatNotesChecksums = foreach ($wechatNotesArchive in (Get-ChildItem -LiteralPath $wechatNotesPackageRoot -Filter 'Ripper-*.zip' -File | Sort-Object Name)) {
    $wechatNotesHash = Get-FileHash -LiteralPath $wechatNotesArchive.FullName -Algorithm SHA256
    '{0}  {1}' -f $wechatNotesHash.Hash.ToLowerInvariant(), $wechatNotesArchive.Name
}
[IO.File]::WriteAllLines((Join-Path $wechatNotesPackageRoot 'SHA256SUMS.txt'), $wechatNotesChecksums, [Text.UTF8Encoding]::new($false))
Write-Host "发布包已生成：$wechatNotesPackageRoot"
