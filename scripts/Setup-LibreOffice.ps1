param([string]$Destination)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Destination) { $Destination = Join-Path $projectRoot 'tools\LibreOffice' }
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$cachePath = Join-Path $projectRoot 'tools\cache'
New-Item -ItemType Directory -Force -Path $cachePath,$destinationPath | Out-Null
$msiPath = Join-Path $cachePath 'LibreOffice_26.2.6_Win_x86-64.msi'
$url = 'https://download.documentfoundation.org/libreoffice/stable/26.2.6/win/x86_64/LibreOffice_26.2.6_Win_x86-64.msi'
# Official SHA-256 from the download mirror list, pinned with this release.
$expectedHash = 'f9877032fd908beb9c0ddf06df4af5c2e85f419c42e14876c4cce5aae5fb2660'
if (-not (Test-Path -LiteralPath $msiPath)) {
    & curl.exe -L --fail --silent --show-error --output $msiPath $url
    if ($LASTEXITCODE -ne 0) { throw 'LibreOffice download failed.' }
}
$actualHash = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) { throw 'LibreOffice checksum mismatch.' }
if (-not (Test-Path -LiteralPath (Join-Path $destinationPath 'program\soffice.com'))) {
    # Administrative extraction: no Office Interop, system installation or file associations.
    $logPath = Join-Path $cachePath 'libreoffice-extract.log'
    $arguments = '/a "{0}" /qn TARGETDIR="{1}" /l*v "{2}"' -f $msiPath,$destinationPath,$logPath
    $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -notin @(0,3010)) { throw "LibreOffice extraction failed: $($process.ExitCode). See $logPath" }
}
$executable = Get-ChildItem -LiteralPath $destinationPath -Filter 'soffice.com' -Recurse | Select-Object -First 1
if (-not $executable) { throw 'soffice.com not found after extraction.' }
Write-Output $executable.FullName
Write-Output ('For a custom location set PRINTMASTER_LIBREOFFICE to this path or use the app settings.')
