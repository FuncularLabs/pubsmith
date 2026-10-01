<#
Harvest: export every .pub to PDF (commercial-print intent) and 300 dpi PNG page proofs
using Publisher itself, while it still runs. Originals are opened read-only and never modified;
output is mirrored under the harvest folder so no existing sibling PDF is ever overwritten.

Re-runnable: a file is skipped only when the manifest records status OK for the same source
SHA-256 AND its PDF still exists. Anything else (new, changed, failed, missing output) is redone.
#>
param(
    [Parameter(Mandatory)][string[]]$Sources,           # .pub paths
    [string]$HarvestRoot = $env:PUBSMITH_HARVEST
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PublisherProcesses.ps1')
Assert-PublisherClosed
if (-not $HarvestRoot) { throw 'Pass -HarvestRoot or set PUBSMITH_HARVEST to your harvest folder.' }
$manifest = Join-Path $HarvestRoot 'manifest.csv'
$prior = @{}
if (Test-Path $manifest) { Import-Csv $manifest | ForEach-Object { $prior[$_.Source] = $_ } }

function Get-RelPath([string]$p) {
    # D:\docs\x\y.pub -> D\docs\x\y  (drive letter kept so files on different drives never collide)
    $full = [IO.Path]::GetFullPath($p)
    ($full.Substring(0,1) + $full.Substring(2)) -replace '\.pub$',''
}

# Every checkpoint keeps the manifest rows of sources this run has not reached yet, so an interrupted run loses
# nothing; it is written to a temporary file and moved into place, so a crash mid-write cannot truncate it.
function Save-Manifest {
    $seen = @{}; $rows | ForEach-Object { $seen[$_.Source] = 1 }
    $all = @($rows) + @($prior.Values | Where-Object { -not $seen[$_.Source] })
    $tmp = "$manifest.$PID.tmp"
    $all | Sort-Object Source | Export-Csv $tmp -NoTypeInformation
    Move-Item -LiteralPath $tmp -Destination $manifest -Force
    $all
}

$pub = $null
$rows = [Collections.Generic.List[object]]::new()
foreach ($src in $Sources) {
    $hash = (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash
    $rel  = Get-RelPath $src
    $pdf  = Join-Path $HarvestRoot "pdf\$rel.pdf"
    $pngBase = Join-Path $HarvestRoot "png\$rel"
    $p = $prior[$src]
    if ($p -and $p.Status -eq 'OK' -and $p.Sha256 -eq $hash -and (Test-Path -LiteralPath $pdf)) {
        $rows.Add($p); Write-Host "SKIP  $src"; continue
    }
    $row = [ordered]@{ Source=$src; Sha256=$hash; Pdf=$pdf; Pages=''; WidthIn=''; HeightIn=''; Pngs=0; Status=''; Error=''; At=(Get-Date -f s) }
    $d = $null
    # Starting Publisher is outside the per-file try: if it cannot start (not installed, activation lapsed), that is
    # the host's problem, so the run stops instead of recording every file as failed.
    if (-not $pub) { $pub = New-Object -ComObject Publisher.Application }
    try {
        New-Item -ItemType Directory -Force (Split-Path $pdf), (Split-Path $pngBase) | Out-Null
        $d = $pub.Open($src, $true)                                  # read-only
        $row.Pages = $d.Pages.Count
        $row.WidthIn = [math]::Round($d.PageSetup.PageWidth / 72, 3)
        $row.HeightIn = [math]::Round($d.PageSetup.PageHeight / 72, 3)
        if (Test-Path -LiteralPath $pdf) { Remove-Item -LiteralPath $pdf }  # our own stale output only
        $d.ExportAsFixedFormat(2, $pdf, 4)                            # 2 = PDF, 4 = commercial press
        if (-not (Test-Path -LiteralPath $pdf) -or (Get-Item -LiteralPath $pdf).Length -eq 0) { throw "PDF not produced" }
        for ($i = 1; $i -le $d.Pages.Count; $i++) {
            $d.Pages($i).SaveAsPicture("$pngBase-p$i.png", 3)         # 3 = 300 dpi
            $row.Pngs++
        }
        $row.Status = 'OK'
        Write-Host "OK    $src"
    } catch {
        $row.Status = 'FAIL'; $row.Error = "$_"
        Write-Host "FAIL  $src :: $_"
        try { $pub.Quit() } catch {}                                # restart Publisher after any failure
        try { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($pub) } catch {}; $pub = $null
    } finally {
        if ($d) { try { $d.Close() } catch {} }
    }
    $rows.Add([pscustomobject]$row)
    Save-Manifest | Out-Null                                         # checkpoint after every file
}
if ($pub) { $pub.Quit(); [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($pub) }   # released, so it can exit
$all = Save-Manifest
$all | Group-Object Status | ForEach-Object { "{0}: {1}" -f $_.Name, $_.Count }
