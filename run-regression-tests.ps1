$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'tests\SCFA.ContentCenter.RegressionTests\SCFA.ContentCenter.RegressionTests.csproj'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCommand) { $dotnetCommand.Source } else { 'C:\Program Files\dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'dotnet was not found. Install the .NET 8 SDK or the VS2022 .NET desktop workload.' }
& $dotnet run --project $project --configuration Release
if ($LASTEXITCODE -ne 0) { throw "Regression tests failed with exit code $LASTEXITCODE." }
