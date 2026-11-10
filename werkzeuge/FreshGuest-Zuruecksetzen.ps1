# FreshGuest - alles zuruecksetzen (auch Reste alter Versionen und des alten "Gaeste-Schutz")
#
# Entfernt ALLES, was FreshGuest je eingerichtet hat: Shared PC Mode, Gast-Konten von Shared PC,
# geplante Aufgaben, Programm- und Datenordner, Browser-Richtlinien, Einstellungen im
# Windows-Standardprofil, Profilbilder, Anmeldebildschirm-Einstellungen.
# Deine eigenen Konten und deine Daten werden NICHT angefasst.
#
# Aufruf: Rechtsklick auf FreshGuest-Zuruecksetzen.cmd -> Als Administrator ausfuehren.
# Protokoll: FreshGuest-Zuruecksetzen_<PC>_<Datum>.txt neben diesem Skript.
# (Reines ASCII - PowerShell 5.1 liest Dateien ohne BOM als ANSI.)

$ErrorActionPreference = 'Continue'
$log = Join-Path $PSScriptRoot ('FreshGuest-Zuruecksetzen_{0}_{1}.txt' -f $env:COMPUTERNAME, (Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'))
function L([string]$t, [string]$farbe = 'Gray') {
    Write-Host $t -ForegroundColor $farbe
    try { Add-Content -Path $log -Value ('{0}  {1}' -f (Get-Date -Format 'HH:mm:ss'), $t) -Encoding UTF8 } catch { }
}

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Bitte als Administrator ausfuehren (Rechtsklick -> Als Administrator ausfuehren).' -ForegroundColor Red
    return
}
$meSid  = $id.User.Value
$meName = $id.Name.Split('\')[-1]

Write-Host ''
Write-Host 'FreshGuest - alles zuruecksetzen' -ForegroundColor Cyan
Write-Host 'Entfernt FreshGuest komplett (auch Reste alter Versionen) und schaltet den Shared PC Mode aus.'
Write-Host "Dein Konto ($meName) und deine Daten bleiben unveraendert. Danach: neu starten, FreshGuest neu installieren."
$a = Read-Host 'Fortfahren? (j/n)'
if ($a -notmatch '^(j|ja|y|yes|s|si)$') { Write-Host 'Abgebrochen.'; return }

L "Start - angemeldet als $meName ($meSid)"
$sharedPc = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedPC'
$daten    = Join-Path $env:ProgramData 'GaestePC'

# Einstellungen von FreshGuest lesen, bevor sie geloescht werden
$store = Get-ItemProperty 'HKLM:\SOFTWARE\GaesteSchutz' -ErrorAction SilentlyContinue
$cfg   = Get-ItemProperty 'HKLM:\SOFTWARE\FreshGuest' -ErrorAction SilentlyContinue

# --- 1) Geplante Aufgaben -------------------------------------------------------------
foreach ($t in @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object {
        $_.TaskName -in 'GaestePC-Bereinigung', 'FreshGuest-Profilbild', 'FreshGuest-Neustart', 'FreshGuest-Anmeldung', 'FreshGuest-Vorauswahl-Test' -or
        $_.TaskName -like 'GaestePC-Setup-*' -or $_.TaskName -like 'FreshGuest-Anmeldung-Test-*' })) {
    try { Unregister-ScheduledTask -TaskName $t.TaskName -Confirm:$false -ErrorAction Stop; L ('Aufgabe entfernt: ' + $t.TaskName) }
    catch { L ('Aufgabe ' + $t.TaskName + ': ' + $_.Exception.Message) 'Yellow' }
}
# Wache fuer den Anmeldebildschirm beenden und die Sperre "Nur Guest" aufheben (samt Resten der Tests)
Get-Process -Name FreshGuest -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq 0 } | Stop-Process -Force -ErrorAction SilentlyContinue
foreach ($p in @(@('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System', 'ExcludedCredentialProviders'),
                 @('HKLM:\SOFTWARE\Policies\Microsoft\Windows\System', 'ExcludedCredentialProviders'),
                 @('HKLM:\SOFTWARE\Policies\Microsoft\Windows\System', 'DefaultCredentialProvider'))) {
    if ($null -ne (Get-ItemProperty $p[0] -ErrorAction SilentlyContinue).($p[1])) {
        Remove-ItemProperty -Path $p[0] -Name $p[1] -ErrorAction SilentlyContinue
        L ('Anmeldebildschirm: ' + $p[1] + ' entfernt')
    }
}
Remove-Item (Join-Path $env:ProgramData 'FreshGuest-Test') -Recurse -Force -ErrorAction SilentlyContinue

