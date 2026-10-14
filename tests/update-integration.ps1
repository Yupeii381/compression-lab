param(
    [string]$BaseRelease = "$PSScriptRoot/../artifacts/releases/CompressionLab-1.3.0-win-x64",
    [string]$NewPackage = "$PSScriptRoot/../artifacts/update-test-packages/CompressionLab-1.3.1-win-x64.zip",
    [int]$Port = 18082,
    [int]$FeedPort = 18083
)

$ErrorActionPreference = 'Stop'
$release = (Resolve-Path -LiteralPath $BaseRelease).Path
$package = (Resolve-Path -LiteralPath $NewPackage).Path
$testRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../artifacts/update-tests/$([Guid]::NewGuid().ToString('N'))"))
New-Item -ItemType Directory -Force $testRoot | Out-Null
$installation = Join-Path $testRoot 'installed app'
$data = Join-Path $testRoot 'user data'
Copy-Item -LiteralPath $release -Destination $installation -Recurse
$baseUrl = "http://127.0.0.1:$Port"
$feed = "http://127.0.0.1:$FeedPort/feed.json"
$server = $null
$initial = $null
$startingVersion = (Get-Content (Join-Path $release 'release-info.json') -Raw | ConvertFrom-Json).version
$archiveMetadata = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $infoEntry = $archiveMetadata.Entries | Where-Object FullName -Like '*/release-info.json' | Select-Object -First 1
    $reader = [IO.StreamReader]::new($infoEntry.Open())
    try { $targetVersion = ($reader.ReadToEnd() | ConvertFrom-Json).version } finally { $reader.Dispose() }
} finally { $archiveMetadata.Dispose() }

function Assert-Update($condition, [string]$message) { if (-not $condition) { throw $message } }
function Set-Feed([string]$archive, [bool]$badHash = $false) {
    $checksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($badHash) { $checksum = '0' * 64 }
    @{
        package=$archive
        manifest=@{format=1;version=$targetVersion;runtime='win-x64';url="http://127.0.0.1:$FeedPort/package.zip";
            sha256=$checksum;size=(Get-Item -LiteralPath $archive).Length;notes='Integration update'}
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $testRoot 'server-config.json') -Encoding utf8
}
function Wait-Condition([scriptblock]$condition, [int]$seconds = 40) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    do {
        try { if (& $condition) { return } } catch { }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Condition timed out. Logs: $testRoot"
}
function Request-Install {
    $checked = Invoke-RestMethod "$baseUrl/api/updates/check" -Method Post
    Assert-Update ($checked.state -eq 'available') "Update not offered: $($checked.message)"
    Invoke-RestMethod "$baseUrl/api/updates/install" -Method Post | Out-Null
}

