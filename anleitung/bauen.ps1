# Baut die Anleitung FreshGuest-Anleitung.html (eine Datei, Bilder eingebettet).
# Aufruf:  powershell -ExecutionPolicy Bypass -File anleitung\bauen.ps1
# Quelle: anleitung\vorlage.html, Bilder aus bilder\, Logo aus logo\FreshGuest.svg
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$tpl  = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'vorlage.html'), [Text.Encoding]::UTF8)

# Logo als eingebettetes SVG (ohne XML-Titel)
$svg = [IO.File]::ReadAllText((Join-Path $root 'logo\FreshGuest.svg'), [Text.Encoding]::UTF8)
$svg = $svg -replace '<title>.*?</title>', '' -replace 'width="256" height="256"', 'aria-hidden="true"'
$tpl = $tpl.Replace('{{LOGO}}', $svg)

# Version aus dem Quellcode
$cs = [IO.File]::ReadAllText((Join-Path $root 'src\FreshGuest.cs'), [Text.Encoding]::UTF8)
$ver = [regex]::Match($cs, 'Version = "([^"]+)"').Groups[1].Value
$tpl = $tpl.Replace('{{VERSION}}', $ver)

# Bilder einbetten
$fehlt = @()
$tpl = [regex]::Replace($tpl, '\{\{BILD:([A-Za-z0-9\-]+)\}\}', {
    param($m)
    $f = Join-Path $root ('bilder\' + $m.Groups[1].Value + '.png')
    if (-not (Test-Path $f)) { $script:fehlt += $m.Groups[1].Value; return '' }
    'data:image/png;base64,' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($f))
})
if ($fehlt.Count) { throw ('Bilder fehlen: ' + ($fehlt -join ', ')) }

$out = Join-Path $root 'FreshGuest-Anleitung.html'
[IO.File]::WriteAllText($out, $tpl, (New-Object Text.UTF8Encoding $false))
'OK: {0} ({1:N0} KB)' -f $out, ((Get-Item $out).Length / 1KB)
