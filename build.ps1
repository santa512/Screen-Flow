param([switch]$Test, [switch]$Publish)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$originalAppData = $env:APPDATA
$originalDotnetHome = $env:DOTNET_CLI_HOME
$originalTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
try {
$env:APPDATA = Join-Path $PSScriptRoot '.build\AppData'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.build'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
New-Item -ItemType Directory -Path $env:APPDATA -Force | Out-Null
$project = Join-Path $PSScriptRoot 'ScreenFlow\ScreenFlow.csproj'
$restoreArgs = @('restore', $project, '--configfile', (Join-Path $PSScriptRoot 'NuGet.Config'))
if ($Publish) { $restoreArgs += @('-r', 'win-x64', '-p:SelfContained=true') }
& dotnet @restoreArgs
if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }
& dotnet build $project --no-restore -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if ($Test) {
    $testExe = Join-Path $PSScriptRoot 'ScreenFlow\bin\Release\net10.0-windows\ScreenFlow.exe'
    $testProcess = Start-Process -FilePath $testExe -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
    Get-Content (Join-Path $PSScriptRoot 'self-test-results.txt')
    if ($testProcess.ExitCode -ne 0) { throw 'Self-tests failed.' }
}
if ($Publish) {
    & dotnet publish $project --no-restore -c Release -r win-x64 --self-contained true -o (Join-Path $PSScriptRoot 'dist\ScreenFlow-Production')
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $PSScriptRoot 'dist\ScreenFlow-Production\README.md') -Force
    Write-Host "Application: $PSScriptRoot\dist\ScreenFlow-Production\ScreenFlow.exe"
}
} finally {
    $env:APPDATA = $originalAppData
    $env:DOTNET_CLI_HOME = $originalDotnetHome
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $originalTelemetry
}
