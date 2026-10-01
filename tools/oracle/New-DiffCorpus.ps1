<#
WI-003: differential corpus for reverse engineering the .pub format. Each variant is a small publication built
by Publisher (COM) that differs from its group's "base" in ONE property, so a byte diff of the saved file
locates that property. Publisher's own ground truth for every variant is produced afterwards by
Export-PubLayout.ps1 (layout JSON) and Export-PubHarvest.ps1 (PDF + 300 dpi PNG).

Writes <OutDir>\<group>\<variant>.pub and <OutDir>\variants.csv (group, variant, description, status, error).
A variant that Publisher rejects is logged as FAIL and the run continues. Existing .pub files are overwritten
(generated artifacts). Needs a working Publisher: a desktop install that still runs, or the PubOracle VM (docs/publisher-oracle-vm.md).
#>
param(
    [string]$OutDir = "C:\pubsmith-corpus\diff-corpus",
    [string]$Picture = "C:\pubsmith-corpus\art\sample-picture.png"
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PublisherProcesses.ps1')
Assert-PublisherClosed
New-Item -ItemType Directory -Force $OutDir | Out-Null
$log = [Collections.Generic.List[object]]::new()
$msoTrue = -1; $msoFalse = 0   # not $msoTrue/$msoFalse: PowerShell names are case-insensitive and loops use $f
function Rgb([int]$r, [int]$g, [int]$b) { $r + 256 * $g + 65536 * $b }

$before = @(Get-Process MSPUB -ErrorAction SilentlyContinue | ForEach-Object Id)
$pub = New-Object -ComObject Publisher.Application
$ours = @(Get-Process MSPUB -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id } | ForEach-Object Id)

# One variant = new document -> base builder for the group -> single tweak -> SaveAs.
function V([string]$group, [string]$variant, [string]$desc, [scriptblock]$base, [scriptblock]$tweak) {
    $dir = Join-Path $OutDir $group; New-Item -ItemType Directory -Force $dir | Out-Null
    $path = Join-Path $dir "$variant.pub"
    $d = $null
    try {
        $d = $pub.NewDocument()
        $d.PageSetup.PageWidth = 612; $d.PageSetup.PageHeight = 792
        $ctx = & $base $d
        if ($tweak) { & $tweak $d $ctx }
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
        $d.SaveAs($path)
        $log.Add([pscustomobject]@{ Group = $group; Variant = $variant; Description = $desc; Status = 'OK'; Error = '' })
    } catch {
        $log.Add([pscustomobject]@{ Group = $group; Variant = $variant; Description = $desc; Status = 'FAIL'; Error = "$($_.Exception.Message)" })
    } finally { if ($d) { try { $d.Close() } catch {} } }
}

# ---------- bases ----------
$RectBase = { param($d) $r = $d.Pages(1).Shapes.AddShape(1, 72, 72, 144, 72); $r.Fill.ForeColor.RGB = (Rgb 0 0 255); @{ s = $r } }
$TextBase = { param($d) $b = $d.Pages(1).Shapes.AddTextbox(1, 72, 72, 288, 144); $b.TextFrame.TextRange.Text = 'Hello Example Text world'; @{ s = $b; r = $b.TextFrame.TextRange } }
$Text2Base = { param($d) $b = $d.Pages(1).Shapes.AddTextbox(1, 72, 72, 288, 144); $b.TextFrame.TextRange.Text = "First paragraph here.`rSecond paragraph here."; @{ s = $b; r = $b.TextFrame.TextRange } }
$PicBase = { param($d) $p = $d.Pages(1).Shapes.AddPicture($Picture, $msoFalse, $msoTrue, 72, 72, 216, 216); @{ s = $p } }
$WaBase = { param($d) $w = $d.Pages(1).Shapes.AddTextEffect(0, 'Sample Text!', 'Arial Black', 36, $msoFalse, $msoFalse, 72, 72); @{ s = $w } }
$EmptyBase = { param($d) @{} }

