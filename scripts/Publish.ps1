param([switch]$WithoutLibreOffice,[string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'src\PrintMaster\PrintMaster.csproj'
$destination = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path $projectRoot 'artifacts\PrintMaster' } else { [System.IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory)) }
Push-Location $projectRoot
try {
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishReadyToRun=false -o $destination
    if ($LASTEXITCODE -ne 0) { throw 'PrintMaster publish failed.' }
    if (-not $WithoutLibreOffice) {
        $source = Join-Path $projectRoot 'tools\LibreOffice'
        if (-not (Test-Path -LiteralPath (Join-Path $source 'program\soffice.com'))) {
            & (Join-Path $PSScriptRoot 'Setup-LibreOffice.ps1')
        }
        $toolsDestination = Join-Path $destination 'tools'
        New-Item -ItemType Directory -Force -Path $toolsDestination | Out-Null
        Copy-Item -LiteralPath $source -Destination $toolsDestination -Recurse -Force
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'),(Join-Path $projectRoot 'THIRD-PARTY.md') -Destination $destination -Force
    foreach ($optionalDocument in @('LICENSE','README.ru.md')) {
        $documentPath = Join-Path $projectRoot $optionalDocument
        if (Test-Path -LiteralPath $documentPath -PathType Leaf) { Copy-Item -LiteralPath $documentPath -Destination $destination -Force }
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $destination -Recurse -Force
    Write-Output (Join-Path $destination 'PrintMaster.exe')
} finally { Pop-Location }
