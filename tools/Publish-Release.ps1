param(
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version = '1.3.0',
    [string]$RepositoryUrl = 'https://github.com/Yupeii381/compression-lab',
    [string]$OutputRoot = "$PSScriptRoot/../artifacts/releases",
    [string]$Notes = ''
)

$ErrorActionPreference = 'Stop'
$repository = $RepositoryUrl.TrimEnd('/')
if ($repository -notmatch '^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw 'RepositoryUrl must identify an HTTPS GitHub repository'
}

$root = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Force -Path $root | Out-Null
$name = "CompressionLab-$Version-win-x64"
$directory = Join-Path $root $name
$project = Join-Path $PSScriptRoot '../src/CompressionLab.Web/CompressionLab.Web.csproj'
dotnet publish $project -p:PublishProfile=Windows-x64 "-p:Version=$Version" -o $directory --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$feed = "$repository/releases/latest/download/update-win-x64.json"
@{ manifestUrl=$feed; checkOnStartup=$true } | ConvertTo-Json | Set-Content (Join-Path $directory 'updates.json') -Encoding utf8
$files = @(Get-ChildItem -LiteralPath $directory -Recurse -File | ForEach-Object {
    [IO.Path]::GetRelativePath($directory, $_.FullName).Replace('\','/')
}) + @('release-info.json')
@{ product='CompressionLab'; version=$Version; runtime='win-x64'; files=@($files | Sort-Object -Unique) } |
    ConvertTo-Json -Depth 5 | Set-Content (Join-Path $directory 'release-info.json') -Encoding utf8

$archive = Join-Path $root "$name.zip"
Compress-Archive -LiteralPath $directory -DestinationPath $archive -CompressionLevel Optimal -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content "$archive.sha256" "$hash  $name.zip" -Encoding utf8
if (-not $Notes) { $Notes = "Новый выпуск Compression Lab $Version. Подробности в GitHub Releases." }
@{
    format=1; version=$Version; runtime='win-x64'; url="$repository/releases/download/v$Version/$name.zip"
    sha256=$hash; size=(Get-Item -LiteralPath $archive).Length; notes=$Notes
} | ConvertTo-Json | Set-Content (Join-Path $root 'update-win-x64.json') -Encoding utf8

Write-Output "Release ZIP: $archive"
Write-Output "Update feed: $feed"
