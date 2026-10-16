#Requires -Version 7.2
<#
.SYNOPSIS
Stages a public source tree and a portable ZIP locally. Does not build, publish,
create a Git repository, upload, print, or change the installed application's state.
.EXAMPLE
pwsh ./scripts/Prepare-GitHubRelease.ps1 -Version 1.0.35
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.35',
    [string]$ApplicationDirectory,
    [string]$ContentDirectory,
    [string]$OutputDirectory = 'publish'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
function Resolve-ProjectPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $projectRoot $Path))
}
function Assert-Within([string]$Path, [string]$Parent) {
    $prefix = $Parent.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if (-not $Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path must remain below the intended workspace directory: $Path"
    }
}
function Get-SafeFiles([string]$Base, [bool]$ExcludeBuildDirectories=$true) {
    $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push($Base)
    while ($pending.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked/reparse entry refused: $($item.FullName)" }
            if ($item.PSIsContainer) {
                if (-not $ExcludeBuildDirectories -or $item.Name -notin @('bin','obj','.git','.vs','TestResults','node_modules','cache')) { $pending.Push($item.FullName) }
            } else { $item }
        }
    }
}
function Copy-File([string]$Source, [string]$Target, [bool]$Overwrite=$false) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Target)) | Out-Null
    [IO.File]::Copy($Source, $Target, $Overwrite)
}
function Hash-File([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
if (-not $ApplicationDirectory) { $ApplicationDirectory = "artifacts/PrintMaster-$Version" }
if (-not $ContentDirectory) { $ContentDirectory = 'publish-content' }
$appRoot = Resolve-ProjectPath $ApplicationDirectory
$contentRoot = Resolve-ProjectPath $ContentDirectory
$outputRoot = Resolve-ProjectPath $OutputDirectory
Assert-Within $outputRoot $projectRoot
if ((Split-Path -Parent $outputRoot) -ne $projectRoot -or (Split-Path -Leaf $outputRoot) -notmatch '^publish(?:-[A-Za-z0-9._-]+)?$') {
    throw 'OutputDirectory must be a direct workspace child named publish or publish-SUFFIX.'
}
if (Test-Path -LiteralPath $outputRoot) {
    throw 'The output already exists. It is preserved; select a fresh publish-SUFFIX directory. This script never recursively deletes a previous release.'
}
foreach ($required in @("$appRoot/PrintMaster.exe","$appRoot/PrintMaster.dll","$appRoot/tools/LibreOffice/program/soffice.com", "$contentRoot/repository/README.md", "$contentRoot/repository/README.ru.md", "$contentRoot/release-assets/RELEASE-NOTES.md")) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required input is missing: $required" }
}
$actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo("$appRoot/PrintMaster.dll").ProductVersion
if ($actualVersion -notmatch ('^' + [regex]::Escape($Version) + '(?:\.|\+|$)')) { throw "Expected application $Version, found $actualVersion" }
$repository = Join-Path $outputRoot 'repository'
$assets = Join-Path $outputRoot 'release-assets'
[IO.Directory]::CreateDirectory($repository) | Out-Null
[IO.Directory]::CreateDirectory($assets) | Out-Null
$excluded = [Collections.Generic.List[string]]::new()
foreach ($tree in @('src','tests')) {
    foreach ($file in Get-SafeFiles (Join-Path $projectRoot $tree)) {
        $relative = [IO.Path]::GetRelativePath($projectRoot, $file.FullName).Replace('\','/')
        # This optional diagnostic embeds a customer's local image path and is not
        # referenced by another test. Public tests use generated fixtures instead.
        if ($relative -eq 'tests/PrintMaster.Tests/CustomerImageDiagnostics.cs' -or
            $file.Extension -in @('.trx','.pdb','.log','.zip','.msi','.user','.suo')) { $excluded.Add($relative); continue }
        Copy-File $file.FullName (Join-Path $repository $relative)
    }
}
foreach ($name in @('PrintMaster.sln','global.json','Directory.Build.props','LICENSE','THIRD-PARTY.md','ico.png')) { Copy-File (Join-Path $projectRoot $name) (Join-Path $repository $name) }
foreach ($name in @('Setup-LibreOffice.ps1','Publish.ps1','Prepare-GitHubRelease.ps1','Verify-GitHubRelease.ps1')) { Copy-File (Join-Path $PSScriptRoot $name) (Join-Path $repository "scripts/$name") }
$sdk = Get-Content -LiteralPath "$repository/global.json" -Raw | ConvertFrom-Json
$previousRollForward = $sdk.sdk.rollForward
$sdk.sdk.rollForward = 'latestFeature'
$sdk | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$repository/global.json" -Encoding utf8
@'
**/bin/
**/obj/
**/TestResults/
.vs/
.idea/
artifacts/
tools/LibreOffice/
tools/cache/
publish/
publish-*/
publish-content/
*.user
*.suo
*.pdb
*.zip
*.msi
*.log
*.trx
'@ | Set-Content -LiteralPath "$repository/.gitignore" -Encoding utf8
foreach ($file in Get-SafeFiles "$contentRoot/repository") {
    $relative = [IO.Path]::GetRelativePath("$contentRoot/repository",$file.FullName).Replace('\','/')
    if ($relative -notmatch '^(?:README(?:\.ru)?\.md|THIRD-PARTY\.md|CHANGELOG\.md|CONTRIBUTING\.md|SECURITY\.md|docs/|images/|examples/|\.github/)') { throw "Unexpected public content: $relative" }
    Copy-File $file.FullName (Join-Path $repository $relative) ($relative -eq 'THIRD-PARTY.md')
}
foreach ($file in Get-SafeFiles "$contentRoot/release-assets") {
    $relative = [IO.Path]::GetRelativePath("$contentRoot/release-assets",$file.FullName).Replace('\','/')
    if ($relative -notmatch '^[A-Za-z0-9._-]+\.md$') { throw "Unexpected release-note input: $relative" }
    Copy-File $file.FullName (Join-Path $assets $relative)
}
# ZIP entries map directly to their inputs: there is no second 1+ GiB copy of
# LibreOffice, and personal root files/internal reports never enter the mapping.
$entries = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in Get-SafeFiles $appRoot $false) {
    $relative = [IO.Path]::GetRelativePath($appRoot,$file.FullName).Replace('\','/')
    $include = $relative -match '^[^/]+\.(?:dll|exe)$' -or
        $relative -in @('PrintMaster.deps.json','PrintMaster.runtimeconfig.json') -or
        $relative -match '^(?:Assets/|(?:cs|de|es|fr|it|ja|ko|pl|pt-BR|ru|tr|zh-Hans|zh-Hant)/[^/]+\.dll$|tools/LibreOffice/)'
    if (-not $include -or $file.Extension -in @('.pdb','.msi','.log','.trx')) { continue }
    $entries.Add($relative,$file.FullName)
}
foreach ($name in @('README.md','README.ru.md','LICENSE','THIRD-PARTY.md','CHANGELOG.md','CONTRIBUTING.md')) { $entries.Add($name,(Join-Path $repository $name)) }
foreach ($file in Get-SafeFiles "$repository/docs") {
    $relative = [IO.Path]::GetRelativePath($repository,$file.FullName).Replace('\','/')
    $entries.Add($relative,$file.FullName)
}
foreach ($tree in @('images','examples')) {
    $publicTree=Join-Path $repository $tree
    if(Test-Path -LiteralPath $publicTree){
        foreach($file in Get-SafeFiles $publicTree){
            $relative=[IO.Path]::GetRelativePath($repository,$file.FullName).Replace('\','/')
            $entries.Add($relative,$file.FullName)
        }
    }
}
$archiveName = "PrintMaster-$Version-win-x64.zip"
$archivePath = Join-Path $assets $archiveName
$prefix = "PrintMaster-$Version-win-x64/"
$manifest = [Collections.Generic.List[object]]::new()
$stream = [IO.File]::Open($archivePath,[IO.FileMode]::CreateNew)
$zip = [IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$false,[Text.Encoding]::UTF8)
try {
    foreach ($name in ($entries.Keys | Sort-Object -CaseSensitive)) {
        if ($name -match '(^/|\\|:|(?:^|/)\.\.(?:/|$))') { throw "Unsafe archive entry: $name" }
        $sourcePath = $entries[$name]
        $entry = $zip.CreateEntry($prefix+$name,[IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
        $inputStream = [IO.File]::OpenRead($sourcePath); $targetStream = $entry.Open()
        try { $inputStream.CopyTo($targetStream) } finally { $inputStream.Dispose(); $targetStream.Dispose() }
        $manifest.Add([ordered]@{Path=$prefix+$name;Length=(Get-Item -LiteralPath $sourcePath).Length;SHA256=Hash-File $sourcePath})
    }
} finally { $zip.Dispose(); $stream.Dispose() }
if ((Get-Item -LiteralPath $archivePath).Length -ge 2GB) { throw 'Release archive reaches GitHub per-asset 2 GiB ceiling.' }
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $assets "PrintMaster-$Version-win-x64.manifest.json") -Encoding utf8
"$(Hash-File $archivePath)  $archiveName" | Set-Content -LiteralPath (Join-Path $assets 'SHA256SUMS.txt') -Encoding ascii
[ordered]@{
    Version=$Version;CreatedUtc=[DateTime]::UtcNow.ToString('o');PublicSourceFiles=@(Get-SafeFiles $repository).Count
    Archive=$archiveName;ArchiveEntries=$manifest.Count;ArchiveBytes=(Get-Item -LiteralPath $archivePath).Length
    SourceAdjustments=@("global.json rollForward: $previousRollForward -> latestFeature (same .NET 8 SDK major/minor)", 'Optional customer-image diagnostic omitted; generated-fixture tests retained')
    ExcludedFiles=$excluded.ToArray();GitCreated=$false;Uploaded=$false;PrintJobs=0
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$outputRoot/PREPARATION.json" -Encoding utf8
& (Join-Path $PSScriptRoot 'Verify-GitHubRelease.ps1') -PublishDirectory $outputRoot -Version $Version
if (-not $?) { throw 'Release verification failed.' }
Write-Output "Prepared locally: $outputRoot"