# ---------- A: geometry ----------
V geometry base 'rect 72,72 144x72 blue' $RectBase $null
foreach ($x in 0, 1, 73, 100, 306, 500) { V geometry "left-$x" "left = $x pt" $RectBase ([scriptblock]::Create("param(`$d,`$c) `$c.s.Left = [double]$x")) }
foreach ($y in 0, 74, 396, 700) { V geometry "top-$y" "top = $y pt" $RectBase ([scriptblock]::Create("param(`$d,`$c) `$c.s.Top = [double]$y")) }
foreach ($w in 1, 150, 612) { V geometry "width-$w" "width = $w pt" $RectBase ([scriptblock]::Create("param(`$d,`$c) `$c.s.Width = [double]$w")) }
foreach ($h in 10, 300) { V geometry "height-$h" "height = $h pt" $RectBase ([scriptblock]::Create("param(`$d,`$c) `$c.s.Height = [double]$h")) }
foreach ($a in 1, 30, 90, 180, 270, 359) { V geometry "rot-$a" "rotation = $a deg" $RectBase ([scriptblock]::Create("param(`$d,`$c) `$c.s.Rotation = [double]$a")) }
V geometry flip-h 'horizontal flip' $RectBase { param($d, $c) $c.s.Flip(0) }
V geometry flip-v 'vertical flip' $RectBase { param($d, $c) $c.s.Flip(1) }
V geometry two-rects 'second rect (red) on top of first' $RectBase { param($d, $c) $r2 = $d.Pages(1).Shapes.AddShape(1, 100, 100, 144, 72); $r2.Fill.ForeColor.RGB = (Rgb 255 0 0) }
V geometry two-rects-swapped 'second rect sent to back' $RectBase { param($d, $c) $r2 = $d.Pages(1).Shapes.AddShape(1, 100, 100, 144, 72); $r2.Fill.ForeColor.RGB = (Rgb 255 0 0); $r2.ZOrder(1) }

# ---------- B: shape kinds ----------
$kinds = @{ rect = 1; roundrect = 5; oval = 9; triangle = 7; star5 = 92; rightarrow = 33; heart = 21; diamond = 4; hexagon = 10 }
foreach ($k in $kinds.Keys) { V shapes $k "autoshape $k ($($kinds[$k]))" ([scriptblock]::Create("param(`$d) `$s = `$d.Pages(1).Shapes.AddShape($($kinds[$k]), 72, 72, 144, 144); @{ s = `$s }")) $null }
V shapes line 'line 72,72 -> 216,144' $EmptyBase { param($d, $c) $d.Pages(1).Shapes.AddLine(72, 72, 216, 144) }
V shapes line-arrow 'line with end arrowhead' $EmptyBase { param($d, $c) $l = $d.Pages(1).Shapes.AddLine(72, 72, 216, 144); $l.Line.EndArrowheadStyle = 2 }
V shapes polyline 'closed polygon 4 points' $EmptyBase { param($d, $c) $p = New-Object 'single[,]' 4, 2; $v = 72, 72, 216, 72, 216, 216, 72, 72; for ($i = 0; $i -lt 4; $i++) { $p[$i, 0] = $v[2 * $i]; $p[$i, 1] = $v[2 * $i + 1] }; $d.Pages(1).Shapes.AddPolyline($p) }
V shapes curve 'bezier curve' $EmptyBase { param($d, $c) $p = New-Object 'single[,]' 4, 2; $v = 72, 200, 150, 72, 250, 328, 350, 200; for ($i = 0; $i -lt 4; $i++) { $p[$i, 0] = $v[2 * $i]; $p[$i, 1] = $v[2 * $i + 1] }; $d.Pages(1).Shapes.AddCurve($p) }

# ---------- C: fill, line, shadow ----------
foreach ($c in @(@('red', 255, 0, 0), @('green', 0, 255, 0), @('grey', 128, 128, 128), @('white', 255, 255, 255), @('orange', 255, 150, 40))) {
    V fill "fill-$($c[0])" "fill $($c[0])" $RectBase ([scriptblock]::Create("param(`$d,`$c) `$c.s.Fill.ForeColor.RGB = $(Rgb $c[1] $c[2] $c[3])")) }
V fill no-fill 'fill invisible' $RectBase { param($d, $c) $c.s.Fill.Visible = $msoFalse }
V fill transparency-50 'fill 50% transparent' $RectBase { param($d, $c) $c.s.Fill.Transparency = 0.5 }
V fill gradient 'two-colour gradient blue->red horizontal' $RectBase { param($d, $c) $c.s.Fill.BackColor.RGB = (Rgb 255 0 0); $c.s.Fill.TwoColorGradient(1, 1) }
V fill scheme-accent1 'fill = scheme colour 1' $RectBase { param($d, $c) $c.s.Fill.ForeColor.SchemeColor = 1 }
foreach ($w in 0.25, 1, 4, 10) { V line "weight-$w" "line weight $w" $RectBase ([scriptblock]::Create("param(`$d,`$c) `$c.s.Line.Weight = [double]$w")) }
V line no-line 'line invisible' $RectBase { param($d, $c) $c.s.Line.Visible = $msoFalse }
V line color-red 'line red' $RectBase { param($d, $c) $c.s.Line.ForeColor.RGB = (Rgb 255 0 0) }
foreach ($ds in 2, 4, 5) { V line "dash-$ds" "dash style $ds" $RectBase ([scriptblock]::Create("param(`$d,`$c) `$c.s.Line.DashStyle = $ds")) }
V shadow on 'shadow visible' $RectBase { param($d, $c) $c.s.Shadow.Visible = $msoTrue }
V shadow offset 'shadow visible offset 6,6' $RectBase { param($d, $c) $c.s.Shadow.Visible = $msoTrue; $c.s.Shadow.OffsetX = 6; $c.s.Shadow.OffsetY = 6 }

