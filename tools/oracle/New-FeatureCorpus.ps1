<#
Builds the feature test set: one small .pub per Publisher feature (F-numbers, from the project's own
feature ranking), authored by Publisher itself through COM. Each file isolates one
feature so an importer/renderer test can be named after it, and Publisher's own PDF/PNG of it
(made afterwards by Export-PubHarvest.ps1) is the expected result.

Every construction step runs in its own try/catch and is logged to corpus.csv as
Applied or Failed, so the corpus records exactly what each file really contains.
Existing .pub files in the corpus folder are overwritten (they are generated artifacts).
#>
param(
    [string]$OutDir = "C:\pubsmith-corpus\test-corpus",
    [string]$Picture = "C:\pubsmith-corpus\art\sample-picture.png",
    [string]$Strip = "C:\pubsmith-corpus\art\sample-strip.png"
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PublisherProcesses.ps1')
Assert-PublisherClosed
New-Item -ItemType Directory -Force $OutDir | Out-Null
$IN = 72                                   # points per inch
$msoTrue = -1; $msoFalse = 0
$log = [Collections.Generic.List[object]]::new()
$before = @(Get-Process MSPUB -EA SilentlyContinue | ForEach-Object Id)
$pub = New-Object -ComObject Publisher.Application
$ourPids = @(Get-Process MSPUB -EA SilentlyContinue | Where-Object { $before -notcontains $_.Id } | ForEach-Object Id)

function Rgb([int]$r, [int]$g, [int]$b) { $r + 256 * $g + 65536 * $b }

function Step([string]$file, [string]$what, [scriptblock]$do) {
    try { & $do | Out-Null; $log.Add([pscustomobject]@{ File = $file; Step = $what; Result = 'Applied'; Error = '' }) }
    catch { $log.Add([pscustomobject]@{ File = $file; Step = $what; Result = 'Failed'; Error = "$($_.Exception.Message)" }) }
}

function Build([string]$name, [double]$wIn, [double]$hIn, [scriptblock]$body) {
    $path = Join-Path $OutDir "$name.pub"
    $doc = $pub.NewDocument()
    try {
        $doc.PageSetup.PageWidth = $wIn * $IN
        $doc.PageSetup.PageHeight = $hIn * $IN
        $s = $doc.Pages(1).Shapes
        & $body $doc $s $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
        $doc.SaveAs($path)
        Write-Host "BUILT $name"
    } catch {
        $log.Add([pscustomobject]@{ File = $name; Step = 'BUILD/SAVE'; Result = 'Failed'; Error = "$($_.Exception.Message)" })
        Write-Host "FAIL  $name :: $_"
    } finally { $doc.Close() }
}

function Tb($s, $l, $t, $w, $h, $text) {
    $b = $s.AddTextbox(1, $l, $t, $w, $h); $b.TextFrame.TextRange.Text = $text; $b
}

# F01 page setup: odd custom size, content touching each margin so size/origin errors show.
Build 'F01-page-custom-size' 5.75 4.75 { param($d, $s, $n)
    Step $n 'page 5.75x4.75in' { if ([math]::Abs($d.PageSetup.PageWidth - 414) -gt 0.5) { throw "width $($d.PageSetup.PageWidth)" } }
    Step $n 'inset frame 0.25in' { $r = $s.AddShape(1, 18, 18, 414 - 36, 342 - 36); $r.Fill.Visible = $msoFalse; $r.Line.Weight = 2 }
    Step $n 'corner markers' { foreach ($p in @(@(0, 0), @(396, 0), @(0, 324), @(396, 324))) { $m = $s.AddShape(1, $p[0], $p[1], 18, 18); $m.Fill.ForeColor.RGB = (Rgb 200 0 0) } }
    Step $n 'center label' { $b = Tb $s 107 150 200 40 'F01 5.75 x 4.75 in'; $b.TextFrame.TextRange.ParagraphFormat.Alignment = 1 }
}

# F04 images: PNG with alpha, crop, scale non-uniform, placed image over coloured background.
Build 'F04-images-crop-alpha' 8.5 11 { param($d, $s, $n)
    Step $n 'coloured background' { $r = $s.AddShape(1, 36, 36, 540, 300); $r.Fill.ForeColor.RGB = (Rgb 255 200 120) }
    Step $n 'png full' { $s.AddPicture($Picture, $msoFalse, $msoTrue, 54, 54, 216, 216) }
    Step $n 'png alpha over colour' { $s.AddPicture($Strip, $msoFalse, $msoTrue, 300, 120, 240, 73) }
    Step $n 'png cropped 25% each side' { $p = $s.AddPicture($Picture, $msoFalse, $msoTrue, 54, 380, 216, 216); $p.PictureFormat.CropLeft = 54; $p.PictureFormat.CropRight = 54; $p.PictureFormat.CropTop = 54; $p.PictureFormat.CropBottom = 54 }
    Step $n 'png stretched 2:1' { $s.AddPicture($Picture, $msoFalse, $msoTrue, 300, 380, 260, 130) }
}

# F05 label sheet: 2x4 grid of grouped label units on Letter with crop marks (a typical label-sheet layout).
Build 'F05-label-sheet-8up' 8.5 11 { param($d, $s, $n)
    $w = 3.75 * $IN; $h = 2.5 * $IN; $x0 = 0.375 * $IN; $y0 = 0.5 * $IN
    for ($r = 0; $r -lt 4; $r++) { for ($c = 0; $c -lt 2; $c++) {
        $x = $x0 + $c * $w; $y = $y0 + $r * $h; $tag = "r$r c$c"
        Step $n "label $tag" {
            $a = $s.AddShape(5, $x + 6, $y + 6, $w - 12, $h - 12); $a.Fill.ForeColor.RGB = (Rgb 230 245 230)
            $t = Tb $s ($x + 12) ($y + 20) ($w - 24) 30 "LABEL $tag"; $t.TextFrame.TextRange.Font.Size = 16; $t.TextFrame.TextRange.ParagraphFormat.Alignment = 1
            $s.Range([object[]]@($a.Name, $t.Name)).Group() }
        Step $n "crop marks $tag" { $s.AddLine($x, $y - 9, $x, $y - 2); $s.AddLine($x - 9, $y, $x - 2, $y) }
    } }
}

# F07 transforms: rotation at several angles, flips, z-order overlap, grouping.
Build 'F07-transforms-zorder-group' 8.5 11 { param($d, $s, $n)
    $angles = 0, 15, 45, 90, 180, 270
    for ($i = 0; $i -lt $angles.Count; $i++) { $a = $angles[$i]
        Step $n "rotate $a" { $b = Tb $s (54 + ($i % 3) * 170) (54 + [double][math]::Floor([double]$i / 3) * 150) 140 40 "rotated $a deg"; $b.Line.Visible = $msoTrue; $b.Rotation = $a } }
    Step $n 'flip horizontal image' { $p = $s.AddPicture($Picture, $msoFalse, $msoTrue, 54, 360, 144, 144); $p.Flip(0) }
    Step $n 'flip vertical image' { $p = $s.AddPicture($Picture, $msoFalse, $msoTrue, 220, 360, 144, 144); $p.Flip(1) }
    Step $n 'z-order red/green/blue overlap, green brought to front' {
        $r = $s.AddShape(1, 380, 360, 120, 120); $r.Fill.ForeColor.RGB = (Rgb 220 0 0)
        $g = $s.AddShape(1, 420, 400, 120, 120); $g.Fill.ForeColor.RGB = (Rgb 0 170 0)
        $b = $s.AddShape(1, 460, 440, 120, 120); $b.Fill.ForeColor.RGB = (Rgb 0 0 220)
        $g.ZOrder(0) }  # msoBringToFront
    Step $n 'rotated group' { $a = $s.AddShape(9, 100, 580, 100, 60); $b = Tb $s 110 600 80 20 'grouped'; $grp = $s.Range([object[]]@($a.Name, $b.Name)).Group(); $grp.Rotation = 30 }
}

# F08 fills and lines: solid, gradient, transparency, shadow, dash, weight, autoshapes.
Build 'F08-fills-lines-shapes' 8.5 11 { param($d, $s, $n)
    Step $n 'solid rect' { $r = $s.AddShape(1, 54, 54, 150, 100); $r.Fill.ForeColor.RGB = (Rgb 0 90 160) }
    Step $n 'two-colour gradient' { $r = $s.AddShape(1, 230, 54, 150, 100); $r.Fill.ForeColor.RGB = (Rgb 255 120 0); $r.Fill.BackColor.RGB = (Rgb 140 0 0); $r.Fill.TwoColorGradient(1, 1) }
    Step $n '50% transparent over text' { $null = Tb $s 406 80 150 40 'UNDER TRANSPARENT'; $r = $s.AddShape(1, 406, 54, 150, 100); $r.Fill.ForeColor.RGB = (Rgb 0 150 0); $r.Fill.Transparency = 0.5 }
    Step $n 'drop shadow' { $r = $s.AddShape(5, 54, 200, 150, 100); $r.Fill.ForeColor.RGB = (Rgb 255 255 255); $r.Shadow.Visible = $msoTrue; $r.Shadow.OffsetX = 6; $r.Shadow.OffsetY = 6 }
    Step $n 'dashed thick line' { $l = $s.AddLine(230, 250, 380, 250); $l.Line.Weight = 6; $l.Line.DashStyle = 4 }
    Step $n 'arrow' { $l = $s.AddLine(406, 250, 556, 250); $l.Line.Weight = 3; $l.Line.EndArrowheadStyle = 2 }
    $shapes = @{ 'oval' = 9; 'star' = 92; 'right arrow' = 33; 'rounded rect' = 5; 'triangle' = 7; 'heart' = 21 }
    $i = 0
    foreach ($k in $shapes.Keys) { $x = 54 + ($i % 3) * 176; $y = 360 + [double][math]::Floor([double]$i / 3) * 140; $i++
        Step $n "autoshape $k" { $a = $s.AddShape($shapes[$k], $x, $y, 150, 110); $a.Fill.ForeColor.RGB = (Rgb 120 60 180); $a.Line.Weight = 2 } }
    Step $n 'no-fill outlined circle (label badge style)' { $c = $s.AddShape(9, 230, 650, 150, 150); $c.Fill.Visible = $msoFalse; $c.Line.Weight = 4; $c.Line.ForeColor.RGB = (Rgb 255 255 255); $c.Shadow.Visible = $msoTrue }
}

# F11 rich text: mixed runs, alignment, spacing, rotated vertical frame (label side-panel style).
Build 'F11-rich-text' 8.5 11 { param($d, $s, $n)
    Step $n 'mixed runs bold/italic/underline/colour/size' {
        $b = Tb $s 54 54 500 60 'Plain Bold Italic Underline Red Big Small'
        $tr = $b.TextFrame.TextRange
        $tr.Characters(7, 4).Font.Bold = $msoTrue; $tr.Characters(12, 6).Font.Italic = $msoTrue
        $tr.Characters(19, 9).Font.Underline = 1; $tr.Characters(29, 3).Font.Color.RGB = (Rgb 200 0 0)
        $tr.Characters(33, 3).Font.Size = 28; $tr.Characters(37, 5).Font.Size = 7 }
    $al = @(@(0, 'left'), @(1, 'center'), @(2, 'right'), @(6, 'justify'))   # PbParagraphAlignmentType values
    $row = 0; foreach ($pair in $al) { $k = $pair[0]; $name = $pair[1]; $y = 120 + $row++ * 70
        Step $n "align $name" { $b = Tb $s 54 $y 300 60 "Alignment ${name}: the quick brown fox jumps over the lazy dog and keeps running."; $b.Line.Visible = $msoTrue; $b.TextFrame.TextRange.ParagraphFormat.Alignment = $k
            if ($b.TextFrame.TextRange.ParagraphFormat.Alignment -ne $k) { throw "alignment read back $($b.TextFrame.TextRange.ParagraphFormat.Alignment), expected $k" } } }
    Step $n 'line spacing 2.0' { $b = Tb $s 54 420 300 100 "Line spacing doubled.`rSecond paragraph."; $b.TextFrame.TextRange.ParagraphFormat.LineSpacing = 2 }
    # Tracking is a percentage (default reads back 100), not points as the VBA docs say.
    Step $n 'tracking 200%' { $b = Tb $s 54 540 300 30 'TRACKED OUT'; $b.TextFrame.TextRange.Font.Tracking = [double]200
        if ($b.TextFrame.TextRange.Font.Tracking -ne 200) { throw "tracking read back $($b.TextFrame.TextRange.Font.Tracking)" } }
    Step $n 'vertical side-panel frame rotated 270' { $b = Tb $s 330 400 380 70 'Contents: sample text for a rotated side panel, long enough to wrap over two or three lines inside a narrow frame on a label.'; $b.TextFrame.TextRange.Font.Size = 8; $b.Rotation = 270 }
    Step $n 'text inside autoshape' { $a = $s.AddShape(9, 380, 54, 170, 110); $a.TextFrame.TextRange.Text = 'Text in an oval'; $a.TextFrame.TextRange.ParagraphFormat.Alignment = 1 }
    Step $n 'bullets' { $b = Tb $s 380 180 170 100 "First point`rSecond point`rThird point"; $b.TextFrame.TextRange.ParagraphFormat.SetListType(23) }   # 23 = pbListTypeBullet; default Symbol bullet (a custom U+2022 rendered as tofu)
}

# F13 fonts: Microsoft 365 cloud fonts, installed fonts, and a deliberately missing font.
Build 'F13-fonts' 8.5 11 { param($d, $s, $n)
    $fonts = 'Grandview', 'Tenorite', 'Roboto', 'Aptos', 'Calibri', 'Arial', 'Times New Roman', 'Definitely Missing Font XYZ'
    $y = 54
    foreach ($f in $fonts) {
        Step $n "font $f" { $b = Tb $s 54 $y 500 44 "$f - Sample Text 0123"; $b.TextFrame.TextRange.Font.Name = $f; $b.TextFrame.TextRange.Font.Size = 22 }
        $y += 60 }
}

# F14 multi-page with master-page elements (header, page number).
Build 'F14-multipage-master' 8.5 11 { param($d, $s, $n)
    Step $n 'add 2 pages' { $d.Pages.Add(2, 1) }
    Step $n 'master header bar' { $m = $d.MasterPages(1).Shapes; $r = $m.AddShape(1, 0, 0, 612, 40); $r.Fill.ForeColor.RGB = (Rgb 0 90 160); $t = $m.AddTextbox(1, 20, 8, 400, 24); $t.TextFrame.TextRange.Text = 'MASTER HEADER' }
    Step $n 'master page number' { $t = $d.MasterPages(1).Shapes.AddTextbox(1, 500, 740, 80, 24); $t.TextFrame.TextRange.InsertPageNumber() }
    foreach ($p in 1..3) { Step $n "body page $p" { $null = Tb $d.Pages($p).Shapes 54 100 400 40 "Body content page $p" } }
}

# F15 table: header row, merged cell, alignment - a Nutrition-Facts-like panel.
Build 'F15-table' 8.5 11 { param($d, $s, $n)
    Step $n 'table 5x3' {
        $t = $s.AddTable(5, 3, 54, 54, 360, 200).Table
        $data = @(@('Nutrient', 'Amount', '% DV'), @('Calories', '5', ''), @('Sodium', '120mg', '5%'), @('Total Carb', '1g', '0%'), @('Sugars', '0g', ''))
        for ($r = 1; $r -le 5; $r++) { for ($c = 1; $c -le 3; $c++) { $t.Rows($r).Cells($c).TextRange.Text = $data[$r - 1][$c - 1] } } }
    Step $n 'bold header row' { $t = $s.Item($s.Count).Table; for ($c = 1; $c -le 3; $c++) { $t.Rows(1).Cells($c).TextRange.Font.Bold = $msoTrue } }
    Step $n 'merge row5 col2-3' { $t = $s.Item($s.Count).Table; $t.Rows(5).Cells(2).Merge($t.Rows(5).Cells(3)) }
}

# F16 text wrap around an image.
Build 'F16-text-wrap' 8.5 11 { param($d, $s, $n)
    $lorem = ('Sample text wraps around the picture here. ' * 30).Trim()
    Step $n 'body text' { $null = Tb $s 54 54 504 400 $lorem }
    Step $n 'image square wrap' { $p = $s.AddPicture($Picture, $msoFalse, $msoTrue, 200, 120, 150, 150); $p.TextWrap.Type = 2 }
    Step $n 'oval tight wrap' { $o = $s.AddShape(9, 380, 300, 150, 100); $o.TextWrap.Type = 3 }
}

# F17 colour: CMYK-specified fills. (Document.EnterColorMode is "not supported in the current version"
# of Publisher via COM, so the document itself stays in RGB mode; the fills still carry CMYK values.)
Build 'F17-cmyk-process' 5.75 4.75 { param($d, $s, $n)
    Step $n 'CMYK fills C/M/Y/K' { $cm = @(@(100, 0, 0, 0), @(0, 100, 0, 0), @(0, 0, 100, 0), @(0, 0, 0, 100)); for ($i = 0; $i -lt 4; $i++) { $r = $s.AddShape(1, 20 + $i * 98, 40, 90, 90); $v = $cm[$i]; $r.Fill.ForeColor.CMYK.SetCMYK($v[0] * 2.55, $v[1] * 2.55, $v[2] * 2.55, $v[3] * 2.55) } }
    Step $n 'rich black vs plain black text' { $b = Tb $s 20 180 380 40 'Plain black text'; $b.TextFrame.TextRange.Font.Size = 20 }
}

# F18 curved text and F19 text effects.
# Classic WordArt reports a cloud font as applied but renders a substitute, so styled title text in a cloud font
# is built the way real publications do it: a text box in that font with character-level fill, outline and
# shadow. Classic WordArt steps use installed TrueType fonts.
Build 'F18-F19-wordart-curved' 8.5 11 { param($d, $s, $n)
    Step $n 'styled title: text box, cloud font, red fill + white outline + shadow' {
        $b = Tb $s 54 40 500 90 'SAMPLE WORDART!'; $f = $b.TextFrame.TextRange.Font
        $f.Name = 'Grandview'; $f.Size = 54; $f.Fill.ForeColor.RGB = (Rgb 170 20 20)
        $f.Line.Visible = $msoTrue; $f.Line.ForeColor.RGB = (Rgb 255 255 255); $f.Line.Weight = 1.5
        $f.Shadow = $msoTrue   # Font.TextShadow.Visible crashes the COM server (RPC_E_SERVERFAULT) and hangs Quit
        if ($f.Name -ne 'Grandview') { throw "font read back '$($f.Name)'" } }
    Step $n 'WordArt plain, outline + shadow (Arial Black)' {
        $w = $s.AddTextEffect(0, 'SAMPLE WORDART!', 'Arial Black', 40, $msoFalse, $msoFalse, 54, 140)
        $w.Fill.ForeColor.RGB = (Rgb 170 20 20); $w.Line.Visible = $msoTrue; $w.Line.ForeColor.RGB = (Rgb 255 255 255); $w.Line.Weight = 1.5
        $w.Shadow.Visible = $msoTrue; $w.Shadow.OffsetX = 3; $w.Shadow.OffsetY = 3 }
    Step $n 'WordArt arch-up curve' { $w = $s.AddTextEffect(0, 'ARCHED TEXT', 'Arial Black', 36, $msoTrue, $msoFalse, 54, 230); $w.TextEffect.PresetShape = 9; $w.Width = 320; $w.Height = 140 }   # msoTextEffectShapeArchUpCurve
    Step $n 'WordArt circle' { $w = $s.AddTextEffect(0, 'TEXT AROUND A CIRCLE', 'Arial', 24, $msoTrue, $msoFalse, 54, 380); $w.TextEffect.PresetShape = 11; $w.Width = 250; $w.Height = 250 }
    Step $n 'WordArt rotated 12deg' { $w = $s.AddTextEffect(0, 'WOW!', 'Arial Black', 40, $msoTrue, $msoFalse, 380, 420); $w.Rotation = -12 }
}

# F20 linked text frames: overflow flowing from frame 1 to frame 2.
Build 'F20-linked-frames' 8.5 11 { param($d, $s, $n)
    $long = (1..40 | ForEach-Object { "Sentence $_ flows between linked frames." }) -join ' '
    Step $n 'two linked frames' { $a = Tb $s 54 54 240 200 $long; $b = $s.AddTextbox(1, 318, 54, 240, 200); $a.TextFrame.NextLinkedTextFrame = $b.TextFrame }
    Step $n 'frame outlines' { foreach ($i in 1..2) { $s.Item($i).Line.Visible = $msoTrue } }
}

# F21 vector drawing: polyline, freeform curve, closed polygon.
Build 'F21-vector-freeform' 8.5 11 { param($d, $s, $n)
    Step $n 'polyline' { $pts = New-Object 'single[,]' 5, 2; $v = @(54, 300, 120, 200, 190, 300, 260, 200, 330, 300); for ($i = 0; $i -lt 5; $i++) { $pts[$i, 0] = $v[2 * $i]; $pts[$i, 1] = $v[2 * $i + 1] }; $p = $s.AddPolyline($pts); $p.Line.Weight = 3 }
    Step $n 'bezier curve' { $pts = New-Object 'single[,]' 4, 2; $v = @(54, 500, 150, 380, 250, 620, 350, 500); for ($i = 0; $i -lt 4; $i++) { $pts[$i, 0] = $v[2 * $i]; $pts[$i, 1] = $v[2 * $i + 1] }; $c = $s.AddCurve($pts); $c.Line.Weight = 3 }
    Step $n 'closed filled polygon (teardrop shape)' { $pts = New-Object 'single[,]' 6, 2; $v = @(400, 200, 460, 220, 480, 300, 450, 420, 420, 330, 400, 200); for ($i = 0; $i -lt 6; $i++) { $pts[$i, 0] = $v[2 * $i]; $pts[$i, 1] = $v[2 * $i + 1] }; $p = $s.AddPolyline($pts); $p.Fill.ForeColor.RGB = (Rgb 170 20 20) }
}

# F12 bleed: artwork running off every page edge (print-export must clip/extend correctly).
Build 'F12-bleed' 5.75 4.75 { param($d, $s, $n)
    Step $n 'background bleeding 0.125in all sides' { $r = $s.AddShape(1, -9, -9, 414 + 18, 342 + 18); $r.Fill.ForeColor.RGB = (Rgb 255 150 40); $r.Line.Visible = $msoFalse }
    Step $n 'image off right edge' { $s.AddPicture($Picture, $msoFalse, $msoTrue, 320, 100, 150, 150) }
    Step $n 'safe-area text' { $null = Tb $s 27 27 250 30 'Inside 0.375in safe area' }
}

try { $pub.Quit() } catch {}
# A COM server fault can leave our Publisher instance alive (stuck on "Publishing..."), which would
# block the next Publisher run. Kill only the instance this script started, never a user's session.
if ($ourPids.Count) {
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline -and (Get-Process -Id $ourPids -EA SilentlyContinue)) { Start-Sleep -Milliseconds 500 }
    Get-Process -Id $ourPids -EA SilentlyContinue | Stop-Process -Force
}
$log | Export-Csv (Join-Path $OutDir 'corpus.csv') -NoTypeInformation
$log | Group-Object Result | ForEach-Object { "{0}: {1}" -f $_.Name, $_.Count }
$log | Where-Object Result -eq 'Failed' | Format-Table File, Step, Error -AutoSize -Wrap
