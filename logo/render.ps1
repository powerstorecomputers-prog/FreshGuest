# Rendert die Logo-SVGs mit Microsoft Edge (headless) zu PNG und baut FreshGuest.ico.
# Aufruf:  powershell -ExecutionPolicy Bypass -File logo\render.ps1
# Aendert nichts am System; schreibt nur in den Ordner logo\.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$dir  = $PSScriptRoot
$edge = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'
if (-not (Test-Path $edge)) { $edge = Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe' }
$work = Join-Path $env:TEMP 'freshguest-render'
New-Item -ItemType Directory -Force -Path $work | Out-Null

function Render-Svg([string]$svg, [int]$w, [int]$h, [int]$scale, [string]$png) {
    $html = Join-Path $dir ('_render_' + [IO.Path]::GetFileNameWithoutExtension($svg) + '.html')
    $page = "<!doctype html><html><head><meta charset='utf-8'><style>html,body{margin:0;background:transparent}</style></head>" +
            "<body><object data='$svg' type='image/svg+xml' width='$w' height='$h'></object></body></html>"
    Set-Content -Path $html -Value $page -Encoding UTF8
    $uri = 'file:///' + ($html -replace '\\', '/')
    $edgeArgs = @('--headless=new', '--disable-gpu', '--hide-scrollbars', "--user-data-dir=`"$work`"",
              '--default-background-color=00000000', "--force-device-scale-factor=$scale",
              '--virtual-time-budget=4000', "--window-size=$w,$h", "--screenshot=`"$png`"", $uri)
    Start-Process -FilePath $edge -ArgumentList $edgeArgs -Wait -WindowStyle Hidden
    Remove-Item $html -Force
    if (-not (Test-Path $png)) { throw "Rendern fehlgeschlagen: $svg" }
}

function Resize-Png([string]$src, [int]$size, [string]$dst) {
    $img = [Drawing.Image]::FromFile($src)
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.SmoothingMode = 'HighQuality'
    $g.Clear([Drawing.Color]::Transparent)
    $g.DrawImage($img, 0, 0, $size, $size)
    $bmp.Save($dst, [Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose(); $img.Dispose()
}

# 1) Master-Bilder
$master = Join-Path $dir 'FreshGuest-1024.png'
$small  = Join-Path $work 'klein-256.png'
Render-Svg 'FreshGuest.svg' 512 512 2 $master
Render-Svg 'FreshGuest-klein.svg' 256 256 1 $small
Render-Svg 'FreshGuest-Schriftzug.svg' 780 200 2 (Join-Path $dir 'FreshGuest-Schriftzug.png')
Render-Svg 'FreshGuest-Schriftzug-hell.svg' 780 200 2 (Join-Path $dir 'FreshGuest-Schriftzug-hell.png')

# 2) Groessen: gross aus dem Master, sehr klein aus der vereinfachten Version
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
foreach ($s in $sizes) {
    $src = if ($s -le 24) { $small } else { $master }
    Resize-Png $src $s (Join-Path $work "icon-$s.png")
}
foreach ($s in 16, 32, 48, 256) { Copy-Item (Join-Path $work "icon-$s.png") (Join-Path $dir "FreshGuest-$s.png") -Force }

# 3) ICO mit PNG-Eintraegen
$ico = Join-Path $dir 'FreshGuest.ico'
$data = foreach ($s in $sizes) { ,[IO.File]::ReadAllBytes((Join-Path $work "icon-$s.png")) }
$fs = [IO.File]::Create($ico); $w = New-Object IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int32]$data[$i].Length); $w.Write([int32]$offset)
    $offset += $data[$i].Length
}
foreach ($d in $data) { $w.Write($d) }
$w.Close()
Write-Host "OK: $ico"