# ---------- D: text ----------
V text base 'text box, default Calibri 10' $TextBase $null
foreach ($f in 'Arial', 'Times New Roman', 'Courier New', 'Arial Black') { V text "font-$($f -replace ' ','')" "font $f" $TextBase ([scriptblock]::Create("param(`$d,`$c) `$c.r.Font.Name = '$f'")) }
foreach ($sz in 8, 12, 24, 36.5, 72) { V text "size-$sz" "size $sz" $TextBase ([scriptblock]::Create("param(`$d,`$c) `$c.r.Font.Size = [double]$sz")) }
V text bold 'bold' $TextBase { param($d, $c) $c.r.Font.Bold = $msoTrue }
V text italic 'italic' $TextBase { param($d, $c) $c.r.Font.Italic = $msoTrue }
V text underline 'underline' $TextBase { param($d, $c) $c.r.Font.Underline = 1 }
V text color-red 'text colour red' $TextBase { param($d, $c) $c.r.Font.Color.RGB = (Rgb 255 0 0) }
V text color-custom 'text colour #1234AB' $TextBase { param($d, $c) $c.r.Font.Color.RGB = (Rgb 0x12 0x34 0xAB) }
V text allcaps 'all caps' $TextBase { param($d, $c) $c.r.Font.AllCaps = $msoTrue }
V text smallcaps 'small caps' $TextBase { param($d, $c) $c.r.Font.SmallCaps = $msoTrue }
V text superscript 'superscript' $TextBase { param($d, $c) $c.r.Font.SuperScript = $msoTrue }
V text tracking-200 'tracking 200%' $TextBase { param($d, $c) $c.r.Font.Tracking = [double]200 }
V text run-bold-word 'second word bold' $TextBase { param($d, $c) $c.r.Characters(7, 7).Font.Bold = $msoTrue }
V text run-red-word 'second word red' $TextBase { param($d, $c) $c.r.Characters(7, 7).Font.Color.RGB = (Rgb 255 0 0) }
V text run-size-word 'second word 20pt' $TextBase { param($d, $c) $c.r.Characters(7, 7).Font.Size = 20 }
V text run-font-word 'second word Arial' $TextBase { param($d, $c) $c.r.Characters(7, 7).Font.Name = 'Arial' }
foreach ($al in @(@('center', 1), @('right', 2), @('justify', 6))) { V text "align-$($al[0])" "alignment $($al[0])" $TextBase ([scriptblock]::Create("param(`$d,`$c) `$c.r.ParagraphFormat.Alignment = $($al[1])")) }
V text ls-single 'line spacing single' $TextBase { param($d, $c) $c.r.ParagraphFormat.SetLineSpacing(0, 1) }
V text ls-double 'line spacing double' $TextBase { param($d, $c) $c.r.ParagraphFormat.SetLineSpacing(2, 2) }
V text ls-multiple-1.5 'line spacing multiple 1.5' $TextBase { param($d, $c) $c.r.ParagraphFormat.SetLineSpacing(5, 1.5) }
V text ls-exact-14 'line spacing exactly 14 pt' $TextBase { param($d, $c) $c.r.ParagraphFormat.SetLineSpacing(4, 14) }
V text ls-exact-30 'line spacing exactly 30 pt' $TextBase { param($d, $c) $c.r.ParagraphFormat.SetLineSpacing(4, 30) }
V text space-before-12 'space before 12' $TextBase { param($d, $c) $c.r.ParagraphFormat.SpaceBefore = 12 }
V text space-after-0 'space after 0' $TextBase { param($d, $c) $c.r.ParagraphFormat.SpaceAfter = 0 }
V text indent-left-18 'left indent 18' $TextBase { param($d, $c) $c.r.ParagraphFormat.LeftIndent = 18 }
V text indent-first-18 'first line indent 18' $TextBase { param($d, $c) $c.r.ParagraphFormat.FirstLineIndent = 18 }
V text indent-right-18 'right indent 18' $TextBase { param($d, $c) $c.r.ParagraphFormat.RightIndent = 18 }
V text bullets 'bulleted' $TextBase { param($d, $c) $c.r.ParagraphFormat.SetListType(23) }
V text margins-10 'text frame margins 10 pt' $TextBase { param($d, $c) $f = $c.s.TextFrame; $f.MarginLeft = 10; $f.MarginTop = 10; $f.MarginRight = 10; $f.MarginBottom = 10 }
V text vanchor-middle 'vertical anchor middle' $TextBase { param($d, $c) $c.s.TextFrame.VerticalTextAlignment = 1 }
V text vanchor-bottom 'vertical anchor bottom' $TextBase { param($d, $c) $c.s.TextFrame.VerticalTextAlignment = 2 }
V text rotated-270 'text box rotated 270' $TextBase { param($d, $c) $c.s.Rotation = 270 }
V text frame-fill-line 'text box yellow fill + black line' $TextBase { param($d, $c) $c.s.Fill.Visible = $msoTrue; $c.s.Fill.ForeColor.RGB = (Rgb 255 255 0); $c.s.Line.Visible = $msoTrue }
V text line-break 'forced line break' $TextBase { param($d, $c) $c.r.Text = "Hello Example$([char]11)Text world" }
V text text-changed 'different text same length' $TextBase { param($d, $c) $c.r.Text = 'Jello Example Text worle' }
V text text-longer 'longer text' $TextBase { param($d, $c) $c.r.Text = 'Hello Example Text world, now with more words in it.' }
V text2 base 'two paragraphs' $Text2Base $null
V text2 p2-center 'second paragraph centred' $Text2Base { param($d, $c) $c.r.Paragraphs(2).ParagraphFormat.Alignment = 1 }
V text2 p2-bold 'second paragraph bold' $Text2Base { param($d, $c) $c.r.Paragraphs(2).Font.Bold = $msoTrue }
V text2 two-boxes 'second text box' $Text2Base { param($d, $c) $b = $d.Pages(1).Shapes.AddTextbox(1, 72, 300, 288, 72); $b.TextFrame.TextRange.Text = 'Another box' }
V text2 text-in-oval 'text inside an oval autoshape' $EmptyBase { param($d, $c) $o = $d.Pages(1).Shapes.AddShape(9, 72, 72, 216, 144); $o.TextFrame.TextRange.Text = 'In an oval' }

