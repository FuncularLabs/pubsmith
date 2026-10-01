<#
WI-002: export ONE .pub to a Pubsmith JSON document, using Publisher itself (COM) to read the layout.

  Editable ("native"):  text boxes (runs, font, size, bold/italic, colour, alignment, insets, rotation),
                        rectangles and ovals with solid fill / solid line, and groups made only of those.
  Flattened:            everything else. In a separate throw-away copy of the document, every other element
                        (page and master) is deleted and Publisher renders the page twice: once on white, once over
                        a temporary black backdrop. matte.py recovers exact colour + alpha from the pair, cropped
                        to the visible pixels. The page export's scale (300 dpi) and origin are known, so the
                        element is placed exactly; its rotation is baked into the pixels.
  Master pages:         master elements are emitted behind each page that shows the master (the model has no
                        master pages yet); a master element is flattened once and its picture reused.

Writes <OutDir>\<name>.json, <OutDir>\<name>.assets-<stamp>-<pid>\*.png and <OutDir>\<name>.import.json (per-element report).
The JSON is written to a temp file and renamed only after the whole file succeeded (never a partial document).
The document being read is opened read-only and never modified; isolation copies are closed unsaved.
Run one file per process (Invoke-PubLayoutBatch.ps1) so a hang can be killed without losing the batch.
With -PidFile, the ids of the Publisher processes this run starts are appended to that file, so a caller that
times out can stop exactly those. Run it with Publisher closed: it starts and quits its own Publisher.
#>
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$OutDir,
    [string]$PidFile
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PublisherProcesses.ps1')
if (-not $PidFile) { Assert-PublisherClosed }   # a batch caller (which passes -PidFile) checked before its first file
$name = [IO.Path]::GetFileNameWithoutExtension($Source)
# Assets go to a NEW folder per run, named in the JSON. Deleting the previous folder first failed when a file-sync
# client was still syncing it; a unique folder never needs that, and the JSON always matches its own pictures.
$assetsName = "$name.assets-" + (Get-Date -Format 'yyyyMMddHHmmss') + "-$PID"
$assets = Join-Path $OutDir $assetsName
$matte = Join-Path $PSScriptRoot 'matte.py'
$work = Join-Path ([IO.Path]::GetTempPath()) "publayout-$PID"
New-Item -ItemType Directory -Force $OutDir, $work, $assets | Out-Null

# Starts a Publisher automation instance and records the process it added (for a caller's timeout kill).
function New-Publisher {
    $before = @(Get-Process MSPUB -ErrorAction SilentlyContinue | ForEach-Object Id)
    $app = New-Object -ComObject Publisher.Application
    $started = @(Get-Process MSPUB -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id } | ForEach-Object Id)
    if ($PidFile -and $started.Count) { Add-Content -LiteralPath $PidFile -Value $started }
    $app
}

$report = [Collections.Generic.List[object]]::new()
$script:assetIndex = 0
$script:page = $null          # the Publisher page being converted
$script:onMaster = $false     # true while converting that page's master shapes
$script:flatCache = @{}       # master/page + z-order -> flattened element (or $null when nothing is visible)
$script:scratchApp = $null    # second Publisher instance used only for throw-away isolation copies

class GroupNeedsFlatten : Exception { GroupNeedsFlatten([string]$m) : base($m) {} }

function Hex([int]$bgr, [double]$transparency = 0) {
    $r = $bgr -band 0xFF; $g = ($bgr -shr 8) -band 0xFF; $b = ($bgr -shr 16) -band 0xFF
    $a = [int][math]::Round(255 * (1 - $transparency))
    if ($a -ge 255) { return '#{0:X2}{1:X2}{2:X2}' -f $r, $g, $b }
    '#{0:X2}{1:X2}{2:X2}{3:X2}' -f $r, $g, $b, $a
}

function BoxOf($s) { [ordered]@{ x = [double]$s.Left; y = [double]$s.Top; width = [double]$s.Width; height = [double]$s.Height } }

function Note($s, [string]$kind, [string]$detail) {
    $report.Add([ordered]@{ page = $script:pageIndex; shape = "$($s.Name)"; master = $script:onMaster; kind = $kind; detail = $detail })
}

