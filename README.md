<p align="center">
  <img src="logo/FreshGuest-Schriftzug-hell.png" alt="FreshGuest" width="420">
</p>

<p align="center"><b>A fresh, empty Windows account for every guest – for holiday flats, B&amp;Bs and guest PCs.</b></p>

<p align="center"><b>⚠️ Requires Windows 11 Pro.</b> Windows Home does not work.</p>

> 🇩🇪 **Deutsch:** FreshGuest richtet einen Windows-PC für Feriengäste ein. Gäste klicken auf dem Anmeldebildschirm nur auf „Anmelden“; beim Abmelden oder Neustart löscht Windows alles, was der Gast hinterlassen hat. **Nur für Windows 11 Pro** – mit Windows Home funktioniert es nicht. Die ausführliche Anleitung (DE/IT/EN) steckt im Programm und liegt jedem [Release](../../releases) bei.
>
> 🇮🇹 **Italiano:** FreshGuest prepara un PC Windows per gli ospiti. Gli ospiti cliccano solo su «Accedi» nella schermata di accesso; alla disconnessione o al riavvio Windows cancella tutto ciò che hanno lasciato. **Solo per Windows 11 Pro** – con Windows Home non funziona. La guida completa (DE/IT/EN) è inclusa nel programma e in ogni [release](../../releases).

---

## What it does

When the PC starts, the **“Guest”** tile is already selected – guests just click **Sign in**, no password needed.
When they sign out or the PC restarts, Windows deletes the guest account completely: browser history, logins
(Netflix, Gmail …), downloads and documents. The next guest gets a fresh, empty account.
Your own admin account and your data stay untouched.

FreshGuest doesn't freeze the whole disk. It uses Windows' built-in, free **Shared PC mode** with a guest account
and gives it a simple, password-protected interface:

| | |
|---|---|
| ![Protection on](bilder/2-schutz-an-en.png) | ![Welcome settings](bilder/10-einstellungen-willkommen-en.png) |

Every window the guest sees is shown in **German, Italian and English at the same time** (plus an optional fourth
language) – you never know who is sitting at the PC:

![Welcome window for guests](bilder/13-gast-willkommen-en.png)

## Guest settings

All settings apply to guests only – your own account stays as it is.

- **Browser** – Edge, Chrome and Firefox without first-run wizards, password saving or “make default” prompts; fixed start page
- **Wallpaper** and **profile picture** for guests – the picture also replaces the grey question-mark figure on the “Guest” tile of the sign-in screen
- **Desktop** – files, programs and links every new guest finds on the desktop
- **Autostart** – open a page or program when a guest signs in
- **Welcome window** – Wi-Fi name and password; check-out, contact, an extra field of your choice and your own text, each per language
- **Fourth language** for all guest windows: French, Spanish, Dutch, Polish or Czech
- **Automatic** – shut the PC down when a guest leaves it unused (after a 60-second “Still there?”), restart daily at a fixed time, show a notice that the PC is reset after every guest
- **Locks & printer** – lock Control Panel / Settings, command prompt, registry editor, installing programs and running programs from Downloads; default printer for guests
- **Sign-in** – “Guest” is selected automatically; optionally show **only** “Guest” and reach your own account with **Ctrl + Alt + F** on the sign-in screen

| | |
|---|---|
| ![Sign-in settings](bilder/16-einstellungen-anmeldung-en.png) | ![Automatic](bilder/11-einstellungen-abmelden-en.png) |
| ![Notice for guests](bilder/17-gast-hinweis-en.png) | ![Still there?](bilder/14-gast-noch-da-en.png) |

Switching protection on/off, changing settings and uninstalling all ask for the FreshGuest password.
Uninstall removes everything again.

The interface and the guide are available in **German, Italian and English** (default: Windows language).

## Requirements

- **Windows 11 Pro.** This is the edition FreshGuest is built and tested on.
  Windows 11 **Home does not work** – it has no Shared PC mode. (Enterprise and Education have Shared PC mode too, but are untested.)
