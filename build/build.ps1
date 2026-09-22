param([string]$Dotnet = "$PSScriptRoot\tools\dotnet\dotnet.exe", [string]$Iscc = "$PSScriptRoot\tools\inno\ISCC.exe")
$ErrorActionPreference='Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$project=Split-Path $PSScriptRoot -Parent
Push-Location $project
try {
 & $Dotnet run --project tests/CleanC.Tests -c Release
 if($LASTEXITCODE -ne 0){throw 'Tests failed'}
 & $Dotnet publish src/CleanC.App -c Release -p:Platform=x64 -r win-x64 --self-contained true -o artifacts/publish
 if($LASTEXITCODE -ne 0){throw 'Publish failed'}
 & $Iscc build/CleanC.iss
 if($LASTEXITCODE -ne 0){throw 'Installer build failed'}
} finally { Pop-Location }

