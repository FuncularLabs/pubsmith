<#
WI-002 batch: run Export-PubLayout.ps1 over every source in the harvest manifest, render each result with
pubsmith, and score every page against Publisher's own 300 dpi reference PNG (same metric as
ImageComparer in src/Pubsmith.Rendering). Writes <OutRoot>\import-report.csv.

Each file runs in its own pwsh process with a timeout. On timeout only that child's Publisher is stopped
(the ids it recorded, or an automation instance started since; never a Publisher the user opened). It refuses to
start while Publisher is open: each child starts and quits its own copy. A file counts as exported only
if the child exited 0 AND its JSON was written during this run (a stale JSON from an earlier run is never
scored as if it were fresh). Every file is redone on every run: no skip markers, so no stale-skip bugs.
#>
param(
    [string]$HarvestRoot = $env:PUBSMITH_HARVEST,
    [string]$OutRoot,
    [string]$PubsmithCli = (Join-Path $PSScriptRoot '..\..\src\Pubsmith.Cli\bin\Release\net10.0\pubsmith.dll'),
    [int]$TimeoutSeconds = 900,
    [double]$FlagAbove = 3.0,
    [string]$Filter = '*'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PublisherProcesses.ps1')
Assert-PublisherClosed
if (-not $HarvestRoot) { throw 'Pass -HarvestRoot or set PUBSMITH_HARVEST to your harvest folder.' }
if (-not $OutRoot) { $OutRoot = Join-Path $HarvestRoot 'funcular' }
$exporter = Join-Path $PSScriptRoot 'Export-PubLayout.ps1'
if (-not (Test-Path $PubsmithCli)) { throw "pubsmith not built at $PubsmithCli (dotnet build -c Release)" }
$sources = Import-Csv (Join-Path $HarvestRoot 'manifest.csv') | Where-Object { $_.Status -eq 'OK' -and $_.Source -like $Filter } | ForEach-Object Source
$rows = [Collections.Generic.List[object]]::new()

function Mirror([string]$p) { $f = [IO.Path]::GetFullPath($p); $f.Substring(0, 1) + $f.Substring(2) }

function Score([string]$golden, [string]$render, [string]$work) {
    $a = Join-Path $work 'a.png'; $b = Join-Path $work 'b.png'
    magick $golden -background white -flatten -resize '800x800!' -colorspace gray -blur 0x1.5 $a
    magick $render -background white -flatten -resize '800x800!' -colorspace gray -blur 0x1.5 $b
    $r = (magick compare -metric RMSE $a $b null: 2>&1 | Out-String).Trim()
    if ($r -match '\(([\d.eE+-]+)\)') { [math]::Round([double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture) * 100, 2) } else { $null }
}

foreach ($src in $sources) {
    $rel = Mirror $src
    $name = [IO.Path]::GetFileNameWithoutExtension($src)
    $outDir = Join-Path $OutRoot (Split-Path $rel)
    $json = Join-Path $outDir "$name.json"
    $row = [ordered]@{ Source = $src; Status = ''; Seconds = 0; Pages = 0; Native = 0; Flattened = 0; Approximated = 0; FontsSubstituted = ''; MaxScore = ''; PageScores = ''; Flag = ''; Error = '' }
    $started = Get-Date
    $p = $null
    $log = Join-Path ([IO.Path]::GetTempPath()) "publayout-child-$PID.log"
    $pidFile = "$log.pids"
    Remove-Item -LiteralPath $log, "$log.err", $pidFile -ErrorAction SilentlyContinue
    try {
        $p = Start-Process pwsh -ArgumentList '-NoProfile', '-File', "`"$exporter`"", '-Source', "`"$src`"", '-OutDir', "`"$outDir`"", '-PidFile', "`"$pidFile`"" `
            -PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError "$log.err"
        if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
            $p.Kill($true)
            Stop-ChildPublisher $pidFile $started   # Publisher runs as a COM server, not as a child of that process
            throw "timed out after $TimeoutSeconds s"
        }
        $childErr = (Get-Content "$log.err" -Raw -ErrorAction SilentlyContinue)
        if ($p.ExitCode -ne 0) { throw "exporter exit $($p.ExitCode): $childErr" }
        if (-not (Test-Path -LiteralPath $json) -or (Get-Item -LiteralPath $json).LastWriteTime -lt $started) { throw 'exporter reported success but wrote no fresh JSON' }

        $report = @(Get-Content -LiteralPath (Join-Path $outDir "$name.import.json") -Raw | ConvertFrom-Json)
        $row.Native = @($report | Where-Object kind -eq 'native').Count
        $row.Flattened = @($report | Where-Object kind -eq 'flattened').Count
        $row.Approximated = @($report | Where-Object kind -eq 'approximated').Count

        $renderDir = Join-Path $outDir "$name.render"
        New-Item -ItemType Directory -Force $renderDir | Out-Null
        $png = Join-Path $renderDir "$name.png"
        $fp = & dotnet $PubsmithCli render $json --png $png --dpi 300 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { throw "pubsmith exit ${LASTEXITCODE}: $fp" }
        $row.FontsSubstituted = (([regex]::Matches($fp, "Font '([^']+)' is not installed") | ForEach-Object { $_.Groups[1].Value }) | Sort-Object -Unique) -join '; '

        $pages = [int](Get-Content -LiteralPath $json -Raw | ConvertFrom-Json).pages.Count
        $row.Pages = $pages
        $scores = @()
        for ($i = 1; $i -le $pages; $i++) {
            $render = if ($pages -eq 1) { $png } else { Join-Path $renderDir "$name-p$i.png" }
            $golden = Join-Path $HarvestRoot "png\$(Split-Path $rel)\$name-p$i.png"
            $scores += if (Test-Path -LiteralPath $golden) { Score $golden $render $renderDir } else { $null }
        }
        $row.PageScores = ($scores | ForEach-Object { if ($null -eq $_) { 'n/a' } else { $_.ToString([Globalization.CultureInfo]::InvariantCulture) } }) -join ' '
        $valid = @($scores | Where-Object { $null -ne $_ })
        if ($valid.Count) { $row.MaxScore = ($valid | Measure-Object -Maximum).Maximum }
        $row.Flag = if ($valid.Count -and $row.MaxScore -gt $FlagAbove) { 'REVIEW' } else { '' }
        $row.Status = 'OK'
    }
    catch { $row.Status = 'FAIL'; $row.Error = "$($_.Exception.Message)".Trim() }
    finally {
        Stop-ChildPublisher $pidFile $started   # also after a normal exit: a Publisher that outlived Quit would block the next file
        if ($p) { Remove-Item -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) "publayout-$($p.Id)") -Recurse -Force -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath $log, "$log.err", $pidFile -ErrorAction SilentlyContinue
    }
    $row.Seconds = [int]((Get-Date) - $started).TotalSeconds
    $rows.Add([pscustomobject]$row)
    Write-Host ("{0,-4} {1,5}s max {2,6} {3} {4}" -f $row.Status, $row.Seconds, $row.MaxScore, $name, $row.Error)
    $rows | Export-Csv (Join-Path $OutRoot 'import-report.csv') -NoTypeInformation   # checkpoint after every file
}
$rows | Group-Object Status | ForEach-Object { "{0}: {1}" -f $_.Name, $_.Count }
