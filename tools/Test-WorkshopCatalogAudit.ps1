#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$auditRepo = Split-Path $PSScriptRoot -Parent
$auditProject = Join-Path $auditRepo 'tools/WorkshopCatalogExport/WorkshopCatalogExport.csproj'
& dotnet build $auditProject -c Release -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Workshop catalog tool build failed.' }

# Catches the old fallback that silently rewrote a missing package's label.
$auditTemp = [IO.Directory]::CreateTempSubdirectory('sc-catalog-audit-')
try {
    $auditGame = Join-Path $auditTemp.FullName 'game.pck'
    $auditCatalog = Join-Path $auditTemp.FullName 'catalog.json'
    # Missing packages must be rejected before this placeholder is opened as a PCK.
    [IO.File]::WriteAllBytes($auditGame, [byte[]]@(0))
    $auditInput = '[{"id":321,"restartRequired":false,"targets":[{"kind":"character","target":"silent"}]},{"id":654,"restartRequired":true,"targets":[{"kind":"character","target":"ironclad"}]}]'
    [IO.File]::WriteAllText($auditCatalog, $auditInput)
    $auditBefore = (Get-FileHash -LiteralPath $auditCatalog).Hash
    $auditOutput = & dotnet run --project $auditProject -c Release --no-build -- --refresh-restart-hints $auditGame $auditTemp.FullName $auditCatalog 0.111.0 2>&1
    $auditExit = $LASTEXITCODE
    if ((Get-FileHash -LiteralPath $auditCatalog).Hash -ne $auditBefore) {
        throw 'Missing packages must not change any existing catalog labels.'
    }
    if ($auditExit -eq 0) { throw 'Missing packages must stop the audit with a failure status.' }
    if (($auditOutput -join "`n") -notmatch '321' -or ($auditOutput -join "`n") -notmatch '654') {
        throw 'The failure must list every missing Workshop ID for resubscription.'
    }
    Write-Output 'PASS: missing packages stop the audit, list all IDs and preserve the catalog byte-for-byte.'
}
finally {
    $auditParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($auditTemp.Parent.FullName -ne $auditParent -or -not $auditTemp.Name.StartsWith('sc-catalog-audit-')) {
        throw 'Refusing to clean a temporary directory outside the test scope.'
    }
    Remove-Item -LiteralPath $auditTemp.FullName -Recurse -Force
}
exit 0
