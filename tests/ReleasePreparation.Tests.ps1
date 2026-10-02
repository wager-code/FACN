[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
$prepare = Join-Path $PSScriptRoot '../scripts/Prepare-ClientRelease.ps1'
$base = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixture = Join-Path $base ('scfa_release_policy_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$requestPath = Join-Path $fixture 'release.json'
$projectPath = Join-Path $fixture 'client.csproj'
$oldOutput = $env:GITHUB_OUTPUT
$env:GITHUB_OUTPUT = $null
$script:passed = 0
function Check($condition,[string]$name) {
    if (!$condition) { throw "FAIL $name" }
    $script:passed++; Write-Output "PASS $name"
}
function Write-Fixture([string]$version,[string]$fileVersion='4.0.0.64') {
    $request = @{ client_tag=$version;client_version=$version;file_version=$fileVersion
        gateway_tag='gateway-v62';client_asset='SCFA-ContentCenter-test-win-x64.exe' }
    $request | ConvertTo-Json | Set-Content -LiteralPath $requestPath -Encoding UTF8
    "<Project><PropertyGroup><InformationalVersion>$version</InformationalVersion><FileVersion>$fileVersion</FileVersion></PropertyGroup></Project>" | Set-Content -LiteralPath $projectPath -Encoding UTF8
    return $request
}
function Reject([scriptblock]$action,[string]$name) {
    $rejected=$false
    try { & $action | Out-Null } catch { $rejected=$true }
    Check $rejected $name
}
try {
    foreach($case in @(@('4.0.0-dev63','developer',$true),@('4.0.0-beta2','beta',$true),@('4.0.0-rc1','beta',$true),@('4.0.0','stable',$false))) {
        Write-Fixture $case[0] | Out-Null
        $result=& $prepare -Mode Validate -RequestPath $requestPath -ProjectPath $projectPath
        Check ($result.channel -ceq $case[1] -and $result.prerelease -eq $case[2]) "Version $($case[0]) selects exact channel/prerelease"
    }
    $request=Write-Fixture '4.0.0-rc1'
    $request.client_channel='stable';$request | ConvertTo-Json | Set-Content $requestPath -Encoding UTF8
    Reject { & $prepare -RequestPath $requestPath -ProjectPath $projectPath } 'RC cannot masquerade as stable manifest'
    $request=Write-Fixture '4.0.0-rc1';$request.client_tag='4.0.0';$request | ConvertTo-Json | Set-Content $requestPath -Encoding UTF8
    Reject { & $prepare -RequestPath $requestPath -ProjectPath $projectPath } 'Tag must match source informational version'
    $request=Write-Fixture '4.0.0-rc1';$request.file_version='4.0.0.65';$request | ConvertTo-Json | Set-Content $requestPath -Encoding UTF8
    Reject { & $prepare -RequestPath $requestPath -ProjectPath $projectPath } 'Request file version must match source'
    $request=Write-Fixture '4.0.0-rc1';$request.client_asset='../unsafe.exe';$request | ConvertTo-Json | Set-Content $requestPath -Encoding UTF8
    Reject { & $prepare -RequestPath $requestPath -ProjectPath $projectPath } 'Asset file path traversal rejected'
    $request=Write-Fixture '4.0.0-rc1';$request.publish_gateway='false';$request | ConvertTo-Json | Set-Content $requestPath -Encoding UTF8
    Reject { & $prepare -RequestPath $requestPath -ProjectPath $projectPath } 'String false cannot change gateway publication'
    $request=Write-Fixture '4.0.0-rc1';$request.publish_gateway=$false;$request | ConvertTo-Json | Set-Content $requestPath -Encoding UTF8
    $result=& $prepare -RequestPath $requestPath -ProjectPath $projectPath
    Check (!$result.publish_gateway) 'Client-only request can reuse existing gateway release'
    $request=Write-Fixture '4.0.0-rc1';$out=Join-Path $fixture 'assets'
    Reject { & $prepare -Mode Prepare -RequestPath $requestPath -ProjectPath $projectPath -PackagePath $PackagePath -OutputDirectory $out -SourceCommit 'invalid' -Draft } 'Exact source identity required before preparing package'
    $package=Get-Item -LiteralPath $PackagePath
    $version=$package.VersionInfo.ProductVersion.Split('+')[0]
    $request=Write-Fixture $version $package.VersionInfo.FileVersion
    $identity=& $prepare -Mode Prepare -RequestPath $requestPath -ProjectPath $projectPath -PackagePath $PackagePath -OutputDirectory $out -SourceCommit ('a'*40) -Draft
    $manifest=Get-Content (Join-Path $out 'update-manifest.draft.json') -Raw | ConvertFrom-Json
    $asset=Get-Item (Join-Path $out $request.client_asset)
    $hash=(Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedChannel=if($version -match '-dev\d+$') {'developer'} elseif($version -match '-(?:beta|rc)\d+$') {'beta'} else {'stable'}
    Check ($manifest.version -ceq $version -and $manifest.channel -ceq $expectedChannel) 'Actual PE stage selects matching manifest version and channel'
    Check ($manifest.sha256 -ceq $hash -and $manifest.size -eq $asset.Length -and $identity.sha256 -ceq $hash) 'Actual copied package SHA/size match manifest and identity'
    Check ($manifest.published_at -ceq '' -and $identity.status -eq 'candidate-not-published' -and !(Test-Path (Join-Path $out 'update-manifest.json'))) 'Draft candidate never claims published manifest or timestamp'
    Check ($manifest.url -ceq "https://github.com/wager-code/FACN/releases/download/$version/$($request.client_asset)") 'Future download URL is exact tag/asset with no placeholder host'
    $request=Write-Fixture '9.9.9' $package.VersionInfo.FileVersion
    Reject { & $prepare -Mode Prepare -RequestPath $requestPath -ProjectPath $projectPath -PackagePath $PackagePath -OutputDirectory $out -SourceCommit ('a'*40) -Draft } 'Actual PE cannot be relabeled to a different release version'
    Write-Output "Release preparation checks passed: $script:passed"
}
finally {
    $env:GITHUB_OUTPUT=$oldOutput
    $resolved=[IO.Path]::GetFullPath($fixture)
    if (!$resolved.StartsWith($base.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or !(Split-Path $resolved -Leaf).StartsWith('scfa_release_policy_')) { throw 'Unsafe fixture cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