# --- 2) Shared PC Mode ausschalten (geht nur als SYSTEM -> kurzlebige Aufgabe) ---------
$spcOk = $false
$tmp = Join-Path $env:windir 'Temp'
$sid8 = [guid]::NewGuid().ToString('N').Substring(0, 8)
$ps1 = Join-Path $tmp "fg-reset-$sid8.ps1"
$out = Join-Path $tmp "fg-reset-$sid8.txt"
$inner = @'
param([string]$OutFile)
try {
    $ns = 'root\cimv2\mdm\dmmap'
    $obj = Get-CimInstance -Namespace $ns -ClassName 'MDM_SharedPC' -ErrorAction SilentlyContinue
    if ($obj) {
        $obj.EnableSharedPCMode = $false
        $obj.EnableAccountManager = $false
        Set-CimInstance -CimInstance $obj -ErrorAction Stop
        Add-Content -Path $OutFile -Value 'OK'
    } else { Add-Content -Path $OutFile -Value 'OK (nie eingerichtet)' }
} catch { Add-Content -Path $OutFile -Value ('FEHLER: ' + $_.Exception.Message) }
Add-Content -Path $OutFile -Value 'ENDE'
'@
try {
    Set-Content -Path $ps1 -Value $inner -Encoding ASCII
    $task = "FreshGuest-Reset-$sid8"
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$ps1`" -OutFile `"$out`""
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Force | Out-Null
    Start-ScheduledTask -TaskName $task
    $ende = (Get-Date).AddMinutes(4)
    while ((Get-Date) -lt $ende -and -not ((Test-Path $out) -and ((Get-Content $out) -contains 'ENDE'))) { Start-Sleep -Seconds 2 }
    Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    $res = if (Test-Path $out) { @(Get-Content $out | Where-Object { $_ -ne 'ENDE' }) } else { @('FEHLER: keine Antwort (Zeitueberschreitung)') }
    $res | ForEach-Object { L ('Shared PC Mode ausschalten: ' + $_) }
    $spcOk = ($res | Where-Object { $_ -like 'OK*' }).Count -gt 0
} catch { L ('Shared PC Mode ausschalten FEHLER: ' + $_.Exception.Message) 'Red' }
finally { Remove-Item $ps1, $out -Force -ErrorAction SilentlyContinue }

if ($spcOk) {
    # Uebrig gebliebene Shared-PC-Einstellungen (z. B. "Gast erlaubt") entfernen -> Zustand wie vor der Installation
    # NodeValues mit entfernen: sonst schreibt Windows beim naechsten Einrichten AccountModel nicht neu (keine Gast-Kachel)
    foreach ($k in 'AccountManagement', 'NodeValues', 'Exemptions') {
        $p = Join-Path $sharedPc $k
        if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue; L "Entfernt: SharedPC\$k" }
    }
} else {
    L 'Shared PC Mode konnte nicht sicher ausgeschaltet werden - Einstellungen von Shared PC bleiben stehen.' 'Yellow'
}