# ---------- E: pages & masters ----------
V pages one 'one page, rect' $RectBase $null
V pages size-5.75x4.75 'page 5.75 x 4.75 in' $RectBase { param($d, $c) $d.PageSetup.PageWidth = 414; $d.PageSetup.PageHeight = 342 }
V pages landscape 'page 11 x 8.5 in' $RectBase { param($d, $c) $d.PageSetup.PageWidth = 792; $d.PageSetup.PageHeight = 612 }
V pages two 'two pages, rect on page 1' $RectBase { param($d, $c) $d.Pages.Add(1, 1) }
V pages two-rect-p2 'two pages, second rect (red) on page 2' $RectBase { param($d, $c) $p2 = $d.Pages.Add(1, 1); $r = $p2.Shapes.AddShape(1, 300, 300, 100, 100); $r.Fill.ForeColor.RGB = (Rgb 255 0 0) }
V pages three-rect-p3 'three pages, second rect on page 3' $RectBase { param($d, $c) $d.Pages.Add(2, 1); $r = $d.Pages(3).Shapes.AddShape(1, 300, 300, 100, 100); $r.Fill.ForeColor.RGB = (Rgb 255 0 0) }
V pages master-rect 'green rect on master page' $RectBase { param($d, $c) $m = $d.MasterPages(1).Shapes.AddShape(1, 400, 600, 100, 100); $m.Fill.ForeColor.RGB = (Rgb 0 200 0) }
V pages master-ignored 'master rect, page ignores master' $RectBase { param($d, $c) $m = $d.MasterPages(1).Shapes.AddShape(1, 400, 600, 100, 100); $m.Fill.ForeColor.RGB = (Rgb 0 200 0); $d.Pages(1).IgnoreMaster = $true }
V pages master-text 'text box on master page' $RectBase { param($d, $c) $t = $d.MasterPages(1).Shapes.AddTextbox(1, 72, 700, 300, 40); $t.TextFrame.TextRange.Text = 'Master footer' }