function Flatten($s, [string]$reason) {
    # The target is always a top-level shape (group children escalate to their group), so its z-order
    # position identifies it uniquely within its own collection. COM wrappers can't be compared by identity.
    $targetZ = [int]$s.ZOrderPosition
    # A master element repeats on every page that shows that master: flatten it once and reuse the picture.
    $cacheKey = if ($script:onMaster) { "M|$($script:page.Master.Name)|$targetZ" } else { "P|$($script:pageIndex)|$targetZ" }
    if ($script:flatCache.ContainsKey($cacheKey)) {
        $hit = $script:flatCache[$cacheKey]
        if ($null -eq $hit) { Note $s 'empty' "$reason (nothing visible)"; return @() }
        Note $s 'flattened' "$reason (same master picture as an earlier page)"
        return , $hit
    }

    # Isolation happens in a SEPARATE, throw-away copy of the document: moving or deleting elements in the
    # document being read changes page/master ownership (an element dragged off a master comes back as a page
    # element), so the source document is never modified.
    if (-not $script:scratchApp) {
        $script:scratchApp = New-Publisher
        # Publisher refuses to open a file that is already open ("detected a problem in the file"),
        # so isolation copies are opened from a private copy of the source.
        $script:scratchSource = Join-Path $work ("source" + [IO.Path]::GetExtension($Source))
        Copy-Item -LiteralPath $Source -Destination $script:scratchSource -Force
    }
    $w = Join-Path $work 'w.png'; $b = Join-Path $work 'b.png'
    $fd = $script:scratchApp.Open($script:scratchSource, $true)
    try {
        $pg = $fd.Pages($script:pageIndex)
        $master = $null; try { $master = $pg.Master } catch {}
        $doomed = [Collections.Generic.List[object]]::new()   # collect first: deleting renumbers z-order positions
        foreach ($o in @($pg.Shapes)) { if ($script:onMaster -or [int]$o.ZOrderPosition -ne $targetZ) { $doomed.Add($o) } }
        if ($master) { foreach ($o in @($master.Shapes)) { if (-not $script:onMaster -or [int]$o.ZOrderPosition -ne $targetZ) { $doomed.Add($o) } } }
        foreach ($o in $doomed) { $o.Delete() }
        $left = $pg.Shapes.Count + $(if ($master) { $master.Shapes.Count } else { 0 })
        if ($left -ne 1) { throw "isolation failed: $left elements left for '$($s.Name)'" }
        $pg.IgnoreMaster = $false                                        # Boolean; the master now holds only the target (or nothing)
        $pg.SaveAsPicture($w, 3)
        # The backdrop must sit behind the target: on the master page for master targets.
        # Assign directly: returning a COM collection from an if-expression would unroll it into an array.
        if ($script:onMaster) { $backdropHost = $master.Shapes } else { $backdropHost = $pg.Shapes }
        $backdrop = $backdropHost.AddShape(1, -20, -20, [double]$fd.PageSetup.PageWidth + 40, [double]$fd.PageSetup.PageHeight + 40)
        $backdrop.Fill.ForeColor.RGB = 0; $backdrop.Line.Visible = 0; $backdrop.ZOrder(1)   # msoSendToBack
        $pg.SaveAsPicture($b, 3)
    }
    finally { $fd.Close() }                                              # discarded, never saved

    $file = "e$($script:assetIndex).png"; $script:assetIndex++
    $bbox = (& python $matte $w $b (Join-Path $assets $file)).Trim()
    if ($LASTEXITCODE -ne 0) { throw "matte.py failed: $bbox" }
    if ($bbox -eq 'EMPTY') { $script:flatCache[$cacheKey] = $null; Note $s 'empty' "$reason (nothing visible)"; return @() }
    $px = $bbox -split ' ' | ForEach-Object { [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) }
    $pxPerPt = $px[4] / [double]$script:doc.PageSetup.PageWidth        # page image width / page width
    if (-not ($pxPerPt -gt 0 -and [double]::IsFinite($pxPerPt))) { throw "invalid page scale $pxPerPt px/pt" }
    Note $s 'flattened' "$reason$(if ($s.Rotation -ne 0) { " (rotation $($s.Rotation) baked in)" })"
    $element = [ordered]@{
        type = 'image'
        bounds = [ordered]@{ x = $px[0] / $pxPerPt; y = $px[1] / $pxPerPt; width = $px[2] / $pxPerPt; height = $px[3] / $pxPerPt }
        rotation = 0.0; source = "$assetsName/$file"; crop = $null; stroke = $null
    }
    $script:flatCache[$cacheKey] = $element
    , $element
}

function SolidFill($s) {
    if ($s.Fill.Visible -ne -1) { return $null }
    if ($s.Fill.Type -ne 1) { throw "fill type $($s.Fill.Type)" }
    Hex $s.Fill.ForeColor.RGB $s.Fill.Transparency
}

