<#
Runs INSIDE the PubOracle VM, in the signed-in desktop session (Task Scheduler "At log on").
Polls C:\PubOracle\inbox for *.pub; for each file, runs Export-PubHarvest.ps1 (which loads PublisherProcesses.ps1
from the same folder) into C:\PubOracle\out, then moves the source to C:\PubOracle\done (or C:\PubOracle\failed)
and writes <name>.status next to the outputs: OK or FAIL: <error>, once the job has finished either way. While the
VM rather than the file is the problem (Publisher running, Publisher failing to start, the export script stopping),
the file stays in the inbox with status WAITING: <reason>. WAITING is not final: the file is tried again on a later
poll, and the host (Invoke-PubOracle.ps1) keeps waiting for OK or FAIL.
A file is only picked up once its size has been stable for two polls (still-copying guard).
#>
param([string]$Root = 'C:\PubOracle', [int]$PollSeconds = 3)
$inbox = Join-Path $Root 'inbox'; $out = Join-Path $Root 'out'
$done = Join-Path $Root 'done'; $failed = Join-Path $Root 'failed'
New-Item -ItemType Directory -Force $inbox, $out, $done, $failed | Out-Null
$export = Join-Path $PSScriptRoot 'Export-PubHarvest.ps1'
$lastSize = @{}

while ($true) {
    foreach ($f in Get-ChildItem $inbox -Filter *.pub -File) {
        if ($lastSize[$f.FullName] -ne $f.Length) { $lastSize[$f.FullName] = $f.Length; continue }
        $status = Join-Path $out ($f.BaseName + '.status')
        # A Publisher still running (someone using the VM, or one that outlived Quit) is not this file's fault:
        # leave the file in the inbox, say why, and try again on a later poll.
        if (Get-Process MSPUB -ErrorAction SilentlyContinue) { Set-Content $status 'WAITING: Publisher is running'; continue }
        $lastSize.Remove($f.FullName)
        try { & $export -Sources $f.FullName -HarvestRoot $out *> (Join-Path $out ($f.BaseName + '.log')) }
        catch {
            # The export script itself stopped (it records a file's own failures in its manifest instead): a host
            # problem, so the file stays in the inbox.
            Set-Content $status "WAITING: $_"
            continue
        }
        try {
            $row = Import-Csv (Join-Path $out 'manifest.csv') | Where-Object Source -eq $f.FullName
            if ($row.Status -ne 'OK') { throw "export status '$($row.Status)': $($row.Error)" }
            Move-Item -LiteralPath $f.FullName -Destination $done -Force
            Set-Content $status 'OK'
        } catch {
            Move-Item -LiteralPath $f.FullName -Destination $failed -Force -ErrorAction SilentlyContinue
            Set-Content $status "FAIL: $_"
        }
    }
    Start-Sleep -Seconds $PollSeconds
}
