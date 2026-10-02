[CmdletBinding()]
param(
    [ValidateSet('Validate','Prepare')][string]$Mode = 'Validate',
    [Parameter(Mandatory)][string]$RequestPath,
    [string]$ProjectPath = (Join-Path $PSScriptRoot '../src/SCFA.ContentCenter/SCFA.ContentCenter.csproj'),
    [string]$PackagePath,
    [string]$OutputDirectory,
    [string]$SourceCommit = $env:GITHUB_SHA,
    [string]$Repository = 'wager-code/FACN',
    [switch]$Draft
)
$ErrorActionPreference = 'Stop'
$release = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json
if ($release.client_tag -notmatch '^\d+\.\d+\.\d+(?:-(dev|beta|rc)\d+)?$' -or
    $release.client_tag -cne $release.client_version -or
    $release.gateway_tag -notmatch '^gateway-v\d+$' -or
    $release.file_version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or
    $release.client_asset -notmatch '^SCFA-ContentCenter-[A-Za-z0-9.-]+\.exe$') { throw 'Invalid release configuration.' }
$channel = if ($release.client_version -match '-dev\d+$') { 'developer' } elseif ($release.client_version -match '-(?:beta|rc)\d+$') { 'beta' } else { 'stable' }
$prerelease = $channel -ne 'stable'
if ($release.PSObject.Properties.Name -contains 'client_channel' -and $release.client_channel -cne $channel) { throw 'Requested channel differs from version stage.' }
$publishGateway = $true
if ($release.PSObject.Properties.Name -contains 'publish_gateway') {
    if ($release.publish_gateway -isnot [bool]) { throw 'publish_gateway must be a boolean.' }
    $publishGateway = $release.publish_gateway
}
[xml]$project = Get-Content -LiteralPath $ProjectPath -Raw
if ($project.Project.PropertyGroup.InformationalVersion -cne $release.client_version -or
    $project.Project.PropertyGroup.FileVersion -cne $release.file_version) { throw 'Release version differs from source.' }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid repository.' }
$result = [pscustomobject]@{
    client_tag=$release.client_tag; gateway_tag=$release.gateway_tag; channel=$channel
    prerelease=$prerelease; publish_gateway=$publishGateway
}
if ($Mode -eq 'Validate') {
    if ($env:GITHUB_OUTPUT) {
        foreach ($name in @('client_tag','gateway_tag','channel','prerelease','publish_gateway')) {
            $value = $result.$name.ToString().ToLowerInvariant()
            "$name=$value" >> $env:GITHUB_OUTPUT
        }
    }
    return $result
}
if (!$PackagePath -or !$OutputDirectory -or $SourceCommit -notmatch '^[a-f0-9]{40}$') { throw 'Package, output directory and exact source commit are required.' }
$output = New-Item -ItemType Directory -Path $OutputDirectory -Force
$asset = Join-Path $output.FullName $release.client_asset
Copy-Item -LiteralPath $PackagePath -Destination $asset -ErrorAction Stop
$file = Get-Item -LiteralPath $asset
if ($file.VersionInfo.ProductVersion.Split('+')[0] -cne $release.client_version -or
    $file.VersionInfo.FileVersion -cne $release.file_version) { throw 'Published PE version differs from release.' }
$hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText($asset + '.sha256', $hash + '  ' + $release.client_asset + [Environment]::NewLine, $utf8)
$manifest = @{
    version=$release.client_version; channel=$channel
    url="https://github.com/$Repository/releases/download/$($release.client_tag)/$($release.client_asset)"
    sha256=$hash; size=$file.Length
    published_at=$(if ($Draft) { '' } else { [DateTimeOffset]::UtcNow.ToString('o') })
    notes='客户端更新与回滚、备份恢复、账号边界修复；支持受控HTTPS跳转。dev61首次需手动迁移。'
}
$manifestName = if ($Draft) { 'update-manifest.draft.json' } else { 'update-manifest.json' }
[IO.File]::WriteAllText((Join-Path $output.FullName $manifestName), ($manifest | ConvertTo-Json), $utf8)
$identity = @{
    source_commit=$SourceCommit; version=$release.client_version; file_version=$release.file_version
    product_version=$file.VersionInfo.ProductVersion; size=$file.Length; sha256=$hash
    self_contained=$true; single_file=$true; channel=$channel
    status=$(if ($Draft) { 'candidate-not-published' } else { 'prepared-for-publication' })
}
[IO.File]::WriteAllText((Join-Path $output.FullName 'release-info.json'), ($identity | ConvertTo-Json), $utf8)
$stage = if ($channel -eq 'developer') { '开发版' } elseif ($channel -eq 'beta') { '正式版候选/测试版' } else { '正式版' }
$notes = @(
    "# SCFA 内容中心 $($release.client_version)"
    ''
    "Windows x64 自包含单文件便携包，无需预装 .NET。版本阶段：$stage。"
    ''
    '更新校验、HTTPS跳转、正文超时、初始化失败回滚、备份恢复和账号连接边界修复。'
    '核心/WPF/实际单文件更新与回滚门槛通过后准备，SHA-256与来源见附件。'
    ''
    'dev61首次迁移：关闭程序、备份原exe、核对新包后手动替换；保留配置、Maps/Mods和备份。'
    "更新清单通道：$channel。实际通道接入与服务器部署是独立验收。"
    ''
    "Source commit: $SourceCommit"
)
[IO.File]::WriteAllText((Join-Path $output.FullName 'release-notes.md'), [string]::Join([Environment]::NewLine,$notes), $utf8)
return $identity