# --- 3) Gast-Konten von Shared PC und verwaiste Profile ----------------------------------
$admins = @()
try { $admins = @(Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction Stop | ForEach-Object { $_.SID.Value }) } catch { }
$caKey = Join-Path $sharedPc 'CreatedLocalAccounts'
$gastNamen = @(Get-ChildItem $caKey -ErrorAction SilentlyContinue | ForEach-Object { $_.PSChildName })
foreach ($u in @(Get-LocalUser | Where-Object { $gastNamen -contains $_.Name })) {
    $sid = $u.SID.Value
    if ($sid -eq $meSid -or $admins -contains $sid) { L ('Uebersprungen (Admin): ' + $u.Name) 'Yellow'; continue }
    $prof = Get-CimInstance Win32_UserProfile | Where-Object { $_.SID -eq $sid }
    if ($prof -and $prof.Loaded) { L ('Uebersprungen (gerade angemeldet): ' + $u.Name) 'Yellow'; continue }
    if ($prof) { try { Remove-CimInstance -InputObject $prof -ErrorAction Stop; L ('Profil geloescht: ' + $prof.LocalPath) } catch { L ('Profil ' + $u.Name + ': ' + $_.Exception.Message) 'Yellow' } }
    try { Remove-LocalUser -SID $sid -ErrorAction Stop; L ('Gast-Konto geloescht: ' + $u.Name) } catch { L ('Konto ' + $u.Name + ': ' + $_.Exception.Message) 'Yellow' }
}
foreach ($n in $gastNamen) {
    if (-not (Get-LocalUser -Name $n -ErrorAction SilentlyContinue)) { Remove-Item (Join-Path $caKey $n) -Recurse -Force -ErrorAction SilentlyContinue }
}
# Profile von Konten, die es nicht mehr gibt (uebrig gebliebene Gaeste)
$konten = @(Get-LocalUser)
$praefix = $konten[0].SID.AccountDomainSid.Value
$lokal = @($konten | ForEach-Object { $_.SID.Value })
foreach ($p in @(Get-CimInstance Win32_UserProfile | Where-Object { -not $_.Special -and -not $_.Loaded -and $_.SID -like "$praefix-*" -and $lokal -notcontains $_.SID })) {
    try { Remove-CimInstance -InputObject $p -ErrorAction Stop; L ('Verwaistes Profil geloescht: ' + $p.LocalPath) } catch { L ('Profil ' + $p.LocalPath + ': ' + $_.Exception.Message) 'Yellow' }
}

# --- 4) Anmeldebildschirm ---------------------------------------------------------------
$sys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
try { Set-ItemProperty -Path $sys -Name dontdisplaylastusername -Value 0 -Type DWord -ErrorAction Stop; L 'Letzten Benutzer anzeigen: Windows-Standard (0)' } catch { }
$ul = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList'
foreach ($n in @(@($cfg.VersteckteKonten) + $meName | Where-Object { $_ } | Select-Object -Unique)) {
    if ($null -ne (Get-ItemProperty $ul -Name $n -ErrorAction SilentlyContinue)) {
        Remove-ItemProperty -Path $ul -Name $n -ErrorAction SilentlyContinue; L "Konto wieder sichtbar: $n"
    }
}
$ui = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI'
try {
    Set-ItemProperty -Path $ui -Name LastLoggedOnUser    -Value ".\$meName"
    Set-ItemProperty -Path $ui -Name LastLoggedOnSAMUser -Value ".\$meName"
    Set-ItemProperty -Path $ui -Name LastLoggedOnUserSID -Value $meSid
    Set-ItemProperty -Path $ui -Name SelectedUserSID     -Value $meSid
    Set-ItemProperty -Path $ui -Name LastLoggedOnProvider -Value '{60B78E88-EAD8-445C-9CFD-0B87F74EA6CD}'
    L "Anmeldebildschirm: zuletzt angemeldet = .\$meName (Kennwort)"
} catch { L ('Anmeldebildschirm: ' + $_.Exception.Message) 'Yellow' }

