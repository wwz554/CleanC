param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root "CleanC.sln"
$project = Join-Path $root "src\CleanC.App\CleanC.App.csproj"
$publishDir = Join-Path $root "artifacts\publish"
$installerDir = Join-Path $root "artifacts\installer"
$iss = Join-Path $root "build\CleanC.iss"

Push-Location $root

try {
    Write-Host "========================================"
    Write-Host " CleanC build script 1.7.0"
    Write-Host "========================================"

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw ".NET SDK was not found. Install .NET 8 SDK x64 first."
    }

    $sdk = (& dotnet --version).Trim()
    Write-Host ("dotnet SDK: " + $sdk)

    if (-not (Test-Path $sln)) {
        throw ("Solution not found: " + $sln)
    }

    if (-not (Test-Path $project)) {
        throw ("Project not found: " + $project)
    }

    Write-Host ""
    Write-Host "[1/4] Restore"
    & dotnet restore $sln
    if ($LASTEXITCODE -ne 0) {
        throw ("dotnet restore failed with exit code " + $LASTEXITCODE)
    }

    Write-Host ""
    Write-Host "[2/4] Build solution (Release / Any CPU)"
    # CleanC.sln only defines Debug|Any CPU and Release|Any CPU.
    # The solution maps CleanC.App internally to x64.
    & dotnet build $sln -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw ("dotnet build failed with exit code " + $LASTEXITCODE)
    }

    Write-Host ""
    Write-Host "[3/4] Publish CleanC.App win-x64 self-contained"

    if (Test-Path $publishDir) {
        Remove-Item $publishDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    & dotnet publish $project `
        -c $Configuration `
        "-p:Platform=x64" `
        "-p:WindowsAppSDKSelfContained=true" `
        "-p:PublishSingleFile=false" `
        -r win-x64 `
        --self-contained true `
        --no-restore `
        -o $publishDir

    if ($LASTEXITCODE -ne 0) {
        throw ("dotnet publish failed with exit code " + $LASTEXITCODE)
    }

    $exe = Join-Path $publishDir "CleanC.exe"
    if (-not (Test-Path $exe)) {
        throw ("CleanC.exe was not created: " + $exe)
    }

    $pri = Join-Path $publishDir "CleanC.pri"
    if (-not (Test-Path $pri)) {
        throw ("CleanC.pri was not published. WinUI resource publish workaround failed: " + $pri)
    }

    Write-Host ("Published EXE: " + $exe)
    Write-Host ("Published PRI: " + $pri)

    Write-Host ""
    Write-Host "[4/4] Build installer"

    $isccCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    )

    $iscc = $null
    foreach ($candidate in $isccCandidates) {
        if ($candidate -and (Test-Path $candidate)) {
            $iscc = $candidate
            break
        }
    }

    if (-not $iscc) {
        throw "Inno Setup 6 ISCC.exe was not found."
    }

    Write-Host ("Inno Setup: " + $iscc)

    if (-not (Test-Path $iss)) {
        throw ("Installer script not found: " + $iss)
    }

    New-Item -ItemType Directory -Force -Path $installerDir | Out-Null

    & $iscc $iss
    if ($LASTEXITCODE -ne 0) {
        throw ("Inno Setup failed with exit code " + $LASTEXITCODE)
    }

    $setup = Join-Path $installerDir "CleanC-Setup.exe"
    if (-not (Test-Path $setup)) {
        $setupCandidates = @(Get-ChildItem $installerDir -Filter "*.exe" -File -ErrorAction SilentlyContinue)
        if ($setupCandidates.Count -eq 1) {
            $setup = $setupCandidates[0].FullName
        }
    }

    if (-not (Test-Path $setup)) {
        throw ("Installer EXE was not found in: " + $installerDir)
    }

    Write-Host ""
    Write-Host "========================================"
    Write-Host " BUILD SUCCESS"
    Write-Host "========================================"
    Write-Host ("Program  : " + $exe)
    Write-Host ("Installer: " + $setup)
}
catch {
    Write-Host ""
    Write-Host "========================================"
    Write-Host " BUILD FAILED"
    Write-Host "========================================"
    Write-Host $_.Exception.Message
    exit 1
}
finally {
    Pop-Location
}