function SolidLine($s) {
    if ($s.Line.Visible -ne -1 -or $s.Line.Weight -le 0) { return $null }
    if ($s.Line.DashStyle -ne 1) { throw "dashed line $($s.Line.DashStyle)" }
    [ordered]@{ color = (Hex $s.Line.ForeColor.RGB $s.Line.Transparency); width = [double]$s.Line.Weight }
}

$alignMap = @{ 0 = 'left'; 1 = 'center'; 2 = 'right'; 3 = 'justify'; 4 = 'justify'; 6 = 'justify' }

function Paragraphs($s) {
    $r = $s.TextFrame.TextRange
    $paras = [Collections.Generic.List[object]]::new()
    for ($p = 1; $p -le $r.ParagraphsCount; $p++) {
        $pr = $r.Paragraphs($p)
        $pf = $pr.ParagraphFormat
        $runs = [Collections.Generic.List[object]]::new()
        $key = $null; $buf = [Text.StringBuilder]::new(); $style = $null
        for ($i = 1; $i -le $pr.Length; $i++) {
            $c = $pr.Characters($i, 1)
            $ch = $c.Text
            if ($ch -eq "`r") { continue }                                  # paragraph mark
            if ($ch -eq [string][char]11) { $ch = "`n" }                    # Publisher line break -> forced break
            $f = $c.Font
            if ($f.Shadow -eq -1 -or $f.Outline -eq -1 -or $f.Line.Visible -eq -1) { throw 'character effects (outline/shadow)' }
            $k = "$($f.Name)|$($f.Size)|$($f.Bold)|$($f.Italic)|$($f.Color.RGB)|$($f.Underline)"
            if ($k -ne $key) {
                if ($buf.Length) { $runs.Add([ordered]@{ text = $buf.ToString(); style = $style }); [void]$buf.Clear() }
                if ($f.Underline -ne 0) { Note $s 'approximated' 'underline not supported yet' }
                $key = $k
                $style = [ordered]@{ family = "$($f.Name)"; size = [double]$f.Size; bold = ($f.Bold -eq -1); italic = ($f.Italic -eq -1); color = (Hex $f.Color.RGB) }
            }
            [void]$buf.Append($ch)
        }
        if ($buf.Length) { $runs.Add([ordered]@{ text = $buf.ToString(); style = $style }) }
        $align = $alignMap[[int]$pf.Alignment]; if (-not $align) { $align = 'left'; Note $s 'approximated' "alignment $($pf.Alignment)" }
        $ls = [double]$pf.LineSpacing
        if ($pf.LineSpacingRule -ne 5 -or $ls -le 0.2 -or $ls -gt 5) { Note $s 'approximated' "line spacing rule $($pf.LineSpacingRule) value $ls"; $ls = 1.19 }
        $paras.Add([ordered]@{ alignment = $align; lineSpacing = $ls; spaceBefore = [double]$pf.SpaceBefore; spaceAfter = [double]$pf.SpaceAfter; runs = @($runs) })
    }
    , @($paras)
}

function Unsupported($s, [bool]$inGroup, [string]$why) {
    if ($inGroup) { throw [GroupNeedsFlatten]::new($why) }
    Flatten $s $why
}