# --- 5) Browser-Richtlinien in HKLM (bis Version 1.7) ------------------------------------
$browserWerte = @{
    'SOFTWARE\Policies\Microsoft\Edge'  = 'HideFirstRunExperience', 'PasswordManagerEnabled', 'DefaultBrowserSettingEnabled', 'DefaultBrowserSettingsCampaignEnabled'
    'SOFTWARE\Policies\Google\Chrome'   = 'PromotionalTabsEnabled', 'PrivacySandboxPromptEnabled', 'BrowserSignin', 'PasswordManagerEnabled', 'DefaultBrowserSettingEnabled'
    'SOFTWARE\Policies\Mozilla\Firefox' = 'OverrideFirstRunPage', 'OverridePostUpdatePage', 'PasswordManagerEnabled', 'OfferToSaveLogins', 'DontCheckDefaultBrowser', 'DisableDefaultBrowserAgent'
    'SOFTWARE\Policies\Mozilla\Firefox\UserMessaging' = 'SkipOnboarding', 'MoreFromMozilla', 'ExtensionRecommendations', 'FeatureRecommendations'
    'SOFTWARE\Policies\Mozilla\Firefox\Homepage'      = 'URL', 'StartPage', 'Locked'
}
$leerReihenfolge = 'SOFTWARE\Policies\Mozilla\Firefox\UserMessaging', 'SOFTWARE\Policies\Mozilla\Firefox\Homepage', 'SOFTWARE\Policies\Mozilla\Firefox',
                   'SOFTWARE\Policies\Mozilla', 'SOFTWARE\Policies\Microsoft\Edge', 'SOFTWARE\Policies\Google\Chrome', 'SOFTWARE\Policies\Google'
function Remove-BrowserWerte([string]$wurzel) {
    $n = 0
    foreach ($k in $browserWerte.Keys) {
        $p = "$wurzel\$k"
        if (Test-Path $p) { foreach ($v in $browserWerte[$k]) { if ($null -ne (Get-ItemProperty $p -Name $v -ErrorAction SilentlyContinue)) { Remove-ItemProperty $p -Name $v; $n++ } } }
    }
    foreach ($k in $leerReihenfolge) {
        $p = "$wurzel\$k"
        if ((Test-Path $p) -and -not (Get-ChildItem $p) -and -not ((Get-Item $p).ValueCount)) { Remove-Item $p -ErrorAction SilentlyContinue }
    }
    return $n
}
L ('Browser-Richtlinien (alle Konten) entfernt: ' + (Remove-BrowserWerte 'HKLM:') + ' Werte')

# --- 6) Windows-Standardprofil (Einstellungen fuer neue Gaeste) ---------------------------
$profDir = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList' -ErrorAction SilentlyContinue).Default
if (-not $profDir) { $profDir = Join-Path $env:SystemDrive 'Users\Default' }
$profDir = [Environment]::ExpandEnvironmentVariables($profDir)
$hive = Join-Path $profDir 'NTUSER.DAT'
$mount = 'FreshGuest_Reset'
& reg.exe load "HKU\$mount" "$hive" | Out-Null
if ($LASTEXITCODE -eq 0) {
    try {
        $r = "Registry::HKEY_USERS\$mount"
        L ('Standardprofil: Browser-Richtlinien entfernt: ' + (Remove-BrowserWerte $r) + ' Werte')
        $safer = "$r\Software\Policies\Microsoft\Windows\Safer\CodeIdentifiers"
        if ((Test-Path $safer) -and (Get-ItemProperty $safer -ErrorAction SilentlyContinue).FreshGuest -eq 1) { Remove-Item $safer -Recurse -Force; L 'Standardprofil: Programm-Sperren entfernt' }
        $sysU = "$r\Software\Microsoft\Windows\CurrentVersion\Policies\System"
        foreach ($v in 'Wallpaper', 'WallpaperStyle', 'DisableRegistryTools', 'DisableCMD') { Remove-ItemProperty $sysU -Name $v -ErrorAction SilentlyContinue }
        Remove-ItemProperty "$r\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer" -Name NoControlPanel -ErrorAction SilentlyContinue
        foreach ($v in 'Device', 'LegacyDefaultPrinterMode') { Remove-ItemProperty "$r\Software\Microsoft\Windows NT\CurrentVersion\Windows" -Name $v -ErrorAction SilentlyContinue }
        L 'Standardprofil: Hintergrund, Sperren, Drucker zurueckgesetzt'
    } catch { L ('Standardprofil FEHLER: ' + $_.Exception.Message) 'Yellow' }
    finally {
        [gc]::Collect(); [gc]::WaitForPendingFinalizers()
        $ok = $false
        for ($i = 0; $i -lt 8 -and -not $ok; $i++) { & reg.exe unload "HKU\$mount" | Out-Null; $ok = ($LASTEXITCODE -eq 0); if (-not $ok) { Start-Sleep -Milliseconds 500 } }
        if (-not $ok) { L 'ACHTUNG: Standardprofil konnte nicht freigegeben werden - bitte sofort neu starten, bevor sich jemand anmeldet!' 'Red' }
    }
} else { L 'Standardprofil konnte nicht geoeffnet werden - uebersprungen' 'Yellow' }

