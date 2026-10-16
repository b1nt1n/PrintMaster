#Requires -Version 7.2
<#
.SYNOPSIS
Validates a prepared public tree and every decompressed ZIP entry without launching
the application, extracting untrusted paths, or using the printer.
#>
[CmdletBinding()]
param([string]$PublishDirectory='publish', [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$publishRoot=if([IO.Path]::IsPathRooted($PublishDirectory)){[IO.Path]::GetFullPath($PublishDirectory)}else{[IO.Path]::GetFullPath((Join-Path $projectRoot $PublishDirectory))}
if((Split-Path -Parent $publishRoot) -ne $projectRoot -or (Split-Path -Leaf $publishRoot) -notmatch '^publish(?:-[A-Za-z0-9._-]+)?$'){
    throw 'PublishDirectory must be a direct workspace child named publish or publish-SUFFIX.'
}
$repo=Join-Path $publishRoot 'repository';$assets=Join-Path $publishRoot 'release-assets'
if(-not $PSBoundParameters.ContainsKey('Version')){
    # Read the prepared release, not this checkout: older releases remain verifiable.
    $versionProject=Join-Path $repo 'src/PrintMaster/PrintMaster.csproj'
    if(-not(Test-Path -LiteralPath $versionProject -PathType Leaf)){
        throw "Cannot infer the release version: prepared project file is missing: $versionProject. Specify -Version explicitly."
    }
    [xml]$projectXml=Get-Content -LiteralPath $versionProject -Raw
    $versions=@($projectXml.SelectNodes('/Project/PropertyGroup/Version')|ForEach-Object{$_.InnerText.Trim()}|Sort-Object -Unique)
    if($versions.Count -ne 1 -or $versions[0] -notmatch '^\d+\.\d+\.\d+$'){
        throw "Cannot infer one numeric major.minor.patch version from $versionProject. Specify -Version explicitly."
    }
    $Version=$versions[0]
}
$archive=Join-Path $assets "PrintMaster-$Version-win-x64.zip"
$manifestPath=Join-Path $assets "PrintMaster-$Version-win-x64.manifest.json"
$problems=[Collections.Generic.List[string]]::new();$warnings=[Collections.Generic.List[string]]::new()
foreach($path in @('README.md','README.ru.md','CHANGELOG.md','LICENSE','THIRD-PARTY.md','PrintMaster.sln','global.json','Directory.Build.props','ico.png','scripts/Publish.ps1','src/PrintMaster/packages.lock.json','src/ThirdParty/WpfMath/packages.lock.json','tests/PrintMaster.Tests/packages.lock.json','docs/licenses/LICENSE-MANIFEST.md')){
    if(-not(Test-Path -LiteralPath (Join-Path $repo $path) -PathType Leaf)){$problems.Add("Missing public source file: $path")}
}
if(Test-Path -LiteralPath $repo){
    foreach($file in Get-ChildItem -LiteralPath $repo -Recurse -File -Force){
        $relative=[IO.Path]::GetRelativePath($repo,$file.FullName).Replace('\','/')
        if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){$problems.Add("Linked entry: $relative")}
        if($relative -match '(?:^|/)(?:bin|obj|\.git|\.vs|artifacts|TestResults|Recovery|Workspaces|node_modules)(?:/|$)' -or $file.Extension -in @('.pdb','.trx','.log','.zip','.msi','.user','.suo')){$problems.Add("Private/generated file: $relative")}
        if($relative -eq 'tests/PrintMaster.Tests/CustomerImageDiagnostics.cs'){$problems.Add('Customer-image diagnostic must not be public.')}
        if($file.Extension -in @('.md','.cs','.xaml','.csproj','.json','.ps1','.txt','.sln','.props','.targets') -or $file.Name -eq 'LICENSE'){
            $text=[IO.File]::ReadAllText($file.FullName)
            $userPattern='[A-Za-z]:'+'[\\/]Users[\\/]'
            if($text -match $userPattern){$problems.Add("Personal absolute path: $relative")}
            if($text -match '(?:ghp_|github_pat_)[A-Za-z0-9_]{20,}|-----BEGIN (?:RSA |OPENSSH |EC )?PRIVATE KEY-----'){$problems.Add("Possible credential: $relative")}
        }
    }
    $sdk=Get-Content -LiteralPath "$repo/global.json" -Raw|ConvertFrom-Json
    if($sdk.sdk.version -notmatch '^8\.0\.' -or $sdk.sdk.rollForward -ne 'latestFeature'){$problems.Add('Public SDK policy must permit current .NET 8 feature bands.')}
    $licenseIndex=Join-Path $repo 'docs/licenses/license-files.json'
    if(Test-Path -LiteralPath $licenseIndex){
        foreach($license in (Get-Content -LiteralPath $licenseIndex -Raw|ConvertFrom-Json)){
            if($license.Path -match '(^/|\\|:|(?:^|/)\.\.(?:/|$))'){$problems.Add('Unsafe license provenance path.');continue}
            $licensePath=Join-Path $repo ('docs/licenses/'+$license.Path)
            if(-not(Test-Path -LiteralPath $licensePath -PathType Leaf)){$problems.Add("Missing preserved license: $($license.Path)");continue}
            if((Get-FileHash -LiteralPath $licensePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $license.SHA256){$problems.Add("Changed license text: $($license.Path)")}
        }
    }else{$problems.Add('License integrity/provenance index is missing.')}
}
if(-not(Test-Path -LiteralPath $archive) -or -not(Test-Path -LiteralPath $manifestPath)){$problems.Add('Archive or manifest is missing.')}
$checked=0;$bytes=0L
if((Test-Path -LiteralPath $archive) -and (Test-Path -LiteralPath $manifestPath)){
    if((Get-Item -LiteralPath $archive).Length -ge 2GB){$problems.Add('Archive reaches 2 GiB ceiling.')}
    $expected=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($item in (Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json)){
        if(-not $expected.TryAdd($item.Path,$item)){$problems.Add("Duplicate manifest path: $($item.Path)")}
    }
    $zip=[IO.Compression.ZipFile]::OpenRead($archive)
    $observed=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    try{
        foreach($entry in $zip.Entries){
            $name=$entry.FullName
            if($name -notlike "PrintMaster-$Version-win-x64/*" -or $name -match '(^/|\\|:|(?:^|/)\.\.(?:/|$))'){$problems.Add("Unsafe ZIP entry: $name");continue}
            if($name -match '(?:^|/)(?:\.git|\.vs|Recovery|Workspaces)(?:/|$)' -or [IO.Path]::GetExtension($name) -in @('.pdb','.msi','.log','.trx')){$problems.Add("Private/generated ZIP entry: $name")}
            if((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000){$problems.Add("Symbolic-link ZIP entry: $name")}
            if(-not $observed.Add($name)){$problems.Add("Duplicate ZIP entry: $name");continue}
            if(-not $expected.ContainsKey($name)){$problems.Add("Unlisted ZIP entry: $name");continue}
            $item=$expected[$name]
            if($entry.Length -ne $item.Length){$problems.Add("Length mismatch: $name")}
            $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
            try{$actual=[Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant()}finally{$stream.Dispose();$sha.Dispose()}
            if($actual -ne $item.SHA256){$problems.Add("Decompressed hash mismatch: $name")}
            $checked++;$bytes+=$entry.Length
        }
        foreach($name in $expected.Keys){if(-not $observed.Contains($name)){$problems.Add("Missing ZIP entry: $name")}}
        $prefix="PrintMaster-$Version-win-x64/"
        foreach($name in @('PrintMaster.exe','PrintMaster.dll','PrintMaster.runtimeconfig.json','coreclr.dll','PresentationFramework.dll','pdfium.dll','tools/LibreOffice/program/soffice.com','tools/LibreOffice/license.txt','tools/LibreOffice/NOTICE','LICENSE','THIRD-PARTY.md','README.md','README.ru.md','docs/licenses/LICENSE-MANIFEST.md','Assets/Licenses/WpfMathFork/NOTICE.md','Assets/Licenses/pdfium/LICENSE')){
            if(-not $observed.Contains($prefix+$name)){$problems.Add("Missing runtime/license entry: $name")}
        }
        foreach($name in @('PrintMaster.exe','pdfium.dll','coreclr.dll')){
            $entry=$zip.GetEntry($prefix+$name)
            if($entry){$stream=$entry.Open();$memory=[IO.MemoryStream]::new();try{$stream.CopyTo($memory);$data=$memory.ToArray();$offset=[BitConverter]::ToInt32($data,0x3c);if([BitConverter]::ToUInt16($data,$offset+4)-ne 0x8664){$problems.Add("Runtime is not AMD64: $name")}}finally{$stream.Dispose();$memory.Dispose()}}
        }
        foreach($name in @('PrintMaster.dll','WpfMath.dll')){
            $entry=$zip.GetEntry($prefix+$name)
            if($entry){
                $stream=$entry.Open();$memory=[IO.MemoryStream]::new()
                try{
                    $stream.CopyTo($memory)
                    if([Text.Encoding]::UTF8.GetString($memory.ToArray()) -match ('[A-Za-z]:'+'[\\/]Users[\\/]')){
                        $problems.Add("Personal build path embedded in $name; rebuild with PathMap/ContinuousIntegrationBuild.")
                    }
                }finally{$stream.Dispose();$memory.Dispose()}
            }
        }
    }finally{$zip.Dispose()}
    $line=(Get-Content -LiteralPath "$assets/SHA256SUMS.txt" -Raw).Trim()
    $actual=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if($line -ne "$actual  $([IO.Path]::GetFileName($archive))"){$problems.Add('SHA256SUMS.txt does not match the archive.')}
}
$warnings.Add('No remote repository was created; GitHub download/issue links must be checked once an owner/repository is chosen.')
$warnings.Add('This verification checks staged files and ZIP bytes; source build and interactive runtime acceptance are separate checks.')
$result=[ordered]@{Passed=$problems.Count-eq 0;Version=$Version;ZipEntriesChecked=$checked;UncompressedBytes=$bytes;Errors=$problems.ToArray();Notes=$warnings.ToArray();Uploaded=$false;PhysicalPrintJobs=0;VerifiedUtc=[DateTime]::UtcNow.ToString('o')}
$result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $publishRoot 'VERIFICATION.json') -Encoding utf8
if($problems.Count){throw ($problems-join [Environment]::NewLine)}
Write-Output "Verified $checked decompressed entries ($bytes bytes); no private/generated files detected."
