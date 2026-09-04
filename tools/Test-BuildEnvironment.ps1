#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$FormalAssemblyDir = 'D:/Programs/Steam/steamapps/content/app_2868840/depot_2868841/data_sts2_windows_x86_64',
    [string]$BetaAssemblyDir = 'D:/Programs/Steam/steamapps/common/Slay the Spire 2/data_sts2_windows_x86_64'
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$repoRoot = Split-Path $PSScriptRoot -Parent
$failures = [Collections.Generic.List[string]]::new()
$projects = @(
    'STS2SkinChanger/STS2SkinChanger.csproj',
    'compat/ThunninoiSkinManagerCompat/ThunninoiSkinManagerCompat.csproj',
    'tests/STS2SkinChanger.RuntimeTests/STS2SkinChanger.RuntimeTests.csproj',
    'tools/FrameworkCompatProbe/FrameworkCompatProbe.csproj',
    'tools/WorkshopPublisher/WorkshopPublisher.csproj'
)

function Read-BuildProperties([string]$Project, [string[]]$ExtraArgs = @()) {
    $result = & dotnet msbuild (Join-Path $repoRoot $Project) -nologo `
        '-getProperty:GameAssemblyDir,MSBuildProjectExtensionsPath,PlatformTarget' `
        '-getItem:Reference,Compile' @ExtraArgs
    if ($LASTEXITCODE -ne 0) { throw "MSBuild evaluation failed: $Project" }
    return (($result -join "`n") | ConvertFrom-Json)
}

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $failures.Add($Message) }
}

foreach ($project in $projects) {
    $evaluated = Read-BuildProperties $project
    $actualDir = $evaluated.Properties.GameAssemblyDir
    if ([string]::IsNullOrWhiteSpace($actualDir)) {
        Check $false "$project has no configurable game directory"
    } else {
        Check ([IO.Path]::GetFullPath($actualDir) -eq [IO.Path]::GetFullPath($FormalAssemblyDir)) "$project defaults to the wrong game directory: $actualDir"
    }
    $cache = $evaluated.Properties.MSBuildProjectExtensionsPath.Replace('\', '/')
    $hostFolder = if ([OperatingSystem]::IsWindows()) { 'windows' } else { 'unix' }
    Check ($cache.TrimEnd('/').EndsWith("/obj/$hostFolder")) "$project shares a cross-OS restore cache: $cache"

    foreach ($reference in $evaluated.Items.Reference) {
        if ($reference.Identity -in @('sts2', 'GodotSharp', '0Harmony', 'Steamworks.NET')) {
            Check (Test-Path -LiteralPath $reference.HintPath) "$project has a missing game reference: $($reference.HintPath)"
        }
    }
    $generatedSources = @($evaluated.Items.Compile | Where-Object { $_.Identity.Replace('\', '/') -match '(^|/)(obj|bin)/' })
    Check ($generatedSources.Count -eq 0) "$project includes stale generated source from bin/obj"

    $overridden = Read-BuildProperties $project @("-p:GameAssemblyDir=$BetaAssemblyDir")
    Check ([IO.Path]::GetFullPath($overridden.Properties.GameAssemblyDir) -eq [IO.Path]::GetFullPath($BetaAssemblyDir)) "$project ignores the explicit game directory"
    foreach ($reference in $overridden.Items.Reference) {
        if ($reference.Identity -in @('sts2', 'GodotSharp', '0Harmony', 'Steamworks.NET')) {
            $referenceDir = Split-Path $reference.HintPath -Parent
            Check ([IO.Path]::GetFullPath($referenceDir) -eq [IO.Path]::GetFullPath($BetaAssemblyDir)) "$project keeps a stale reference after a game-directory override: $($reference.HintPath)"
        }
    }
}

$main = Read-BuildProperties $projects[0]
Check ($main.Properties.PlatformTarget -eq 'AnyCPU') 'The shipped Mod must remain AnyCPU for Windows/macOS compatibility'
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    throw "$($failures.Count) build-environment checks failed"
}
Write-Host "PASS: $($projects.Count) projects use native paths, isolated restore caches and explicit game overrides; Mod remains AnyCPU."
