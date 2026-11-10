<#
.SYNOPSIS
    Gaeste-PC fuer die Ferienwohnung: Windows "Shared PC Mode" mit Gastkonto.
    PC per ospiti / guest PC (Deutsch / Italiano / English).

.DESCRIPTION
    Nach dem Einrichten gibt es auf dem Anmeldebildschirm einen Button "Gast".
    Das Gastkonto wird beim Abmelden bzw. Neustart von Windows komplett geloescht
    (Browserverlauf, Logins, Downloads, Dokumente). Der naechste Gast bekommt ein
    frisches, leeres Konto. Gaeste haben keine Admin-Rechte.

    Bestehende Konten (dein Admin-Konto) bleiben erhalten und werden zusaetzlich
    ausdruecklich von der Loeschung ausgenommen.

    Zusaetzlich wird eine Bereinigung beim Systemstart eingerichtet, die
    uebrig gebliebene Profile geloeschter Gastkonten entfernt (z. B. nach Stromausfall).

    Hinweis: Diese Datei bleibt absichtlich reines ASCII (PowerShell 5.1 liest
    Dateien ohne BOM als ANSI). Italienische Akzente daher als Apostroph (e', piu').

.PARAMETER Modus
    Status       - nur anzeigen, nichts aendern (Standard)
    Einrichten   - Shared PC Mode + Bereinigung einschalten
    Rueckgaengig - Shared PC Mode + Bereinigung wieder ausschalten
    Aktualisieren - nach einem Programm-Update: Bereinigung + Anmeldebildschirm neu schreiben

.PARAMETER Sprache
    de / it / en. Ohne Angabe: Windows-Sprache.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\GaestePC.ps1 -Modus Einrichten
#>
[CmdletBinding()]
param(
    [ValidateSet('Status', 'Einrichten', 'Rueckgaengig', 'Aktualisieren')]
    [string]$Modus = 'Status',
    [ValidateSet('', 'de', 'it', 'en')]
    [string]$Sprache = '',
    [switch]$OhneRueckfrage
)

$ErrorActionPreference = 'Stop'

if (-not $Sprache) {
    $Sprache = switch ((Get-UICulture).TwoLetterISOLanguageName) { 'de' { 'de' } 'it' { 'it' } default { 'en' } }
}

$BaseDir       = Join-Path $env:ProgramData 'GaestePC'
$LogFile       = Join-Path $BaseDir 'GaestePC.log'
$CleanupScript = Join-Path $BaseDir 'Bereinigung.ps1'
$CleanupTask   = 'GaestePC-Bereinigung'
$SharedPCKey   = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedPC'
$ExemptionsKey = Join-Path $SharedPCKey 'Exemptions'

# ---------------------------------------------------------------------------
# Hilfsfunktionen
# ---------------------------------------------------------------------------

# Text in der gewaehlten Sprache
function T([string]$de, [string]$it, [string]$en) {
    switch ($Sprache) { 'it' { $it } 'en' { $en } default { $de } }
}

function Test-Ja([string]$a) { return ($a -match '^(j|ja|y|yes|s|si)$') }