- An administrator account **with a password** (a PIN alone is not enough)
- .NET Framework 4.8 (included in Windows 11)
- No other “reset on restart” program (e.g. disk-freeze tools) – uninstall it first

## Usage

1. Download `FreshGuest.exe` from the [latest release](../../releases/latest) and copy it to a USB stick.
2. On the guest PC, sign in with your admin account, start `FreshGuest.exe` (confirm the admin prompt) → set a FreshGuest password → **Install**.
3. Click the big **⚙ Guest settings** button and set everything up. Only then click **Restart now**. Protection is active after the restart.
4. Test it: sign in as guest, create a folder, restart – the folder is gone.

Try it without changing anything: `FreshGuest.exe --demo`.

Forgot the FreshGuest password? In an administrator command prompt:

```bat
"C:\Program Files\FreshGuest\FreshGuest.exe" --kennwort-zuruecksetzen
```

To update, simply start a newer `FreshGuest.exe` – password, settings and protection are kept.

`werkzeuge\FreshGuest-Zuruecksetzen.cmd` removes everything any FreshGuest version ever changed (for leftovers of old versions).

## Building from source

No Visual Studio needed – the build uses the C# compiler that ships with Windows (.NET Framework 4.8).

```bat
src\build.cmd
```

This produces `FreshGuest.exe` in the project folder. The PowerShell script `GaestePC.ps1`, the logo and the guide
are embedded as resources.

Full rebuild including logo, screenshots and guide:

1. `powershell -ExecutionPolicy Bypass -File logo\render.ps1` – renders the SVG logo to PNG/ICO (needs Microsoft Edge; the generated files are already in the repo)
2. `src\build.cmd`
3. `FreshGuest.exe --snapshot <folder>` – creates the screenshots in demo mode; copy them to `bilder\`
4. `powershell -ExecutionPolicy Bypass -File anleitung\bauen.ps1` – builds `FreshGuest-Anleitung.html` (template `anleitung\vorlage.html`, images embedded)
5. `src\build.cmd` again – now with the guide embedded

## Project layout

| Path | Contents |
|---|---|
| `src\` | C# WinForms source, manifest, `build.cmd` |
| `src\Anmeldung.cs` | Sign-in screen: selects the “Guest” tile, “Guest only” mode with Ctrl + Alt + F |
| `src\Kachelbild.cs` | Guest profile picture on the “Guest” tile of the sign-in screen |
| `GaestePC.ps1` | PowerShell script that does the system work (embedded in the exe) |
| `1-…`, `2-…`, `3-….cmd` | Run the script directly without the UI (status / set up / undo) |
| `werkzeuge\` | Reset tool for leftovers of old versions |
| `anleitung\` | Guide template and build script |
| `bilder\` | Screenshots (no suffix = German, `-it`, `-en`) |
| `logo\` | Logo as SVG, PNG and ICO, plus `render.ps1` |
| `ANLEITUNG.md` | Technical notes (German): what FreshGuest changes in Windows, logs, version history |

## Disclaimer

FreshGuest changes system settings (Shared PC mode, policies, scheduled tasks, the default user profile).
It creates a restore point where Windows allows one, and uninstalling reverts its changes – but use it at your own
risk and test it before handing the PC to guests.

## License

© 2026 PowerStore Computers

FreshGuest is free to use, copy and modify – also on PCs in a holiday flat, hotel or other business – under the
[MIT License with the Commons Clause](LICENSE). **You may not sell it:** offering FreshGuest itself, or a product
or service whose value comes mainly from FreshGuest, for a fee is not permitted.

> 🇩🇪 Kostenlos nutzbar, auch gewerblich (z. B. in der Ferienwohnung) – aber **kein Verkauf** des Programms.
> 🇮🇹 Uso gratuito, anche in attività commerciali (ad es. casa vacanze) – ma **vietata la vendita** del programma.
