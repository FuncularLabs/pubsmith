<#
WI-004: Publisher's ground truth for every diff-corpus variant, while Publisher still runs.
  - page renders (PDF + 300 dpi PNG) via Export-PubHarvest.ps1 into the harvest's pdf\ and png\ mirrors
  - layout JSON (+ flattened pictures + element report) via Export-PubLayout.ps1 into <group>\truth\
Each layout export runs in its own process (a hang can't stop the batch); on timeout only that child's Publisher
is stopped. It refuses to start while Publisher is open. Results: <CorpusDir>\truth.csv.
#>
param(
    [string]$CorpusDir = "C:\pubsmith-corpus\diff-corpus",
    [string]$HarvestRoot = "C:\pubsmith-corpus\harvest",
    [int]$TimeoutSeconds = 900
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PublisherProcesses.ps1')
Assert-PublisherClosed
$pubs = Get-ChildItem $CorpusDir -Recurse -Filter *.pub | Where-Object { $_.DirectoryName -notmatch '\\truth$' } | ForEach-Object FullName
& (Join-Path $PSScriptRoot 'Export-PubHarvest.ps1') -Sources $pubs -HarvestRoot $HarvestRoot | Select-Object -Last 2

$rows = [Collections.Generic.List[object]]::new()
foreach ($p in $pubs) {
    $out = Join-Path (Split-Path $p) 'truth'
    $started = Get-Date
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "dct-$PID"
    Remove-Item -LiteralPath "$tmp.out", "$tmp.err", "$tmp.pids" -ErrorAction SilentlyContinue
    $proc = Start-Process pwsh -ArgumentList '-NoProfile', '-File', "`"$(Join-Path $PSScriptRoot 'Export-PubLayout.ps1')`"", '-Source', "`"$p`"", '-OutDir', "`"$out`"", '-PidFile', "`"$tmp.pids`"" -PassThru -NoNewWindow -RedirectStandardOutput "$tmp.out" -RedirectStandardError "$tmp.err"
    $status = 'OK'; $err = ''
    if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
        $proc.Kill($true)
        Stop-ChildPublisher "$tmp.pids" $started
        $status = 'FAIL'; $err = 'timeout'
    } elseif ($proc.ExitCode -ne 0) { $status = 'FAIL'; $err = (Get-Content "$tmp.err" -Raw) }
    Stop-ChildPublisher "$tmp.pids" $started   # also after a normal exit: a Publisher that outlived Quit would block the next file
    Remove-Item -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) "publayout-$($proc.Id)") -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath "$tmp.out", "$tmp.err", "$tmp.pids" -ErrorAction SilentlyContinue
    $rows.Add([pscustomobject]@{ Source = $p; Status = $status; Seconds = [int]((Get-Date) - $started).TotalSeconds; Error = "$err".Trim() })
    $rows | Export-Csv (Join-Path $CorpusDir 'truth.csv') -NoTypeInformation
}
$rows | Group-Object Status | ForEach-Object { "{0}: {1}" -f $_.Name, $_.Count }
