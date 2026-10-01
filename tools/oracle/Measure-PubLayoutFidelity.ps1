<#
WI-002 scoring pass (no Publisher needed): for every exported Pubsmith document, render each page with
pubsmith into a SHORT temp path (ImageMagick cannot open paths over 260 characters) and score it against
Publisher's own 300 dpi reference PNG. Adds per-element counts from the .import.json report.
Writes <OutRoot>\import-report.csv. A page that cannot be scored is reported as UNSCORED, never left blank.
#>
param(
    [string]$HarvestRoot = $env:PUBSMITH_HARVEST,
    [string]$OutRoot,
    [string]$PubsmithCli = (Join-Path $PSScriptRoot '..\..\src\Pubsmith.Cli\bin\Release\net10.0\pubsmith.dll'),
    [double]$FlagAbove = 3.0
)
$ErrorActionPreference = 'Stop'
if (-not $HarvestRoot) { throw 'Pass -HarvestRoot or set PUBSMITH_HARVEST to your harvest folder.' }
if (-not $OutRoot) { $OutRoot = Join-Path $HarvestRoot 'funcular' }
$work = Join-Path ([IO.Path]::GetTempPath()) "plf-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
$rows = [Collections.Generic.List[object]]::new()

function Score([string]$golden, [string]$render) {
    $a = Join-Path $work 'a.png'; $b = Join-Path $work 'b.png'
    Copy-Item -LiteralPath $golden -Destination (Join-Path $work 'g.png') -Force   # short path for magick
    magick (Join-Path $work 'g.png') -background white -flatten -resize '800x800!' -colorspace gray -blur 0x1.5 $a
    magick $render -background white -flatten -resize '800x800!' -colorspace gray -blur 0x1.5 $b
    $r = (magick compare -metric RMSE $a $b null: 2>&1 | Out-String).Trim()
    if ($r -match '\(([\d.eE+-]+)\)') { [math]::Round([double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture) * 100, 2) } else { $null }
}

try {
    foreach ($src in (Import-Csv (Join-Path $HarvestRoot 'manifest.csv') | Where-Object Status -eq 'OK' | ForEach-Object Source)) {
        $full = [IO.Path]::GetFullPath($src)
        $rel = $full.Substring(0, 1) + $full.Substring(2)
        $name = [IO.Path]::GetFileNameWithoutExtension($src)
        $json = Join-Path $OutRoot "$(Split-Path $rel)\$name.json"
        $row = [ordered]@{ Source = $src; Corpus = ($src -like '*\test-corpus\*'); Status = ''; Pages = 0; Native = 0; Flattened = 0; Approximated = 0; FontsSubstituted = ''; MaxScore = ''; PageScores = ''; Flag = ''; Error = '' }
        try {
            if (-not (Test-Path -LiteralPath $json)) { throw 'no exported document' }
            $report = @(Get-Content -LiteralPath (Join-Path (Split-Path $json) "$name.import.json") -Raw | ConvertFrom-Json)
            $row.Native = @($report | Where-Object kind -eq 'native').Count
            $row.Flattened = @($report | Where-Object kind -eq 'flattened').Count
            $row.Approximated = @($report | Where-Object kind -eq 'approximated').Count
            Get-ChildItem $work -Filter 'r*.png' | Remove-Item
            $fp = & dotnet $PubsmithCli render $json --png (Join-Path $work 'r.png') --dpi 300 2>&1 | Out-String
            if ($LASTEXITCODE -ne 0) { throw "pubsmith exit ${LASTEXITCODE}: $fp" }
            $row.FontsSubstituted = (([regex]::Matches($fp, "Font '([^']+)' is not installed") | ForEach-Object { $_.Groups[1].Value }) | Sort-Object -Unique) -join '; '
            $pages = [int](Get-Content -LiteralPath $json -Raw | ConvertFrom-Json).pages.Count
            $row.Pages = $pages
            $scores = for ($i = 1; $i -le $pages; $i++) {
                $render = if ($pages -eq 1) { Join-Path $work 'r.png' } else { Join-Path $work "r-p$i.png" }
                $golden = Join-Path $HarvestRoot "png\$(Split-Path $rel)\$name-p$i.png"
                if ((Test-Path -LiteralPath $golden) -and (Test-Path -LiteralPath $render)) { Score $golden $render } else { $null }
            }
            $scores = @($scores)
            $row.PageScores = ($scores | ForEach-Object { if ($null -eq $_) { 'n/a' } else { $_.ToString([Globalization.CultureInfo]::InvariantCulture) } }) -join ' '
            $valid = @($scores | Where-Object { $null -ne $_ })
            if ($valid.Count -lt $pages) { $row.Flag = 'UNSCORED' }
            if ($valid.Count) {
                $row.MaxScore = ($valid | Measure-Object -Maximum).Maximum
                if ($row.MaxScore -gt $FlagAbove) { $row.Flag = (@($row.Flag, 'REVIEW') | Where-Object { $_ }) -join ' ' }
            }
            $row.Status = 'OK'
        }
        catch { $row.Status = 'FAIL'; $row.Error = "$($_.Exception.Message)".Trim() }
        $rows.Add([pscustomobject]$row)
    }
    $rows | Export-Csv (Join-Path $OutRoot 'import-report.csv') -NoTypeInformation
    $rows
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
