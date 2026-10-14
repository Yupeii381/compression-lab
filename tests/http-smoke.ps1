param([string]$BaseUrl = 'http://localhost/co-lab', [int]$ExpectedCodecCount = 5)
$ErrorActionPreference = 'Stop'
function Assert-Lab($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$state = Invoke-RestMethod "$BaseUrl/api/state"
Assert-Lab ($state.codecs.Count -eq $ExpectedCodecCount) 'Unexpected codec count'
$page = Invoke-WebRequest $BaseUrl
Assert-Lab ($page.Content.Contains('Исследуйте сжатие.')) 'Home page missing'
Assert-Lab ($page.Headers['Content-Security-Policy']) 'CSP missing'
$hostile = Invoke-WebRequest "$BaseUrl/api/demo" -Method Post -Headers @{ Origin = 'https://example.org' } -SkipHttpErrorCheck
Assert-Lab ($hostile.StatusCode -eq 403) 'Foreign browser origin accepted'
$invalid = Invoke-WebRequest "$BaseUrl/api/run" -Method Post -ContentType 'application/json' -Body '{"fileIds":[],"chains":[]}' -SkipHttpErrorCheck
Assert-Lab ($invalid.StatusCode -eq 400) 'Invalid experiment accepted'
$nullChain = Invoke-WebRequest "$BaseUrl/api/run" -Method Post -ContentType 'application/json' -Body '{"fileIds":["x"],"chains":[null]}' -SkipHttpErrorCheck
Assert-Lab ($nullChain.StatusCode -eq 400) 'Null chain must produce a validation error'
$body = [Text.Encoding]::UTF8.GetBytes(('Тестовый файл — ABCDEAAAAAAABBBBBBBB' * 3000))
$upload = Invoke-RestMethod "$BaseUrl/api/files" -Method Post -Headers @{ 'X-File-Name' = [Uri]::EscapeDataString('проверка.txt') } -Body $body -ContentType 'application/octet-stream'
if (-not $state.paused) { Invoke-RestMethod "$BaseUrl/api/queue/pause" -Method Post | Out-Null }
$request = @{ fileIds = @($upload.id); chains = @(@{ name = 'RLE → LZMA'; steps = @(@{ id = 'rle'; options = @{} }, @{ id = 'lzma'; options = @{dictionaryMiB=1} }) }); blockSize=65536; repeats=2; warmup=1 } | ConvertTo-Json -Depth 10
Invoke-RestMethod "$BaseUrl/api/run" -Method Post -ContentType 'application/json' -Body $request | Out-Null
$state = Invoke-RestMethod "$BaseUrl/api/state"
$job = $state.jobs | Where-Object fileId -EQ $upload.id | Select-Object -Last 1
Assert-Lab ($job.status -eq 'queued') 'Pause ignored'
Invoke-RestMethod "$BaseUrl/api/jobs/$($job.id)/cancel" -Method Post | Out-Null
$cancelled = (Invoke-RestMethod "$BaseUrl/api/state").jobs | Where-Object id -EQ $job.id
Assert-Lab ($cancelled.status -eq 'cancelled') 'Cancel failed'
Invoke-RestMethod "$BaseUrl/api/jobs/$($job.id)/retry" -Method Post | Out-Null
Invoke-RestMethod "$BaseUrl/api/queue/pause" -Method Post | Out-Null
$deadline = [DateTime]::UtcNow.AddSeconds(30)
do {
    Start-Sleep -Milliseconds 300
    $job = (Invoke-RestMethod "$BaseUrl/api/state").jobs | Where-Object id -EQ $job.id
} while ($job.status -in @('queued','running') -and [DateTime]::UtcNow -lt $deadline)
Assert-Lab ($job.status -eq 'done') "Experiment failed: $($job.status) $($job.error)"
Assert-Lab ($job.result.verified) 'Roundtrip not verified'
$download = Invoke-WebRequest "$BaseUrl/api/jobs/$($job.id)/archive"
Assert-Lab ($download.RawContentLength -eq $job.result.archiveBytes) 'Wrong archive size'
$restored = Invoke-WebRequest "$BaseUrl/api/restore" -Method Post -Body $download.Content -ContentType 'application/octet-stream'
Assert-Lab ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$restored.Content)) -eq $upload.sha256) 'Restored bytes differ'
$export = Invoke-RestMethod "$BaseUrl/api/export.json"
Assert-Lab ($export.jobs.Count -ge 1) 'Export empty'
$csv = Invoke-WebRequest "$BaseUrl/api/export.csv"
Assert-Lab ($csv.RawContentLength -gt 50) 'CSV empty'
Write-Output 'PASS: HTTP upload, plugins, validation, origin protection, pause/cancel/retry, chain execution, archive download, restore SHA-256, JSON and CSV.'