# ---------- F: pictures ----------
V picture base 'picture 216x216' $PicBase $null
V picture moved 'picture at 100,120' $PicBase { param($d, $c) $c.s.Left = [double]100; $c.s.Top = [double]120 }
V picture crop-left 'crop left 54' $PicBase { param($d, $c) $c.s.PictureFormat.CropLeft = 54 }
V picture crop-top 'crop top 54' $PicBase { param($d, $c) $c.s.PictureFormat.CropTop = 54 }
V picture crop-all 'crop 54 each side' $PicBase { param($d, $c) $pf = $c.s.PictureFormat; $pf.CropLeft = 54; $pf.CropTop = 54; $pf.CropRight = 54; $pf.CropBottom = 54 }
V picture rotated-30 'picture rotated 30' $PicBase { param($d, $c) $c.s.Rotation = 30 }
V picture flip-h 'picture flipped horizontally' $PicBase { param($d, $c) $c.s.Flip(0) }
V picture border 'picture 5pt white border' $PicBase { param($d, $c) $c.s.Line.Visible = $msoTrue; $c.s.Line.Weight = 5; $c.s.Line.ForeColor.RGB = (Rgb 255 255 255) }
V picture oval-mask 'picture as oval' $PicBase { param($d, $c) $c.s.AutoShapeType = 9 }
V picture stretched 'picture 260x130' $PicBase { param($d, $c) $c.s.Width = [double]260; $c.s.Height = [double]130 }
V picture two 'second picture' $PicBase { param($d, $c) $d.Pages(1).Shapes.AddPicture($Picture, $msoFalse, $msoTrue, 320, 72, 100, 100) }

# ---------- G: WordArt ----------
V wordart base 'WordArt plain Arial Black 36' $WaBase $null
V wordart text 'WordArt different text' $WaBase { param($d, $c) $c.s.TextEffect.Text = 'Second Line' }
V wordart font 'WordArt font Times New Roman' $WaBase { param($d, $c) $c.s.TextEffect.FontName = 'Times New Roman' }
V wordart arch 'WordArt arch up (9)' $WaBase { param($d, $c) $c.s.TextEffect.PresetShape = 9 }
V wordart circle 'WordArt circle (11)' $WaBase { param($d, $c) $c.s.TextEffect.PresetShape = 11 }
V wordart fill-red 'WordArt fill red' $WaBase { param($d, $c) $c.s.Fill.ForeColor.RGB = (Rgb 200 0 0) }
V wordart line-white 'WordArt white outline 1.5' $WaBase { param($d, $c) $c.s.Line.Visible = $msoTrue; $c.s.Line.ForeColor.RGB = (Rgb 255 255 255); $c.s.Line.Weight = 1.5 }
V wordart shadow 'WordArt shadow' $WaBase { param($d, $c) $c.s.Shadow.Visible = $msoTrue }

# ---------- H: tables & groups ----------
V table 2x2 'table 2x2 with text' $EmptyBase { param($d, $c) $t = $d.Pages(1).Shapes.AddTable(2, 2, 72, 72, 288, 72).Table; $t.Rows(1).Cells(1).TextRange.Text = 'A1'; $t.Rows(1).Cells(2).TextRange.Text = 'B1'; $t.Rows(2).Cells(1).TextRange.Text = 'A2'; $t.Rows(2).Cells(2).TextRange.Text = 'B2' }
V table 3x2 'table 3 rows 2 cols' $EmptyBase { param($d, $c) $d.Pages(1).Shapes.AddTable(3, 2, 72, 72, 288, 108) }
V table merged 'table 2x2 top row merged' $EmptyBase { param($d, $c) $t = $d.Pages(1).Shapes.AddTable(2, 2, 72, 72, 288, 72).Table; $t.Rows(1).Cells(1).Merge($t.Rows(1).Cells(2)) }
V group two 'group of two rects' $RectBase { param($d, $c) $r2 = $d.Pages(1).Shapes.AddShape(1, 250, 72, 72, 72); $d.Pages(1).Shapes.Range([object[]]@($c.s.Name, $r2.Name)).Group() }
V group rotated 'group of two rects rotated 30' $RectBase { param($d, $c) $r2 = $d.Pages(1).Shapes.AddShape(1, 250, 72, 72, 72); $g = $d.Pages(1).Shapes.Range([object[]]@($c.s.Name, $r2.Name)).Group(); $g.Rotation = 30 }

try { }
finally {
    try { $pub.Quit() } catch {}
    Start-Sleep -Seconds 2
    if ($ours.Count) { Get-Process -Id $ours -ErrorAction SilentlyContinue | Stop-Process -Force }
    $log | Export-Csv (Join-Path $OutDir 'variants.csv') -NoTypeInformation
    $log | Group-Object Status | ForEach-Object { "{0}: {1}" -f $_.Name, $_.Count }
    $log | Where-Object Status -eq 'FAIL' | ForEach-Object { "FAIL $($_.Group)/$($_.Variant): $($_.Error)" }
}