try {
    dotnet build "$PSScriptRoot/CompressionLab.Codecs.TestPlugin" -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Test plugin build failed' }
    $plugins = Join-Path $installation 'plugins/External'
    New-Item -ItemType Directory -Force $plugins | Out-Null
    $pluginFile = "$PSScriptRoot/CompressionLab.Codecs.TestPlugin/bin/Release/net10.0/CompressionLab.Codecs.TestPlugin.dll"
    Copy-Item -LiteralPath $pluginFile -Destination $plugins
    $pluginHash = (Get-FileHash -LiteralPath $pluginFile).Hash

    Set-Feed $package $true
    $serverArgs = "`"$PSScriptRoot/update-feed-server.cjs`" `"$testRoot`" $FeedPort"
    $server = Start-Process node -ArgumentList $serverArgs -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $testRoot 'feed.out.log') -RedirectStandardError (Join-Path $testRoot 'feed.err.log')
    $appArgs = "--no-browser --port $Port --data-dir `"$data`" --update-feed $feed"
    $initial = Start-Process (Join-Path $installation 'CompressionLab.Web.exe') -ArgumentList $appArgs -WindowStyle Hidden -PassThru `
        -WorkingDirectory $testRoot -RedirectStandardOutput (Join-Path $testRoot 'app.out.log') -RedirectStandardError (Join-Path $testRoot 'app.err.log')
    Wait-Condition { (Invoke-RestMethod "$baseUrl/api/health").version -eq $startingVersion }
    & "$PSScriptRoot/http-smoke.ps1" -BaseUrl $baseUrl -ExpectedCodecCount 6
    Invoke-RestMethod "$baseUrl/api/queue/pause" -Method Post | Out-Null
    $before = Invoke-RestMethod "$baseUrl/api/state"
    $request = @{fileIds=@($before.files[0].id);chains=@(@{name='External plugin';steps=@(@{id='test-identity';options=@{}})})} | ConvertTo-Json -Depth 10
    Invoke-RestMethod "$baseUrl/api/run" -Method Post -ContentType 'application/json' -Body $request | Out-Null
    $before = Invoke-RestMethod "$baseUrl/api/state"

    Request-Install
    Wait-Condition { (Invoke-RestMethod "$baseUrl/api/updates").state -eq 'error' }
    Assert-Update ((Invoke-RestMethod "$baseUrl/api/health").processId -eq $initial.Id) 'Checksum failure stopped old application'
    Write-Output 'PASS: checksum mismatch rejected before shutdown.'

    # The new helper can run, but the new host lacks its builtin codec assembly and cannot start.
    $broken = Join-Path $testRoot 'broken-host.zip'
    Copy-Item -LiteralPath $package -Destination $broken
    $zip = [IO.Compression.ZipFile]::Open($broken, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $zip.Entries | Where-Object FullName -Like '*/CompressionLab.Codecs.BuiltIn.dll' | Select-Object -First 1
        Assert-Update ($null -ne $entry) 'Builtin assembly not found in test package'
        $entry.Delete()
    } finally { $zip.Dispose() }
    Set-Feed $broken
    Request-Install
    Wait-Condition {
        $health = Invoke-RestMethod "$baseUrl/api/health"
        $result = Get-Content (Join-Path $data '.updates/last-result.json') -Raw | ConvertFrom-Json
        $health.version -eq $startingVersion -and $health.processId -ne $initial.Id -and $result.state -eq 'error'
    } 60
    Assert-Update ((Get-FileHash (Join-Path $plugins 'CompressionLab.Codecs.TestPlugin.dll')).Hash -eq $pluginHash) 'Rollback lost custom plugin'
    Write-Output 'PASS: failed new host rolled back and restarted previous version.'

    Set-Feed $package
    Request-Install
    Wait-Condition { (Invoke-RestMethod "$baseUrl/api/health").version -eq $targetVersion } 60
    Wait-Condition { (Get-Content (Join-Path $data '.updates/last-result.json') -Raw | ConvertFrom-Json).state -eq 'updated' }
    $after = Invoke-RestMethod "$baseUrl/api/state"
    Assert-Update ($after.paused -and $after.files[0].id -eq $before.files[0].id -and $after.jobs.Count -eq $before.jobs.Count) 'Update lost data or queue'
    Assert-Update ($after.codecs.id -contains 'test-identity') 'Custom codec not loaded after update'
    Assert-Update ($after.diagnostics.Count -eq 0) 'Plugin diagnostics after update'
    Assert-Update ((Get-FileHash (Join-Path $plugins 'CompressionLab.Codecs.TestPlugin.dll')).Hash -eq $pluginHash) 'Custom plugin bytes changed'
    Invoke-RestMethod "$baseUrl/api/queue/pause" -Method Post | Out-Null
    Wait-Condition { ((Invoke-RestMethod "$baseUrl/api/state").jobs | Select-Object -Last 1).status -eq 'done' }
    Write-Output 'PASS: downloaded update installed, app restarted, data and queue preserved, external codec survived and executed.'
} finally {
    # Stop only test processes inside this newly created test directory, never a user's running release.
    Get-Process -Name CompressionLab.Web -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            if ($_.Path -and $_.Path.StartsWith($testRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $_.Id
            }
        } catch { }
    }
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id }
    Write-Output "Integration artifacts: $testRoot"
}