# Desktop des Standardprofils: Browser-Symbole und Dateien, die FreshGuest dort abgelegt hat
$defDesk = Join-Path $profDir 'Desktop'
$namen = @('Internet (Edge).lnk', 'Internet (Chrome).lnk', 'Internet (Firefox).lnk') + @(("$($store.DesktopDateien)").Split('|') | Where-Object { $_ })
foreach ($n in $namen) { $f = Join-Path $defDesk $n; if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force; L "Gast-Desktop: entfernt $n" } }
$pub = [Environment]::GetFolderPath('CommonDesktopDirectory')
foreach ($n in 'Internet (Edge).lnk', 'Internet (Chrome).lnk', 'Internet (Firefox).lnk') { $f = Join-Path $pub $n; if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force; L "Oeffentlicher Desktop: entfernt $n" } }
$autostart = Join-Path ([Environment]::GetFolderPath('CommonStartup')) 'FreshGuest-Gaststart.lnk'
if (Test-Path -LiteralPath $autostart) { Remove-Item -LiteralPath $autostart -Force; L 'Autostart-Verknuepfung fuer Gaeste entfernt' }

# --- 7) Profilbilder -------------------------------------------------------------------
$orig = Join-Path $daten 'Profilbild-Original'
$picDir = Join-Path $env:ProgramData 'Microsoft\User Account Pictures'
if (Test-Path $orig) { Get-ChildItem $orig -File | ForEach-Object { Copy-Item $_.FullName (Join-Path $picDir $_.Name) -Force }; L 'Windows-Standardbilder wiederhergestellt' }
$apKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users'
foreach ($k in @(Get-ChildItem $apKey -ErrorAction SilentlyContinue)) {
    if ((Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue).FreshGuest -eq 1) {
        Remove-Item $k.PSPath -Recurse -Force
        Remove-Item (Join-Path $env:SystemDrive ('Users\Public\AccountPictures\' + $k.PSChildName)) -Recurse -Force -ErrorAction SilentlyContinue
        L ('Profilbild-Eintrag entfernt: ' + $k.PSChildName)
    }
}

# --- 8) Einstellungen, Programm, Verknuepfungen ----------------------------------------
foreach ($k in 'HKLM:\SOFTWARE\FreshGuest', 'HKLM:\SOFTWARE\GaesteSchutz') { if (Test-Path $k) { Remove-Item $k -Recurse -Force; L "Entfernt: $k" } }
foreach ($d in (Join-Path $env:ProgramFiles 'FreshGuest'), (Join-Path $env:ProgramFiles 'GaesteSchutz'), $daten) {
    if (Test-Path $d) { try { Remove-Item $d -Recurse -Force -ErrorAction Stop; L "Ordner entfernt: $d" } catch { L ("Ordner $d (laeuft FreshGuest noch?): " + $_.Exception.Message) 'Yellow' } }
}
foreach ($dir in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('DesktopDirectory')) {
    Get-ChildItem $dir -Filter '*.lnk' -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq 'FreshGuest.lnk' -or $_.Name -like 'G*ste-Schutz.lnk' } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force; L ('Verknuepfung entfernt: ' + $_.FullName) }
}

L 'FERTIG. Bitte jetzt neu starten und danach FreshGuest neu installieren.' 'Green'
L "Protokoll: $log"