function Write-Log {
    param([string]$Text, [string]$Farbe = 'Gray')
    Write-Host $Text -ForegroundColor $Farbe
    try {
        if (Test-Path $BaseDir) {
            $line = '{0}  [{1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Modus, $Text
            Add-Content -Path $LogFile -Value $line -Encoding UTF8
        }
    } catch { }
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Der Datenordner enthaelt Skripte, die als SYSTEM laufen. Gaeste (normale Benutzer)
# duerfen dort nichts anlegen oder aendern, sonst koennten sie SYSTEM-Rechte erlangen.
function Protect-BaseDir {
    $okOwner = @('S-1-5-32-544', 'S-1-5-18', [Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
    if (Test-Path $BaseDir) {
        $ownerSid = ''
        try { $ownerSid = (Get-Acl $BaseDir).GetOwner([Security.Principal.SecurityIdentifier]).Value } catch { }
        if ($okOwner -notcontains $ownerSid) {
            # Ordner wurde nicht von einem Admin angelegt -> verwerfen und neu anlegen
            Remove-Item $BaseDir -Recurse -Force
        }
    }
    if (-not (Test-Path $BaseDir)) { New-Item -ItemType Directory -Path $BaseDir -Force | Out-Null }
    & icacls.exe $BaseDir /setowner '*S-1-5-32-544' /T /C /Q | Out-Null
    & icacls.exe $BaseDir /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' /C /Q | Out-Null
    if (Get-ChildItem $BaseDir -Force -ErrorAction SilentlyContinue) {
        & icacls.exe (Join-Path $BaseDir '*') /reset /T /C /Q | Out-Null
    }
}

function Wait-Ende {
    if (-not $OhneRueckfrage) {
        [void](Read-Host ("`n" + (T 'Enter druecken zum Schliessen' 'Premere Invio per chiudere' 'Press Enter to close')))
    }
}

function Get-EditionInfo {
    $cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $name = $cv.ProductName
    # Windows 11 meldet in der Registry weiterhin "Windows 10"
    if ([int]$cv.CurrentBuildNumber -ge 22000) { $name = $name -replace 'Windows 10', 'Windows 11' }
    [pscustomobject]@{
        EditionID = $cv.EditionID
        Name      = $name
        Build     = [int]$cv.CurrentBuildNumber
        IsHome    = ($cv.EditionID -like 'Core*')
    }
}

# Fuehrt ein Skript als SYSTEM aus (noetig fuer die MDM-WMI-Schnittstelle),
# ueber eine kurzlebige geplante Aufgabe. Kein PsExec noetig.
function Invoke-AlsSystem {
    param([Parameter(Mandatory)][string]$InnerScript, [int]$TimeoutSek = 300)

    $id   = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $ps1  = Join-Path $BaseDir "als-system-$id.ps1"
    $out  = Join-Path $BaseDir "als-system-$id.txt"
    $task = "GaestePC-Setup-$id"

    Set-Content -Path $ps1 -Value $InnerScript -Encoding UTF8

    $action    = New-ScheduledTaskAction -Execute 'powershell.exe' `
                   -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$ps1`" -OutFile `"$out`""
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                   -ExecutionTimeLimit (New-TimeSpan -Seconds $TimeoutSek)

    Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    try {
        Start-ScheduledTask -TaskName $task
        $deadline = (Get-Date).AddSeconds($TimeoutSek)
        $fertig = $false
        while (-not $fertig -and (Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 2
            if (Test-Path $out) { $fertig = (Get-Content $out -Encoding UTF8) -contains 'ENDE' }
        }
    } finally {
        Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    }

    $result = if (Test-Path $out) { @(Get-Content $out -Encoding UTF8 | Where-Object { $_ -ne 'ENDE' }) }
              else { @((T 'FEHLER: Keine Antwort vom SYSTEM-Task (Zeitueberschreitung).' 'ERRORE: Nessuna risposta dall''attivita'' SYSTEM (timeout).' 'ERROR: No answer from the SYSTEM task (timeout).')) }
    Remove-Item $ps1, $out -ErrorAction SilentlyContinue
    return $result
}

# Wird als SYSTEM ausgefuehrt: liest bzw. setzt die SharedPC-Einstellungen.
# ('OK' / 'ENDE' sind Steuerzeilen und bleiben unuebersetzt.)
$InnerTemplate = @'
param([string]$OutFile)
function Out-Line([string]$t) { Add-Content -Path $OutFile -Value $t -Encoding UTF8 }
$ErrorActionPreference = 'Stop'
try {
    $ns = 'root\cimv2\mdm\dmmap'
    $obj = Get-CimInstance -Namespace $ns -ClassName 'MDM_SharedPC' -ErrorAction SilentlyContinue
    if (-not $obj) {
        $obj = New-CimInstance -Namespace $ns -ClassName 'MDM_SharedPC' `
                 -Property @{ ParentID = './Vendor/MSFT'; InstanceID = 'SharedPC' }
    }
    __AKTION__
    $obj = Get-CimInstance -Namespace $ns -ClassName 'MDM_SharedPC'
    foreach ($p in 'EnableSharedPCMode','EnableAccountManager','AccountModel','DeletionPolicy','SetEduPolicies','SetPowerPolicies','RestrictLocalStorage') {
        Out-Line ("  {0} = {1}" -f $p, $obj.$p)
    }
    Out-Line 'OK'
} catch {
    Out-Line ('FEHLER/ERRORE/ERROR: ' + $_.Exception.Message)
} finally {
    Out-Line 'ENDE'
}
'@

$AktionEinrichten = @'
    # 1) Einstellungen - muessen laut Microsoft VOR EnableSharedPCMode gesetzt sein
    $obj.EnableAccountManager = $true
    $obj.AccountModel         = 2       # Gast-Button + bestehende Konten bleiben nutzbar
    $obj.DeletionPolicy       = 0       # Konten sofort loeschen
    $obj.SetEduPolicies       = $false  # keine Schul-Richtlinien
    $obj.SetPowerPolicies     = $false  # Energieeinstellungen nicht anfassen
    $obj.RestrictLocalStorage = $false  # Gaeste duerfen z. B. Bordkarten speichern/drucken
    Set-CimInstance -CimInstance $obj
    # 2) Modus einschalten
    $obj = Get-CimInstance -Namespace $ns -ClassName 'MDM_SharedPC'
    $obj.EnableSharedPCMode = $true
    Set-CimInstance -CimInstance $obj
'@

$AktionRueckgaengig = @'
    $obj.EnableSharedPCMode   = $false
    $obj.EnableAccountManager = $false
    Set-CimInstance -CimInstance $obj
'@

$AktionLesen = '    # nur lesen'

# Bereinigung beim Systemstart (laeuft als SYSTEM).
# Loescht NUR verwaiste Profile: lokale Profile, deren Konto nicht mehr existiert
# (= uebrig gebliebene Gast-Profile). Ausgenommene Konten werden nie angefasst.
$CleanupContent = @'
param([switch]$NurAnzeigen, [switch]$NurAnmeldung, [switch]$AnmeldungZuruecksetzen)
$ErrorActionPreference = 'Continue'
$log = Join-Path $env:ProgramData 'GaestePC\Bereinigung.log'
function L([string]$t) {
    try { Add-Content -Path $log -Value ('{0}  {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $t) -Encoding UTF8 -ErrorAction Stop } catch { }
    Write-Host $t
}

# Lokale Gruppenrichtlinie (C:\Windows\System32\GroupPolicy\Machine\Registry.pol).
# Shared PC Mode schreibt dort u. a. dontdisplaylastusername = 1 hinein. Windows wendet diese Datei
# bei jedem Start und bei jeder Richtlinien-Aktualisierung neu an - darum reicht es nicht, nur den
# Registry-Wert zu setzen. Wir aendern NUR den DWORD-Wert dieses einen Eintrags an Ort und Stelle
# (alle anderen Eintraege bleiben unberuehrt). Aufbau: "PReg"+Version, dann [Schluessel;Wert;Typ;Groesse;Daten] in UTF-16.
function Read-PolText([byte[]]$b, [ref]$p) {
    $s = $p.Value
    while ($p.Value + 1 -lt $b.Length -and -not ($b[$p.Value] -eq 0 -and $b[$p.Value + 1] -eq 0)) { $p.Value += 2 }
    $t = [Text.Encoding]::Unicode.GetString($b, $s, $p.Value - $s)
    $p.Value += 2
    return $t
}
function Set-LgpoDword([string]$schluessel, [string]$name, [int]$wert) {
    $pol = Join-Path $env:windir 'System32\GroupPolicy\Machine\Registry.pol'
    if (-not (Test-Path $pol)) { return 'keine Registry.pol' }
    try {
        $b = [IO.File]::ReadAllBytes($pol)
        if ($b.Length -lt 8 -or [Text.Encoding]::ASCII.GetString($b, 0, 4) -ne 'PReg') { return 'Registry.pol unbekanntes Format' }
        $pos = 8
        while ($pos + 2 -lt $b.Length) {
            if ($b[$pos] -ne 0x5B) { return "Registry.pol: unerwartetes Zeichen bei Byte $pos - nichts geaendert" }
            $pos += 2
            $k = Read-PolText $b ([ref]$pos); $pos += 2
            $v = Read-PolText $b ([ref]$pos); $pos += 2
            $typ = [BitConverter]::ToUInt32($b, $pos); $pos += 6
            $len = [BitConverter]::ToUInt32($b, $pos); $pos += 6
            if ($k -eq $schluessel -and $v -eq $name -and $typ -eq 4 -and $len -eq 4) {
                $alt = [BitConverter]::ToUInt32($b, $pos)
                if ($alt -eq $wert) { return "Registry.pol unveraendert ($alt)" }
                $sicherung = "$pol.vor-freshguest"
                if (-not (Test-Path $sicherung)) { Copy-Item $pol $sicherung -Force }
                [BitConverter]::GetBytes([uint32]$wert).CopyTo($b, $pos)
                [IO.File]::WriteAllBytes($pol, $b)
                return "Registry.pol $alt -> $wert"
            }
            $pos += $len + 2
        }
        return 'Registry.pol: Eintrag nicht vorhanden'
    } catch { return ('Registry.pol FEHLER: ' + $_.Exception.Message) }
}

# Automatische Anmeldung von Windows (Winlogon AutoAdminLogon). Ist sie eingeschaltet, versucht Windows
# nach jedem Start, sich selbst anzumelden. Fehlt Name oder Kennwort (z. B. ein geloeschtes Gast-Konto
# oder Reste eines frueheren Programms), erscheint "Anderer Benutzer" mit der Meldung "Benutzername und
# Kennwort eingeben". Ein Gaeste-PC darf sich NIE selbst anmelden (sonst saesse ein Gast in deinem Konto)
# -> ausschalten und ein dafuer gespeichertes Klartext-Kennwort entfernen. Protokolliert ohne Kennwort.
function Stop-AutoAnmeldung {
    $wl = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
    try {
        $p = Get-ItemProperty $wl -ErrorAction Stop
        $pw = if ($null -ne $p.DefaultPassword) { 'vorhanden' } else { '-' }
        L ("Automatische Anmeldung: AutoAdminLogon='{0}' DefaultUserName='{1}' DefaultDomainName='{2}' ForceAutoLogon='{3}' AutoLogonCount='{4}' AutoLogonSID='{5}' DefaultPassword={6}" -f `
            $p.AutoAdminLogon, $p.DefaultUserName, $p.DefaultDomainName, $p.ForceAutoLogon, $p.AutoLogonCount, $p.AutoLogonSID, $pw)
        if (("$($p.AutoAdminLogon)").Trim() -notin @('', '0')) {
            Set-ItemProperty -Path $wl -Name AutoAdminLogon -Value '0' -Type String
            foreach ($n in 'ForceAutoLogon', 'AutoLogonCount', 'DefaultPassword', 'AutoLogonSID') {
                Remove-ItemProperty -Path $wl -Name $n -ErrorAction SilentlyContinue
            }
            L 'Automatische Anmeldung AUSGESCHALTET (Gaeste-PC meldet sich nie selbst an)'
        }
    } catch { L ('Automatische Anmeldung: FEHLER ' + $_.Exception.Message) }
}

# Gast-Kachel von Shared PC: Sie haengt an SharedPC\AccountManagement\AccountModel (2 = Gast erlaubt).
# Windows schreibt den Wert nur, wenn sich die Einstellung aus seiner Sicht aendert (SharedPC\NodeValues).
# Fehlt er (z. B. nach einer Bereinigung), gibt es keine Gast-Kachel -> bei eingeschaltetem Shared PC ergaenzen.
# Beim Ausschalten wird er entfernt (sonst bleibt die Gast-Kachel), samt den gemerkten Werten, damit
# Windows ihn beim naechsten Einrichten wieder schreibt.
function Repair-GastEinstellung([switch]$Entfernen) {
    $spc = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedPC'
    $am  = Join-Path $spc 'AccountManagement'
    $nv  = Join-Path $spc 'NodeValues'
    try {
        if ($Entfernen) {
            foreach ($n in 'AccountModel', 'DeletionPolicy') { Remove-ItemProperty -Path $am -Name $n -ErrorAction SilentlyContinue }
            foreach ($n in '09', '10') { Remove-ItemProperty -Path $nv -Name $n -ErrorAction SilentlyContinue }
            L 'Shared PC: Gast-Einstellung entfernt (keine Gast-Kachel mehr)'
            return
        }
        if ((Get-ItemProperty $spc -ErrorAction SilentlyContinue).SharedPCMode -ne 1) { return }
        if (-not (Test-Path $am)) { New-Item -Path $am -Force | Out-Null }
        $p = Get-ItemProperty $am -ErrorAction SilentlyContinue
        if ($p.AccountModel -ne 2) {
            New-ItemProperty -Path $am -Name AccountModel -Value 2 -PropertyType DWord -Force | Out-Null
            L ("Shared PC: AccountModel war '" + $p.AccountModel + "' -> 2 (Gast-Kachel)")
        }
        if ($null -eq $p.DeletionPolicy) {
            New-ItemProperty -Path $am -Name DeletionPolicy -Value 0 -PropertyType DWord -Force | Out-Null
            L 'Shared PC: DeletionPolicy fehlte -> 0 (Gast sofort loeschen)'
        }
    } catch { L ('Shared PC Gast-Einstellung FEHLER: ' + $_.Exception.Message) }
}

# Gast-Konten, die Shared PC selbst angelegt hat (shpc...), duerfen NIE geschuetzt sein -
# sonst bleiben sie samt Daten erhalten und bekommen keine Gast-Einstellungen.
function Remove-GastSchutz {
    $exKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedPC\Exemptions'
    $namen = @(Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedPC\CreatedLocalAccounts' -ErrorAction SilentlyContinue | ForEach-Object { $_.PSChildName })
    if (-not $namen.Count) { return }
    foreach ($u in @(Get-LocalUser | Where-Object { $namen -contains $_.Name })) {
        $k = Join-Path $exKey $u.SID.Value
        if (Test-Path $k) {
            Remove-Item $k -Recurse -Force -ErrorAction SilentlyContinue
            L ('Gast-Konto ' + $u.Name + ' (' + $u.SID.Value + ') war faelschlich geschuetzt -> Schutz entfernt')
        }
    }
}

# Anmeldebildschirm. Shared PC Mode schaltet die Richtlinie "Interaktive Anmeldung: Letzten
# angemeldeten Benutzer nicht anzeigen" ein. Dann zeigt Windows nach jedem Start das leere
# Formular "Anderer Benutzer" (Benutzername + Kennwort).
#   Standard  : Richtlinie AUS -> Kacheln "Gast" + dein Konto, kein leeres Formular.
#   Nur Gast  : Richtlinie AN + deine Konten-Kacheln ausgeblendet -> nur "Gast"; du meldest dich
#               ueber "Anderer Benutzer" an. Ausgeblendet wird NUR, wenn die Richtlinie sicher AN
#               ist - sonst gaebe es kein "Anderer Benutzer" und du kaemst nicht mehr an den PC.
# Die Richtlinie wird in der lokalen Sicherheitsrichtlinie gesetzt (sonst setzt Windows sie beim
# naechsten Richtlinien-Abgleich zurueck) und direkt in der Registry.
function Set-Anmeldebildschirm([switch]$Zuruecksetzen) {
    $sysKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
    $cfgKey = 'HKLM:\SOFTWARE\FreshGuest'
    $ulKey  = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList'
    $exKey  = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedPC\Exemptions'
    $nurGast = $false
    if (-not $Zuruecksetzen) { $nurGast = ((Get-ItemProperty $cfgKey -ErrorAction SilentlyContinue).NurGastKachel -eq 1) }
    $soll = if ($nurGast) { 1 } else { 0 }

    $vorher = (Get-ItemProperty $sysKey -Name DontDisplayLastUserName -ErrorAction SilentlyContinue).DontDisplayLastUserName
    $id  = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $inf = Join-Path $env:windir "Temp\freshguest-anmeldung-$id.inf"
    try {
        Set-Content -Path $inf -Encoding Unicode -Value @(
            '[Unicode]', 'Unicode=yes',
            '[Version]', 'signature="$CHICAGO$"', 'Revision=1',
            '[Registry Values]',
            ('MACHINE\Software\Microsoft\Windows\CurrentVersion\Policies\System\DontDisplayLastUserName=4,' + $soll))
        $db = Join-Path $env:windir 'security\database\secedit.sdb'
        & secedit.exe /configure /db $db /cfg $inf /areas SECURITYPOLICY /quiet | Out-Null
        $rc = $LASTEXITCODE
    } catch { $rc = $_.Exception.Message }
    finally { Remove-Item $inf -Force -ErrorAction SilentlyContinue }
    $lgpo = Set-LgpoDword 'Software\Microsoft\Windows\CurrentVersion\Policies\System' 'DontDisplayLastUserName' $soll
    try { Set-ItemProperty -Path $sysKey -Name DontDisplayLastUserName -Value $soll -Type DWord -ErrorAction Stop } catch { }
    $nachher = (Get-ItemProperty $sysKey -Name DontDisplayLastUserName -ErrorAction SilentlyContinue).DontDisplayLastUserName
    L "Anmeldebildschirm: NurGast=$nurGast DontDisplayLastUserName vorher=$vorher nachher=$nachher (secedit $rc; $lgpo)"

    # Kacheln deiner (geschuetzten) Konten. Wir merken uns, welche WIR ausgeblendet haben,
    # damit wir nie Eintraege anfassen, die jemand anders gesetzt hat.
    $versteckt = @((Get-ItemProperty $cfgKey -ErrorAction SilentlyContinue).VersteckteKonten | Where-Object { $_ })
    if ($nurGast -and $nachher -eq 1) {
        $ex = @(Get-ChildItem $exKey -ErrorAction SilentlyContinue | ForEach-Object { $_.PSChildName })
        foreach ($u in @(Get-LocalUser | Where-Object { $_.Enabled -and $ex -contains $_.SID.Value })) {
            $n = $u.Name
            $schonDa = $null -ne (Get-ItemProperty $ulKey -Name $n -ErrorAction SilentlyContinue)
            if ($schonDa -and $versteckt -notcontains $n) { continue }
            if (-not (Test-Path $ulKey)) { New-Item -Path $ulKey -Force | Out-Null }
            New-ItemProperty -Path $ulKey -Name $n -Value 0 -PropertyType DWord -Force | Out-Null
            if ($versteckt -notcontains $n) { $versteckt += $n }
        }
    } else {
        if ($nurGast) { L 'Anmeldebildschirm: WARNUNG - Richtlinie ist nicht AN, Konten bleiben aus Sicherheitsgruenden sichtbar' }
        foreach ($n in $versteckt) { Remove-ItemProperty -Path $ulKey -Name $n -ErrorAction SilentlyContinue }
        $versteckt = @()
    }
    if ($versteckt.Count) {
        if (-not (Test-Path $cfgKey)) { New-Item -Path $cfgKey -Force | Out-Null }
        New-ItemProperty -Path $cfgKey -Name 'VersteckteKonten' -Value ([string[]]$versteckt) -PropertyType MultiString -Force | Out-Null
    } elseif (Test-Path $cfgKey) {
        Remove-ItemProperty -Path $cfgKey -Name 'VersteckteKonten' -ErrorAction SilentlyContinue
    }
    L ('Anmeldebildschirm: ausgeblendete Konten: ' + $(if ($versteckt.Count) { $versteckt -join ', ' } else { '-' }))

    # Vorauswahl "Gast": Windows waehlt nach dem Start das zuletzt benutzte Konto bzw. den zuletzt benutzten
    # Anmelde-Anbieter. Wir tragen den Anbieter von Shared PC ("Guest") ein und leeren das Konto.
    # (Die fruehere Meldung "Benutzername und Kennwort eingeben" kam vom Admin-Konto OHNE Kennwort -
    # Windows versuchte es nach dem Start automatisch anzumelden. Mit Kennwort tritt sie nicht auf.)
    # Laeuft beim Start und 10 Sekunden nach jeder Anmeldung (Windows schreibt den Eintrag beim Anmelden).
    $ui = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI'
    $cpRoot = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers'
    $gastCp = @(Get-ChildItem $cpRoot -ErrorAction SilentlyContinue | Where-Object { $_.GetValue('') -eq 'Windows.SharedPC.CredentialProvider' } | ForEach-Object { $_.PSChildName }) | Select-Object -First 1
    $kennwortCp = '{60B78E88-EAD8-445C-9CFD-0B87F74EA6CD}'
    try {
        $lu = Get-ItemProperty $ui -ErrorAction Stop
        $vorUser = $lu.LastLoggedOnUser; $vorCp = $lu.LastLoggedOnProvider
        if ($Zuruecksetzen) {
            if ($gastCp -and $vorCp -eq $gastCp) {
                Set-ItemProperty -Path $ui -Name LastLoggedOnProvider -Value $kennwortCp
                L 'Anmeldebildschirm: Vorauswahl Gast entfernt'
            }
        } elseif ($gastCp) {
            if ($vorCp -ne $gastCp -or $vorUser) {
                foreach ($n in 'LastLoggedOnUser', 'LastLoggedOnSAMUser', 'LastLoggedOnDisplayName', 'LastLoggedOnUserSID', 'SelectedUserSID') {
                    Remove-ItemProperty -Path $ui -Name $n -ErrorAction SilentlyContinue
                }
                Set-ItemProperty -Path $ui -Name LastLoggedOnProvider -Value $gastCp
                L "Anmeldebildschirm: Vorauswahl = Gast (vorher: '$vorUser' $vorCp)"
            } else {
                L 'Anmeldebildschirm: Vorauswahl = Gast - ok'
            }
        } else {
            L 'Anmeldebildschirm: Anmelde-Anbieter von Shared PC nicht gefunden - keine Vorauswahl'
        }
    } catch { L ('Anmeldebildschirm: Vorauswahl FEHLER: ' + $_.Exception.Message) }
}

# "Nur Guest anzeigen": FreshGuest.exe --anmeldewache (Aufgabe FreshGuest-Anmeldung) schliesst per
# ExcludedCredentialProviders alle Anmelde-Wege ausser "Guest" aus und hebt das auf Strg+Alt+F wieder auf.
# Sicherheitsnetz: Die Sperre darf nur bestehen, solange FreshGuest sie auch wieder aufheben kann -
# sonst kaeme niemand mehr an den PC. Fehlt etwas davon, wird sie hier entfernt (bei jedem Start).
function Test-AnmeldeSperre([switch]$Entfernen) {
    $sysKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
    try {
        if ($Entfernen) {
            Unregister-ScheduledTask -TaskName 'FreshGuest-Anmeldung' -Confirm:$false -ErrorAction SilentlyContinue
            Get-Process -Name FreshGuest -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq 0 } | Stop-Process -Force -ErrorAction SilentlyContinue
        }
        $wert = (Get-ItemProperty $sysKey -ErrorAction SilentlyContinue).ExcludedCredentialProviders
        if (-not $wert) { return }
        $grund = $null
        if ($Entfernen) { $grund = 'Schutz wird ausgeschaltet' }
        elseif ((Get-ItemProperty 'HKLM:\SOFTWARE\FreshGuest' -ErrorAction SilentlyContinue).NurGuest -ne 1) { $grund = 'Einstellung "Nur Guest" ist aus' }
        elseif (-not (Test-Path (Join-Path $env:ProgramFiles 'FreshGuest\FreshGuest.exe'))) { $grund = 'FreshGuest.exe fehlt' }
        elseif (-not (Get-ScheduledTask -TaskName 'FreshGuest-Anmeldung' -ErrorAction SilentlyContinue)) { $grund = 'Aufgabe FreshGuest-Anmeldung fehlt' }
        elseif ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedPC' -ErrorAction SilentlyContinue).SharedPCMode -ne 1) { $grund = 'Shared PC ist aus' }
        if ($grund) {
            Remove-ItemProperty -Path $sysKey -Name ExcludedCredentialProviders -ErrorAction SilentlyContinue
            L ('Anmeldebildschirm: Sperre "Nur Guest" entfernt (' + $grund + ')')
        }
    } catch { L ('Anmeldebildschirm: Sperre pruefen FEHLER: ' + $_.Exception.Message) }
}

if ($AnmeldungZuruecksetzen) { Test-AnmeldeSperre -Entfernen; Repair-GastEinstellung -Entfernen; Set-Anmeldebildschirm -Zuruecksetzen; return }
if (-not $NurAnzeigen) { Test-AnmeldeSperre; Stop-AutoAnmeldung; Remove-GastSchutz; Repair-GastEinstellung; Set-Anmeldebildschirm }
if ($NurAnmeldung) { return }

try {
    if ((Test-Path $log) -and ((Get-Item $log).Length -gt 1MB)) { Remove-Item $log -Force }
    $lokaleKonten = @(Get-LocalUser)
    $praefix = $lokaleKonten[0].SID.AccountDomainSid.Value
    $lokaleSids = @($lokaleKonten | ForEach-Object { $_.SID.Value })
    $exKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedPC\Exemptions'
    $ausgenommen = @()
    if (Test-Path $exKey) { $ausgenommen = @(Get-ChildItem $exKey | ForEach-Object { $_.PSChildName }) }

    $kandidaten = Get-CimInstance Win32_UserProfile | Where-Object {
        -not $_.Special -and -not $_.Loaded -and
        $_.SID -like "$praefix-*" -and
        $ausgenommen -notcontains $_.SID -and
        $lokaleSids -notcontains $_.SID
    }
    if (-not $kandidaten) { L 'Bereinigung: nichts zu tun.'; return }
    foreach ($p in $kandidaten) {
        if ($NurAnzeigen) { L "Wuerde loeschen: $($p.LocalPath) ($($p.SID))"; continue }
        L "Loesche verwaistes Profil: $($p.LocalPath) ($($p.SID))"
        try { Remove-CimInstance -InputObject $p -ErrorAction Stop; L '  -> geloescht' }
        catch { L ('  -> FEHLER: ' + $_.Exception.Message) }
    }
} catch {
    L ('Bereinigung FEHLER: ' + $_.Exception.Message)
}
'@

# ---------------------------------------------------------------------------
# Modi
# ---------------------------------------------------------------------------

function Show-Status {
    $ed = Get-EditionInfo
    Write-Host ("`n=== " + (T 'Gaeste-PC Status' 'Stato PC ospiti' 'Guest PC status') + ' ===') -ForegroundColor Cyan
    Write-Host ("Windows:       {0} (Edition {1}, Build {2})" -f $ed.Name, $ed.EditionID, $ed.Build)
    if ($ed.IsHome) {
        Write-Host ('               -> ' + (T 'Home-Edition: Shared PC Mode wird NICHT unterstuetzt.' 'Edizione Home: la modalita'' PC condiviso NON e'' supportata.' 'Home edition: Shared PC Mode is NOT supported.')) -ForegroundColor Red
    }

    $node = Join-Path $SharedPCKey 'NodeValues'
    if (Test-Path $node) {
        Write-Host 'SharedPC-Registry (NodeValues):'
        $vals = Get-ItemProperty $node
        $vals.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } |
            ForEach-Object { Write-Host ("               {0} = {1}" -f $_.Name, $_.Value) }
    } else {
        Write-Host (T 'Shared PC Mode: noch nie eingerichtet (keine Registry-Werte).' 'Modalita'' PC condiviso: mai configurata (nessun valore nel registro).' 'Shared PC Mode: never set up (no registry values).')
    }

    if (Test-Admin) {
        Write-Host (T 'Aktuelle Einstellung (live, als SYSTEM gelesen):' 'Impostazione attuale (letta come SYSTEM):' 'Current setting (live, read as SYSTEM):')
        Invoke-AlsSystem -InnerScript ($InnerTemplate.Replace('__AKTION__', $AktionLesen)) |
            ForEach-Object { Write-Host $_ }
    } else {
        Write-Host (T '(Fuer die Live-Abfrage als Administrator starten.)' '(Per la verifica completa avviare come amministratore.)' '(Run as administrator for the live check.)') -ForegroundColor DarkGray
    }

    if (Test-Path $ExemptionsKey) {
        $ex = @(Get-ChildItem $ExemptionsKey | ForEach-Object { $_.PSChildName })
        Write-Host ((T 'Geschuetzte Konten: {0}' 'Account protetti: {0}' 'Protected accounts: {0}') -f $ex.Count)
        foreach ($sid in $ex) {
            $name = try { (New-Object Security.Principal.SecurityIdentifier $sid).Translate([Security.Principal.NTAccount]).Value } catch { (T '(unbekannt)' '(sconosciuto)' '(unknown)') }
            Write-Host "               $name  $sid"
        }
    }

    $t = Get-ScheduledTask -TaskName $CleanupTask -ErrorAction SilentlyContinue
    $tText = if ($t) { T 'eingerichtet' 'attiva' 'set up' } else { T 'nicht eingerichtet' 'non attiva' 'not set up' }
    Write-Host ((T 'Bereinigung beim Start: {0}' 'Pulizia all''avvio: {0}' 'Cleanup at startup: {0}') -f $tText)

    # Diagnose: geplante Aufgaben und Konten
    foreach ($tn in $CleanupTask, 'FreshGuest-Profilbild', 'FreshGuest-Neustart') {
        $st = Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
        if ($st) {
            $ti = $st | Get-ScheduledTaskInfo
            Write-Host ('Task {0}: {1}, last run {2}, result 0x{3:X}, triggers {4}' -f $tn, $st.State, $ti.LastRunTime, $ti.LastTaskResult,
                (($st.Triggers | ForEach-Object { $_.CimClass.CimClassName -replace 'MSFT_Task', '' }) -join '+'))
        } else { Write-Host ('Task {0}: -' -f $tn) }
    }
    $gastNamen = @(Get-ChildItem (Join-Path $SharedPCKey 'CreatedLocalAccounts') -ErrorAction SilentlyContinue | ForEach-Object { $_.PSChildName })
    Write-Host ('SharedPC CreatedLocalAccounts: ' + ($gastNamen -join ', '))
    Get-LocalUser | ForEach-Object { Write-Host ('User {0}  {1}  enabled={2}  lastlogon={3}' -f $_.Name, $_.SID.Value, $_.Enabled, $_.LastLogon) }
    Get-CimInstance Win32_UserProfile | Where-Object { -not $_.Special } | ForEach-Object { Write-Host ('Profile {0}  {1}  loaded={2}' -f $_.SID, $_.LocalPath, $_.Loaded) }
    $lu = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI' -ErrorAction SilentlyContinue
    Write-Host ('LogonUI: LastLoggedOnUser={0} Provider={1} SelectedUserSID={2}' -f $lu.LastLoggedOnUser, $lu.LastLoggedOnProvider, $lu.SelectedUserSID)

    $setupLog = Join-Path $env:windir 'SharedPCSetup.log'
    if (Test-Path $setupLog) {
        Write-Host ("`n" + (T 'Letzte Zeilen aus' 'Ultime righe di' 'Last lines of') + " $setupLog :") -ForegroundColor DarkGray
        try { Get-Content $setupLog -Tail 8 | ForEach-Object { Write-Host "   $_" -ForegroundColor DarkGray } } catch { }
    }
}

# Bereinigung beim Systemstart (geplante Aufgabe) schreiben/registrieren und den Anmeldebildschirm
# sofort einstellen. Beim Einrichten und nach einem Programm-Update (Modus Aktualisieren).
function Install-Bereinigung {
    Remove-Item $CleanupScript -Force -ErrorAction SilentlyContinue
    Set-Content -Path $CleanupScript -Value $CleanupContent -Encoding UTF8
    $action    = New-ScheduledTaskAction -Execute 'powershell.exe' `
                   -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$CleanupScript`""
    # Beim Start und 10 Sekunden nach jeder Anmeldung: Windows schreibt beim Anmelden das Konto als
    # "zuletzt angemeldet"; wir setzen sofort wieder die Vorauswahl "Gast".
    $tStart = New-ScheduledTaskTrigger -AtStartup
    $tLogon = New-ScheduledTaskTrigger -AtLogOn
    $tLogon.Delay = 'PT10S'
    $trigger = @($tStart, $tLogon)
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                   -ExecutionTimeLimit (New-TimeSpan -Minutes 15)
    Register-ScheduledTask -TaskName $CleanupTask -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings -Force | Out-Null
    Write-Log ((T 'Bereinigung beim Systemstart eingerichtet (Aufgabe ''{0}'').' 'Pulizia all''avvio configurata (attivita'' ''{0}'').' 'Cleanup at startup set up (task ''{0}'').') -f $CleanupTask) 'Green'

    # 5) Anmeldebildschirm: Kacheln "Gast" + Admin statt leerem Formular "Anderer Benutzer"
    #    (wird zusaetzlich bei jedem Systemstart von der Bereinigung erneut gesetzt)
    try {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $CleanupScript -NurAnmeldung | Out-Null
        Write-Log (T 'Anmeldebildschirm eingestellt (Details in Bereinigung.log).' 'Schermata di accesso impostata (dettagli in Bereinigung.log).' 'Sign-in screen set (details in Bereinigung.log).') 'Green'
    } catch {
        Write-Log ((T 'Hinweis: Anmeldebildschirm nicht geaendert ({0}).' 'Nota: schermata di accesso non modificata ({0}).' 'Note: sign-in screen not changed ({0}).') -f $_.Exception.Message) 'Yellow'
    }
}

function Invoke-Einrichten {
    $ed = Get-EditionInfo
    if ($ed.IsHome) {
        Write-Log ((T 'Abbruch: {0} ist eine Home-Edition. Shared PC Mode braucht Pro, Enterprise oder Education.' `
                      'Interrotto: {0} e'' un''edizione Home. La modalita'' PC condiviso richiede Pro, Enterprise o Education.' `
                      'Aborted: {0} is a Home edition. Shared PC Mode needs Pro, Enterprise or Education.') -f $ed.Name) 'Red'
        $script:Erfolg = $false
        return
    }

    Write-Host ("`n" + (T 'Das wird jetzt eingerichtet:' 'Ora viene configurato:' 'This will now be set up:')) -ForegroundColor Cyan
    Write-Host (T ' - Button "Gast" auf dem Anmeldebildschirm'                  ' - Pulsante "Ospite" nella schermata di accesso'            ' - "Guest" button on the sign-in screen')
    Write-Host (T ' - Gastkonto wird beim Abmelden/Neustart komplett geloescht' ' - L''account ospite viene cancellato alla disconnessione/al riavvio' ' - Guest account is deleted completely at sign-out/restart')
    Write-Host (T ' - alle jetzt vorhandenen Konten (auch deins) werden geschuetzt' ' - tutti gli account esistenti (anche il tuo) vengono protetti' ' - all existing accounts (including yours) are protected')
    Write-Host (T ' - Bereinigung beim Systemstart fuer uebrig gebliebene Gast-Profile' ' - pulizia all''avvio per i profili ospite rimasti' ' - cleanup at startup for left-over guest profiles')
    Write-Host (T ' - vorher wird ein Wiederherstellungspunkt versucht'          ' - prima si prova a creare un punto di ripristino'          ' - a restore point is attempted first')
    if (-not $OhneRueckfrage) {
        $a = Read-Host ("`n" + (T 'Fortfahren? (j/n)' 'Continuare? (s/n)' 'Continue? (y/n)'))
        if (-not (Test-Ja $a)) { Write-Log (T 'Abgebrochen durch Benutzer.' 'Annullato dall''utente.' 'Cancelled by user.') 'Yellow'; $script:Erfolg = $false; return }
    }

    # Wiederherstellungspunkt (nicht kritisch, wenn es nicht klappt).
    # Windows legt hoechstens einen pro 24 Stunden an und meldet das nur als WARNUNG,
    # nicht als Fehler -> Warnung abfangen, sonst wuerde faelschlich "erstellt" gemeldet.
    try {
        $warnung = $null
        Checkpoint-Computer -Description 'Vor GaestePC Shared PC Mode' -RestorePointType MODIFY_SETTINGS `
            -ErrorAction Stop -WarningAction SilentlyContinue -WarningVariable warnung
        if ($warnung) {
            Write-Log (T 'Hinweis: Kein neuer Wiederherstellungspunkt - Windows erlaubt nur einen pro 24 Stunden und es gibt schon einen. Weiter.' `
                         'Nota: nessun nuovo punto di ripristino - Windows ne consente solo uno ogni 24 ore e ne esiste gia'' uno. Si continua.' `
                         'Note: no new restore point - Windows allows only one per 24 hours and one already exists. Continuing.') 'Yellow'
        } else {
            Write-Log (T 'Wiederherstellungspunkt erstellt.' 'Punto di ripristino creato.' 'Restore point created.') 'Green'
        }
    } catch {
        Write-Log ((T 'Hinweis: Kein Wiederherstellungspunkt ({0}). Weiter ohne.' 'Nota: nessun punto di ripristino ({0}). Si continua senza.' 'Note: no restore point ({0}). Continuing without.') -f $_.Exception.Message) 'Yellow'
    }

    # 1) Alle vorhandenen Konten schuetzen
    $meSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $sids = New-Object System.Collections.Generic.HashSet[string]
    [void]$sids.Add($meSid)
    try {
        Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction Stop |
            Where-Object { $_.SID.Value -match '^S-1-(5-21|12-1)-' } |
            ForEach-Object { [void]$sids.Add($_.SID.Value) }
    } catch {
        Write-Log ((T 'Hinweis: Administratoren-Gruppe nicht lesbar ({0}).' 'Nota: gruppo Administrators non leggibile ({0}).' 'Note: Administrators group not readable ({0}).') -f $_.Exception.Message) 'Yellow'
    }
    # Gerade angemeldete fremde Konten (z. B. ein Gast, der noch angemeldet ist) NICHT schuetzen
    $profilListe = @(Get-CimInstance Win32_UserProfile | Where-Object { -not $_.Special -and $_.SID -match '^S-1-(5-21|12-1)-' })
    $fremdAngemeldet = @($profilListe | Where-Object { $_.Loaded -and $_.SID -ne $meSid } | ForEach-Object { $_.SID })
    $profilListe | Where-Object { $fremdAngemeldet -notcontains $_.SID } | ForEach-Object { [void]$sids.Add($_.SID) }
    Get-LocalUser | Where-Object { $fremdAngemeldet -notcontains $_.SID.Value } | ForEach-Object { [void]$sids.Add($_.SID.Value) }
    # Gast-Konten, die Shared PC angelegt hat (shpc...), nie schuetzen - auch wenn sie gerade nicht angemeldet sind
    $gastNamen = @(Get-ChildItem (Join-Path $SharedPCKey 'CreatedLocalAccounts') -ErrorAction SilentlyContinue | ForEach-Object { $_.PSChildName })
    foreach ($u in @(Get-LocalUser | Where-Object { $gastNamen -contains $_.Name })) {
        [void]$sids.Remove($u.SID.Value)
        $alt = Join-Path $ExemptionsKey $u.SID.Value
        if (Test-Path $alt) { Remove-Item $alt -Recurse -Force -ErrorAction SilentlyContinue }
        Write-Log ((T 'Gast-Konto von Shared PC wird nicht geschuetzt: {0}' 'Account ospite di PC condiviso non protetto: {0}' 'Shared PC guest account is not protected: {0}') -f $u.Name) 'Yellow'
    }
    foreach ($f in $fremdAngemeldet) {
        Write-Log ((T 'Hinweis: Konto {0} ist gerade angemeldet und wird nicht geschuetzt.' 'Nota: l''account {0} e'' connesso ora e non viene protetto.' 'Note: account {0} is signed in right now and is not protected.') -f $f) 'Yellow'
    }

    if (-not (Test-Path $ExemptionsKey)) { New-Item -Path $ExemptionsKey -Force | Out-Null }
    foreach ($sid in $sids) {
        New-Item -Path (Join-Path $ExemptionsKey $sid) -Force | Out-Null
        $name = try { (New-Object Security.Principal.SecurityIdentifier $sid).Translate([Security.Principal.NTAccount]).Value } catch { (T '(unbekannt)' '(sconosciuto)' '(unknown)') }
        Write-Log ((T 'Geschuetzt: {0} ({1})' 'Protetto: {0} ({1})' 'Protected: {0} ({1})') -f $name, $sid) 'Green'
    }

    # 2) Shared PC Mode als SYSTEM einschalten
    Write-Log (T 'Schalte Shared PC Mode ein (kann 1-2 Minuten dauern) ...' 'Attivo la modalita'' PC condiviso (puo'' durare 1-2 minuti) ...' 'Turning on Shared PC Mode (may take 1-2 minutes) ...') 'Cyan'
    $res = Invoke-AlsSystem -InnerScript ($InnerTemplate.Replace('__AKTION__', $AktionEinrichten))
    $res | ForEach-Object { Write-Log $_ }
    if (-not ($res -contains 'OK')) {
        Write-Log (T 'Shared PC Mode konnte NICHT eingeschaltet werden. Details siehe oben und C:\Windows\SharedPCSetup.log' `
                     'NON e'' stato possibile attivare la modalita'' PC condiviso. Dettagli sopra e in C:\Windows\SharedPCSetup.log' `
                     'Shared PC Mode could NOT be turned on. See details above and C:\Windows\SharedPCSetup.log') 'Red'
        $script:Erfolg = $false
        return
    }

    # 3) Schnellstart aus: "Herunterfahren" soll ein echter Neustart sein,
    #    damit Gast-Sitzungen wirklich beendet und geloescht werden.
    try {
        Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' `
            -Name 'HiberbootEnabled' -Value 0 -Type DWord -ErrorAction Stop
        Write-Log (T 'Schnellstart ausgeschaltet (Herunterfahren = echter Neustart).' 'Avvio rapido disattivato (spegnere = vero riavvio).' 'Fast startup turned off (shut down = real restart).') 'Green'
    } catch {
        Write-Log ((T 'Hinweis: Schnellstart nicht geaendert ({0}).' 'Nota: avvio rapido non modificato ({0}).' 'Note: fast startup not changed ({0}).') -f $_.Exception.Message) 'Yellow'
    }

    # 4) Bereinigung beim Systemstart + 5) Anmeldebildschirm
    Install-Bereinigung

    Write-Log (T 'FERTIG. Bitte den PC jetzt neu starten.' 'FATTO. Ora riavviare il PC.' 'DONE. Please restart the PC now.') 'Green'
    Write-Host (T 'Danach auf dem Anmeldebildschirm unten links "Gast" testen.' 'Poi provare "Ospite" in basso a sinistra nella schermata di accesso.' 'Then try "Guest" at the bottom left of the sign-in screen.') -ForegroundColor Green
    if (-not $OhneRueckfrage) {
        $a = Read-Host (T 'Jetzt neu starten? (j/n)' 'Riavviare ora? (s/n)' 'Restart now? (y/n)')
        if (Test-Ja $a) { Restart-Computer -Force }
    }
}

# Nach einem Programm-Update: die Skripte auf dem PC auf den neuen Stand bringen,
# ohne Shared PC Mode anzufassen. Nur wenn der Schutz eingerichtet ist (Aufgabe vorhanden).
function Invoke-Aktualisieren {
    if (-not (Get-ScheduledTask -TaskName $CleanupTask -ErrorAction SilentlyContinue)) {
        Write-Log (T 'Aktualisieren: Schutz ist nicht eingerichtet, nichts zu tun.' 'Aggiornamento: la protezione non e'' configurata, niente da fare.' 'Update: protection is not set up, nothing to do.')
        return
    }
    Install-Bereinigung
}

function Invoke-Rueckgaengig {
    if (-not $OhneRueckfrage) {
        $a = Read-Host (T 'Shared PC Mode und Bereinigung wirklich ausschalten? (j/n)' 'Disattivare davvero la modalita'' PC condiviso e la pulizia? (s/n)' 'Really turn off Shared PC Mode and cleanup? (y/n)')
        if (-not (Test-Ja $a)) { Write-Log (T 'Abgebrochen durch Benutzer.' 'Annullato dall''utente.' 'Cancelled by user.') 'Yellow'; $script:Erfolg = $false; return }
    }
    $res = Invoke-AlsSystem -InnerScript ($InnerTemplate.Replace('__AKTION__', $AktionRueckgaengig))
    $res | ForEach-Object { Write-Log $_ }
    if (-not ($res -contains 'OK')) {
        Write-Log (T 'Shared PC Mode konnte NICHT ausgeschaltet werden. Details siehe oben.' 'NON e'' stato possibile disattivare la modalita'' PC condiviso. Dettagli sopra.' 'Shared PC Mode could NOT be turned off. See details above.') 'Red'
        $script:Erfolg = $false
        return
    }

    # Anmeldebildschirm wieder auf Windows-Standard (Kacheln sichtbar, ausgeblendete Konten zurueck)
    if (Test-Path $CleanupScript) {
        try { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $CleanupScript -AnmeldungZuruecksetzen | Out-Null } catch { }
    }

    if (Get-ScheduledTask -TaskName $CleanupTask -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $CleanupTask -Confirm:$false
        Write-Log (T 'Bereinigungs-Aufgabe entfernt.' 'Attivita'' di pulizia rimossa.' 'Cleanup task removed.') 'Green'
    }
    Remove-Item $CleanupScript -ErrorAction SilentlyContinue
    Write-Log (T 'Ausgeschaltet. Bitte neu starten. (Geschuetzte-Konten-Liste bleibt bestehen, sie schadet nicht.)' `
                 'Disattivato. Riavviare. (L''elenco degli account protetti resta, non da'' fastidio.)' `
                 'Turned off. Please restart. (The protected-accounts list stays, it does no harm.)') 'Green'
}

# ---------------------------------------------------------------------------
# Start
# ---------------------------------------------------------------------------

if ($Modus -ne 'Status' -and -not (Test-Admin)) {
    # Mit Administratorrechten neu starten
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Modus', $Modus, '-Sprache', $Sprache)
    if ($OhneRueckfrage) { $argList += '-OhneRueckfrage' }
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList
    return
}

$script:Erfolg = $true
try {
    if (Test-Admin) { Protect-BaseDir }
    switch ($Modus) {
        'Status'       { Show-Status }
        'Einrichten'   { Invoke-Einrichten }
        'Rueckgaengig' { Invoke-Rueckgaengig }
        'Aktualisieren' { Invoke-Aktualisieren }
    }
} catch {
    Write-Log ((T 'FEHLER: ' 'ERRORE: ' 'ERROR: ') + $_.Exception.Message) 'Red'
    $script:Erfolg = $false
}
Wait-Ende
# Rueckmeldung fuer das Programm GaesteSchutz.exe: 0 = OK, 1 = Fehler
if ($script:Erfolg) { exit 0 } else { exit 1 }
