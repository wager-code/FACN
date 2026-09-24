param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCommand) { $dotnetCommand.Source } else { "C:\Program Files\dotnet\dotnet.exe" }
if (-not (Test-Path -LiteralPath $dotnet)) { throw "dotnet was not found. Install the .NET 8 SDK or the VS2022 .NET desktop workload." }

function Invoke-DotNet([string[]]$Arguments) {
  & $dotnet @Arguments
  if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE." }
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
  Invoke-DotNet @("restore", ".\SCFA.ContentCenter.sln")
  Invoke-DotNet @("build", ".\SCFA.ContentCenter.sln", "-c", $Configuration, "--no-restore")
  Invoke-DotNet @("publish", ".\src\SCFA.ContentCenter\SCFA.ContentCenter.csproj", "-c", $Configuration, "-r", "win-x64", "--self-contained", "false", "-p:PublishSingleFile=true", "-p:DebugType=None", "-o", ".\artifacts\win-x64")
  Write-Host "Build completed: $root\artifacts\win-x64" -ForegroundColor Green
} finally { Pop-Location }
