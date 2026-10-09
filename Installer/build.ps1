<#
.SYNOPSIS
    Builds the UbaTrayTool installer.

.DESCRIPTION
    1. Generates UbaTrayTool\app.ico (from IconFactory) if it doesn't exist yet.
    2. Publishes the app self-contained (win-x64) into <StagingDir>\publish.
    3. Compiles UbaTrayTool.iss with Inno Setup into <StagingDir>\output\UbaTrayToolSetup-<version>.exe
       and copies it to <DeployDir>.

    The version defaults to <Version> in UbaTrayTool.csproj.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Version 0.2.0
#>
param(
    [string] $Version = "",
    # Publish folder and installer go here (kept out of the repo by .gitignore).
    [string] $StagingDir = "",
    # The finished installer is copied here too when the folder is reachable. Empty to skip.
    [string] $DeployDir = "Z:\Software\Claude\UbaTrayTool"
)

$ErrorActionPreference = "Stop"

$installerDir = $PSScriptRoot
$repoRoot     = Split-Path $installerDir -Parent
$projectDir   = Join-Path $repoRoot "UbaTrayTool"
$project      = Join-Path $projectDir "UbaTrayTool.csproj"
if (-not $StagingDir) { $StagingDir = $installerDir }
$publishDir   = Join-Path $StagingDir "publish"
$outputDir    = Join-Path $StagingDir "output"

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { $Version = "0.1.0" }
}

function Find-Iscc {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($found) { return $found }
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw "Inno Setup 6 not found. Install it with:  winget install JRSoftware.InnoSetup"
}

$iscc = Find-Iscc
Write-Host "UbaTrayTool $Version" -ForegroundColor Cyan

# 1. Icon: the exe only picks up app.ico when it exists at build time.
$icon = Join-Path $projectDir "app.ico"
if (-not (Test-Path $icon)) {
    Write-Host "Generating app.ico" -ForegroundColor Cyan
    $iconBuild = Join-Path $StagingDir "iconbuild"
    dotnet build $project -c Release --nologo -v quiet -o $iconBuild
    if ($LASTEXITCODE) { throw "Build failed" }
    $process = Start-Process -FilePath (Join-Path $iconBuild "UbaTrayTool.exe") -ArgumentList "--export-icon", "`"$icon`"" -Wait -PassThru
    if ($process.ExitCode -ne 0 -or -not (Test-Path $icon)) { throw "Icon export failed" }
    Remove-Item $iconBuild -Recurse -Force -ErrorAction SilentlyContinue
}

# 2. Publish
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
Write-Host "Publishing" -ForegroundColor Cyan
dotnet publish $project `
    -c Release -r win-x64 --self-contained true `
    -o $publishDir `
    -p:DebugType=none `
    -p:SatelliteResourceLanguages=en `
    -p:Version=$Version `
    --nologo
if ($LASTEXITCODE) { throw "Publish failed" }

# 3. Installer
Write-Host "Compiling installer" -ForegroundColor Cyan
Push-Location $installerDir
try {
    & $iscc "/DAppVersion=$Version" "/DPublishDir=$publishDir" "/O$outputDir" "UbaTrayTool.iss"
    if ($LASTEXITCODE) { throw "Inno Setup failed" }
}
finally {
    Pop-Location
}

$setup = Join-Path $outputDir "UbaTrayToolSetup-$Version.exe"
Write-Host "Done: $setup" -ForegroundColor Green

if ($DeployDir) {
    $target = $DeployDir
    $drive = Split-Path $DeployDir -Qualifier
    if ($drive -and -not (Test-Path "$drive\")) {
        # Mapped drive not visible (elevated shell, or the share is still reconnecting):
        # use the network path it maps to instead.
        $remote = (Get-ItemProperty "HKCU:\Network\$($drive.TrimEnd(':'))" -ErrorAction SilentlyContinue).RemotePath
        if ($remote) { $target = $remote + $DeployDir.Substring($drive.Length) }
    }
    try {
        New-Item -ItemType Directory -Force $target -ErrorAction Stop | Out-Null
        Copy-Item $setup $target -Force -ErrorAction Stop
        Write-Host "Copied to $target" -ForegroundColor Green
    }
    catch {
        Write-Warning "Could not copy to $target ($($_.Exception.Message)); installer left in $outputDir"
    }
}
