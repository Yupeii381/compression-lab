param(
    [string]$Executable = "$PSScriptRoot/../artifacts/releases/CompressionLab-1.3.0-win-x64/CompressionLab.Web.exe",
    [int]$Port = 18080
)

$ErrorActionPreference = 'Stop'
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$releaseDirectory = Split-Path -Parent $executablePath
$testDirectory = Join-Path $PSScriptRoot "../artifacts/release-tests/$([Guid]::NewGuid().ToString('N'))"
$testDirectory = [IO.Path]::GetFullPath($testDirectory)
New-Item -ItemType Directory -Force -Path $testDirectory | Out-Null
$dataDirectory = Join-Path $testDirectory 'data'
$baseUrl = "http://127.0.0.1:$Port"
$runtimeConfig = Get-Content (Join-Path $releaseDirectory 'CompressionLab.Web.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.frameworks -or -not $runtimeConfig.runtimeOptions.includedFrameworks) {
    throw 'Release is framework-dependent'
}

# Hide the machine-wide runtime from the apphost. Verify the loaded CLR path as well.
$previousRoot = $env:DOTNET_ROOT
$previousRootX64 = $env:DOTNET_ROOT_X64
$previousLookup = $env:DOTNET_MULTILEVEL_LOOKUP
$env:DOTNET_ROOT = Join-Path $testDirectory 'no-installed-runtime'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$process = $null

function Start-Release([string]$LogName) {
    $arguments = "--no-browser --port $Port --data-dir `"$dataDirectory`""
    # Deliberately launch outside the release folder: static assets must be located relative to the EXE.
    $started = Start-Process -FilePath $executablePath -ArgumentList $arguments -WorkingDirectory $testDirectory `
        -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testDirectory "$LogName.out.log") `
        -RedirectStandardError (Join-Path $testDirectory "$LogName.err.log")
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        if ($started.HasExited) { throw "Release exited with $($started.ExitCode). See $testDirectory" }
        try {
            $response = Invoke-RestMethod "$baseUrl/api/state" -TimeoutSec 1
            if ($response) { return $started }
        } catch { Start-Sleep -Milliseconds 200 }
    } while ([DateTime]::UtcNow -lt $deadline)
    Stop-Process -Id $started.Id
    throw 'Release did not start within 20 seconds'
}

try {
    $process = Start-Release 'first'
    $loadedClr = $process.Modules | Where-Object ModuleName -EQ 'coreclr.dll' | Select-Object -First 1
    if (-not $loadedClr -or [IO.Path]::GetDirectoryName($loadedClr.FileName) -ne $releaseDirectory) {
        throw 'Application did not load its bundled runtime'
    }

    $state = Invoke-RestMethod "$baseUrl/api/state"
    if ($state.diagnostics.Count) { throw "Plugin diagnostics: $($state.diagnostics)" }
    $appUrl = "$baseUrl/co-lab"
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler)
    try {
        foreach ($path in @('', '/co-lab')) {
            $redirect = $client.GetAsync("$baseUrl$path").GetAwaiter().GetResult()
            if ([int]$redirect.StatusCode -ne 302 -or $redirect.Headers.Location.OriginalString -ne '/co-lab/') {
                throw "Missing application path redirect: $path"
            }
            $redirect.Dispose()
        }
    } finally { $client.Dispose() }
    foreach ($asset in @('style.css', 'app.js', 'updates.js')) {
        $response = Invoke-WebRequest "$appUrl/$asset"
        if ($response.StatusCode -ne 200 -or -not $response.Content) { throw "Missing prefixed asset: $asset" }
    }
    $health = Invoke-RestMethod "$appUrl/api/health"
    if ($health.processId -ne $process.Id) { throw 'Prefixed health endpoint failed' }
    & "$PSScriptRoot/http-smoke.ps1" -BaseUrl $appUrl
    Invoke-RestMethod "$baseUrl/api/queue/pause" -Method Post | Out-Null
    $beforeRestart = Invoke-RestMethod "$baseUrl/api/state"
    Stop-Process -Id $process.Id
    $process.WaitForExit()
    $process = Start-Release 'restarted'
    $afterRestart = Invoke-RestMethod "$baseUrl/api/state"
    if (-not $afterRestart.paused -or $afterRestart.files.Count -ne $beforeRestart.files.Count -or
        $afterRestart.jobs[0].id -ne $beforeRestart.jobs[0].id -or $afterRestart.jobs[0].status -ne 'done') {
        throw 'State was lost after restarting release EXE'
    }

    $fixture = (Resolve-Path "$PSScriptRoot/Fixtures/legacy-rle-lzma.clcs").Path
    $restored = Join-Path $testDirectory 'cli-restored.txt'
    & $executablePath --restore $fixture $restored
    if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $restored -Algorithm SHA256).Hash -ne
        'BE22E5A6A253F65B7E0CE796DEC6B1B0D30C4779914394833DBB80A32FFC392A') {
        throw 'Standalone CLI restore failed'
    }

    Write-Output 'PASS: self-contained EXE, bundled CLR, unrelated working directory, HTTP workflows, persistence after restart, standalone CLI restore.'
} finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
    $env:DOTNET_ROOT = $previousRoot
    $env:DOTNET_ROOT_X64 = $previousRootX64
    $env:DOTNET_MULTILEVEL_LOOKUP = $previousLookup
}