function Convert-Shape($s, [bool]$inGroup = $false) {
    $type = [int]$s.Type
    if ($type -eq 6) {                                                  # group
        try {
            if ($s.Rotation -ne 0) { throw [GroupNeedsFlatten]::new('rotated group') }
            $out = @(); foreach ($g in $s.GroupItems) { $out += Convert-Shape $g $true }
            return $out
        }
        catch [GroupNeedsFlatten] {
            if ($inGroup) { throw }
            return Flatten $s "group containing $($_.Exception.Message)"
        }
    }
    # Decide native vs flattened inside the try; flatten OUTSIDE it, so a failure while flattening propagates
    # once instead of being mistaken for "unsupported" and flattened a second time.
    $reason = $null
    $notesBefore = $report.Count
    try {
        $hasText = $false
        try { $hasText = ($s.HasTextFrame -eq -1 -and $s.TextFrame.HasText -eq -1) } catch {}
        $auto = $null; try { $auto = [int]$s.AutoShapeType } catch {}
        if ($s.Shadow.Visible -eq -1) { throw 'shadow' }
        if ($type -eq 17 -or ($type -eq 1 -and $auto -in 1, 9)) {       # text box, rectangle, oval
            if ($hasText -and ($s.HorizontalFlip -eq -1 -or $s.VerticalFlip -eq -1)) { throw 'flipped text' }
            $els = @()
            $fill = SolidFill $s; $line = SolidLine $s
            if ($fill -or $line) {
                $kind = if ($auto -eq 9) { 'ellipse' } else { 'rectangle' }
                $els += , [ordered]@{ type = 'shape'; bounds = (BoxOf $s); rotation = [double]$s.Rotation; kind = $kind; fill = $fill; stroke = $line }
            }
            if ($hasText) {
                $tf = $s.TextFrame
                if ($tf.VerticalTextAlignment -ne 0) { Note $s 'approximated' "vertical anchor $($tf.VerticalTextAlignment) drawn as top" }
                $els += , [ordered]@{
                    type = 'text'; bounds = (BoxOf $s); rotation = [double]$s.Rotation
                    insets = [ordered]@{ left = [double]$tf.MarginLeft; top = [double]$tf.MarginTop; right = [double]$tf.MarginRight; bottom = [double]$tf.MarginBottom }
                    paragraphs = (Paragraphs $s)
                }
            }
            if ($els.Count) { Note $s 'native' "type $type" }
            return $els
        }
        $reason = switch ($type) { 13 { 'picture' } 11 { 'linked picture' } 15 { 'WordArt' } 9 { 'line' } 5 { 'freeform' } 19 { 'table' } default { "shape type $type / autoshape $auto" } }
    }
    catch [GroupNeedsFlatten] { throw }
    catch {
        # Drop notes from the abandoned native attempt (e.g. "underline") so the report describes what was output.
        while ($report.Count -gt $notesBefore) { $report.RemoveAt($report.Count - 1) }
        $reason = "unsupported: $($_.Exception.Message)"
    }
    return Unsupported $s $inGroup $reason
}

$pub = $null; $d = $null
try {
    $pub = New-Publisher
    $d = $pub.Open($Source, $true)
    $script:doc = $d
    $pages = @()
    for ($pi = 1; $pi -le $d.Pages.Count; $pi++) {
        $script:pageIndex = $pi
        $script:page = $d.Pages($pi)
        $els = @()
        if (-not [bool]$script:page.IgnoreMaster) {
            $script:onMaster = $true
            # No try/catch here: a master page that can't be read would silently drop content, so it fails the file.
            foreach ($ms in @($script:page.Master.Shapes | Sort-Object ZOrderPosition)) { $els += Convert-Shape $ms }
            $script:onMaster = $false
        }
        try { if ($script:page.Background.Exists -eq -1) { $report.Add([ordered]@{ page = $pi; shape = '(page)'; kind = 'warning'; detail = 'page background not exported' }) } } catch {}
        foreach ($s in @($script:page.Shapes | Sort-Object ZOrderPosition)) { $els += Convert-Shape $s }
        $pages += , [ordered]@{ width = [double]$d.PageSetup.PageWidth; height = [double]$d.PageSetup.PageHeight; elements = @($els) }
    }
    $doc = [ordered]@{ schemaVersion = 1; name = $name; pages = @($pages) }
    $json = Join-Path $OutDir "$name.json"
    $tmp = "$json.tmp"
    $doc | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $tmp -Encoding utf8NoBOM
    ConvertTo-Json -InputObject @($report) -Depth 5 | Set-Content -LiteralPath (Join-Path $OutDir "$name.import.json") -Encoding utf8NoBOM
    Move-Item -LiteralPath $tmp -Destination $json -Force                # the document appears only when complete
    # Only now are older asset folders unreferenced. Only folders this script names (<name>.assets-<stamp>-<pid>)
    # are removed: never <name>.assets (the folder `pubsmith import` writes) or another folder that starts with the
    # same name. Best effort: a locked folder is left for the next run.
    $ownPattern = '^' + [regex]::Escape("$name.assets-") + '\d{14}-\d+$'
    Get-ChildItem -LiteralPath $OutDir -Directory -Filter "$name.assets-*" |
        Where-Object { $_.Name -ne $assetsName -and $_.Name -match $ownPattern } |
        ForEach-Object { $dir = $_.FullName; try { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction Stop } catch { Write-Warning "left stale folder $dir" } }
    $counts = $report | Group-Object { $_.kind } | ForEach-Object { "$($_.Name)=$($_.Count)" }
    Write-Output "OK $Source :: $($counts -join ' ')"
}
finally {
    if ($d) { try { $d.Close() } catch {} }
    if ($pub) { try { $pub.Quit() } catch {} }
    if ($script:scratchApp) { try { $script:scratchApp.Quit() } catch {} }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
