// FreshGuest – PC für Feriengäste  (Deutsch / Italiano / English)
// © 2026 PowerStore Computers – freie Software unter der GNU GPL v3.0 oder neuer (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later
// (früher "Gäste-Schutz"; interne Speicherorte heißen weiter GaesteSchutz/GaestePC)
// Grafische Oberfläche für den Windows "Shared PC Mode" (Gastkonto wird beim
// Abmelden/Neustart gelöscht). Die eigentliche Systemarbeit macht GaestePC.ps1,
// das als Ressource in die .exe eingebettet ist.
//
// Bauen: build.cmd   (nutzt den C#-Compiler, der bei Windows dabei ist)
// Start-Optionen:
//   --demo                     Oberfläche testen, am PC wird NICHTS verändert
//   --snapshot <ordner>        Demo-Bilder der Fenster als PNG speichern
//   --kennwort-zuruecksetzen   Kennwort neu setzen (braucht Administrator-Rechte)
//   --protokolle <ordner>      Protokolle ohne Fenster speichern

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("FreshGuest")]
[assembly: AssemblyProduct("FreshGuest")]
[assembly: AssemblyDescription("PC für Feriengäste: Gast-Daten werden beim Neustart gelöscht")]
[assembly: AssemblyCompany("PowerStore Computers")]
[assembly: AssemblyCopyright("© 2026 PowerStore Computers")]
[assembly: AssemblyVersion("1.27.0.0")]
[assembly: AssemblyFileVersion("1.27.0.0")]

namespace GaesteSchutz
{
    // ------------------------------------------------------------------
    // Sprache: Deutsch / Italiano / English
    // ------------------------------------------------------------------
    static class L
    {
        public static string Code = "de";   // de | it | en
        public static readonly string[] Codes = { "de", "it", "en" };
        public static readonly string[] Names = { "Deutsch", "Italiano", "English" };

        public static string T(string de, string it, string en)
        {
            return Code == "it" ? it : Code == "en" ? en : de;
        }

        public static void Init(string saved)
        {
            if (Codes.Contains(saved)) { Code = saved; return; }
            string sys = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            Code = sys == "de" ? "de" : sys == "it" ? "it" : "en";
        }

        public static string AppName { get { return Program.Name; } }
    }

    static class Program
    {
        public const string Name = "FreshGuest";
        public const string Version = "1.27";
        public const string Copyright = "© 2026 PowerStore Computers";
        public static bool Demo;
        public static bool CleanShots;   // Bilder für die Anleitung: ohne DEMO-Hinweise
        public static bool ShowDemoHints { get { return Demo && !CleanShots; } }

        [STAThread]
        static int Main(string[] args)
        {
            // Beim Anmelden jedes Benutzers (Autostart-Ordner): startet das eingestellte Programm nur bei Gästen
            if (args.Contains("--gast-start")) { GuestExtras.RunGuestStart(); return 0; }
            // Anmeldebildschirm: Wache (SYSTEM, beim Start) und Helfer (auf dem Anmelde-Desktop)
            if (args.Contains("--anmeldewache")) return SignIn.RunWatch();
            if (args.Contains("--anmeldehelfer")) return SignIn.RunHelper(args);

            Demo = args.Contains("--demo");
            string snapshotDir = null;
            int si = Array.IndexOf(args, "--snapshot");
            if (si >= 0 && si + 1 < args.Length) { snapshotDir = args[si + 1]; Demo = true; }
            bool resetPw = args.Contains("--kennwort-zuruecksetzen");
            L.Init(null);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (!Demo && !Sys.IsAdmin())
            {
                // Mit Administrator-Rechten neu starten (Gäste haben keine -> kommen nicht weiter)
                try
                {
                    var psi = new ProcessStartInfo(Application.ExecutablePath,
                        string.Join(" ", args.Select(a => "\"" + a + "\"")));
                    psi.Verb = "runas";
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                }
                catch (Win32Exception)
                {
                    MessageBox.Show(L.T("FreshGuest braucht Administrator-Rechte.",
                                        "FreshGuest richiede i diritti di amministratore.",
                                        "FreshGuest needs administrator rights."),
                        L.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return 0;
            }

            if (snapshotDir != null) { Snapshots.Run(snapshotDir); return 0; }

            Store store = Store.Open();
            L.Init(store.Get("Sprache"));
            GuestExtras.MigratePicture(store);     // alte Version: Profilbild galt auch für dein Konto

            // Protokolle ohne Fenster speichern:  GaesteSchutz.exe --protokolle E:\
            int pi = Array.IndexOf(args, "--protokolle");
            if (pi >= 0 && pi + 1 < args.Length)
            {
                try
                {
                    string dir = Sys.ExportLogs(store, args[pi + 1], "(ohne Fenster gestartet)").Result;
                    MessageBox.Show(L.T("Protokolle gespeichert in:", "Log salvati in:", "Logs saved to:") + "\n\n" + dir, L.AppName);
                    return 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(L.T("Protokolle konnten nicht gespeichert werden:", "Impossibile salvare i log:", "Logs could not be saved:") +
                        "\n\n" + ex.GetBaseException().Message, L.AppName);
                    return 1;
                }
            }

            // Update: andere exe (z. B. vom USB-Stick) gestartet -> installierte Kopie ersetzen und
            // die Skripte auf dem PC (Bereinigung, Anmeldebildschirm, Profilbild) neu schreiben.
            // Nicht nur über die Versionsnummer: auch ein neuer Build mit gleicher Nummer wird erkannt.
            if (store.IsInstalled && !Demo && (store.Get("Version") != Version || Sys.InstalledExeDiffers()))
            {
                try
                {
                    if (Sys.UpdateInstalledExe())
                    {
                        store.Set("Version", Version);
                        store.Save();
                        Sys.RefreshScripts(store);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(L.T("Die installierte Version konnte nicht aktualisiert werden:",
                        "Impossibile aggiornare la versione installata:", "The installed version could not be updated:") +
                        "\n\n" + ex.Message, L.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            if (resetPw)
            {
                if (!store.IsInstalled)
                {
                    MessageBox.Show(L.T("FreshGuest ist noch nicht eingerichtet.", "FreshGuest non è ancora installato.",
                        "FreshGuest is not set up yet."), L.AppName);
                    return 1;
                }
                string neu = PasswordForm.AskNew(null, L.T("Neues Kennwort festlegen", "Imposta nuova password", "Set new password"));
                if (neu == null) return 1;
                Pwd.Set(store, neu);
                store.Save();
                MessageBox.Show(L.T("Das neue Kennwort ist gespeichert.", "La nuova password è stata salvata.", "The new password has been saved."),
                    L.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            if (!store.IsInstalled)
            {
                while (true)
                {
                    var setup = new SetupForm(store);
                    Application.Run(setup);
                    if (setup.SwitchLanguage) continue;          // Fenster in neuer Sprache neu öffnen
                    if (!setup.OpenMainAfter) return 0;
                    break;
                }
            }
            while (true)
            {
                var main = new MainForm(store);
                Application.Run(main);
                if (!main.SwitchLanguage) break;
            }
            return 0;
        }
    }

    // ------------------------------------------------------------------
    // Gespeicherter Zustand (Registry, nur für Administratoren lesbar).
    // Im Demo-Modus: Textdatei im Temp-Ordner.
    // ------------------------------------------------------------------
    class Store
    {
        const string RegPath = @"SOFTWARE\GaesteSchutz";
        readonly Dictionary<string, string> data = new Dictionary<string, string>();
        readonly string demoFile;

        Store(string demoFile) { this.demoFile = demoFile; Load(); }

        public static Store Open()
        {
            if (Program.Demo)
            {
                string dir = Path.Combine(Path.GetTempPath(), "GaesteSchutzDemo");
                Directory.CreateDirectory(dir);
                return new Store(Path.Combine(dir, "zustand.txt"));
            }
            return new Store(null);
        }

        public static Store OpenFile(string file) { return new Store(file); }

        void Load()
        {
            data.Clear();
            if (demoFile != null)
            {
                if (!File.Exists(demoFile)) return;
                foreach (string line in File.ReadAllLines(demoFile, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) data[line.Substring(0, i)] = line.Substring(i + 1).Replace("\u2028", "\r\n");
                }
                return;
            }
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(RegPath))
            {
                if (k == null) return;
                foreach (string n in k.GetValueNames()) data[n] = Convert.ToString(k.GetValue(n));
            }
        }

        public void Save()
        {
            if (demoFile != null)
            {
                File.WriteAllLines(demoFile, data.Select(kv => kv.Key + "=" + (kv.Value ?? "").Replace("\r\n", "\u2028").Replace("\n", "\u2028")).ToArray(), Encoding.UTF8);
                return;
            }
            var sec = new RegistrySecurity();
            sec.SetAccessRuleProtection(true, false);
            foreach (WellKnownSidType t in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            {
                sec.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(t, null),
                    RegistryRights.FullControl, InheritanceFlags.ContainerInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            }
            using (RegistryKey k = Registry.LocalMachine.CreateSubKey(RegPath, RegistryKeyPermissionCheck.ReadWriteSubTree, sec))
            {
                k.SetAccessControl(sec);
                foreach (var kv in data) k.SetValue(kv.Key, kv.Value ?? "", RegistryValueKind.String);
                // entfernte Einstellungen auch in der Registry löschen
                foreach (string n in k.GetValueNames())
                    if (!data.ContainsKey(n)) k.DeleteValue(n, false);
            }
        }

        public void Delete()
        {
            if (demoFile != null) { if (File.Exists(demoFile)) File.Delete(demoFile); }
            else Registry.LocalMachine.DeleteSubKeyTree(RegPath, false);
            data.Clear();
        }

        public string Get(string key) { string v; return data.TryGetValue(key, out v) ? v : null; }
        public void Set(string key, string value) { data[key] = value; }
        public void Remove(string key) { data.Remove(key); }
        public List<string> Keys { get { return data.Keys.ToList(); } }

        public bool IsInstalled { get { return Get("Installiert") == "1"; } }
        public bool Enabled { get { return Get("SchutzAn") == "1"; } }

        public void MarkChanged(bool enabled)
        {
            Set("SchutzAn", enabled ? "1" : "0");
            Set("AenderungBoot", Sys.BootTimeUtc().Ticks.ToString());
        }

        // Änderung wurde in dieser Windows-Sitzung gemacht -> wirkt erst nach Neustart
        public bool IsPending
        {
            get
            {
                long t;
                if (!long.TryParse(Get("AenderungBoot"), out t)) return false;
                return Math.Abs((Sys.BootTimeUtc() - new DateTime(t, DateTimeKind.Utc)).TotalMinutes) < 2;
            }
        }
    }

    // ------------------------------------------------------------------
    // Kennwort: nur ein gesalzener PBKDF2-Hash wird gespeichert.
    // ------------------------------------------------------------------
    static class Pwd
    {
        const int Iterations = 200000;

        public static void Set(Store s, string pw)
        {
            byte[] salt = new byte[16];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(salt);
            s.Set("KennwortSalz", Convert.ToBase64String(salt));
            s.Set("KennwortHash", Convert.ToBase64String(Hash(pw, salt, Iterations)));
            s.Set("KennwortRunden", Iterations.ToString());
        }

        public static bool Check(Store s, string pw)
        {
            try
            {
                byte[] salt = Convert.FromBase64String(s.Get("KennwortSalz"));
                byte[] soll = Convert.FromBase64String(s.Get("KennwortHash"));
                byte[] ist = Hash(pw, salt, int.Parse(s.Get("KennwortRunden")));
                if (ist.Length != soll.Length) return false;
                int diff = 0;
                for (int i = 0; i < ist.Length; i++) diff |= ist[i] ^ soll[i];
                return diff == 0;
            }
            catch { return false; }
        }

        static byte[] Hash(string pw, byte[] salt, int iterations)
        {
            using (var kdf = new Rfc2898DeriveBytes(pw, salt, iterations, HashAlgorithmName.SHA256))
                return kdf.GetBytes(32);
        }
    }

    // ------------------------------------------------------------------
    // Alles, was den PC betrifft
    // ------------------------------------------------------------------
    static class Sys
    {
        [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
        [DllImport("kernel32.dll")] static extern uint GetOEMCP();
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool LogonUser(string user, string domain, string password, int logonType, int provider, out IntPtr token);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        // Hat dein Windows-Konto KEIN Kennwort? Dann versucht Windows es nach jedem Start automatisch anzumelden
        // (am Gäste-PC: „Anderer Benutzer – Benutzername und Kennwort eingeben“) und jeder Gast kommt über
        // „Anderer Benutzer“ ohne Kennwort in dein Admin-Konto.
        // Prüfung: Anmeldeversuch mit leerem Kennwort. Gelingt er oder meldet Windows 1327 (leeres Kennwort nur an
        // der Konsole erlaubt), ist das Kennwort leer. 1326 = falsches Kennwort -> es gibt eins.
        public static bool BlankPassword()
        {
            try
            {
                IntPtr t;
                bool ok = LogonUser(Environment.UserName, ".", "", 3 /* Netzwerk */, 0, out t);
                int err = Marshal.GetLastWin32Error();
                if (ok) { CloseHandle(t); return true; }
                return err == 1327;
            }
            catch { return false; }
        }

        public static string DataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GaestePC"); }
        }
        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FreshGuest"); }
        }
        public static string InstalledExe { get { return Path.Combine(InstallDir, "FreshGuest.exe"); } }
        // Version 1.0/1.1 hieß "Gäste-Schutz"
        static string OldInstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GaesteSchutz"); }
        }

        // Verknüpfungsname bleibt fest (sonst bliebe beim Sprachwechsel eine alte liegen)
        static string ShortcutStart
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "FreshGuest.lnk"); }
        }
        static string ShortcutDesktop
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "FreshGuest.lnk"); }
        }
        static string[] OldShortcuts
        {
            get
            {
                return new[] {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Gäste-Schutz.lnk"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Gäste-Schutz.lnk") };
            }
        }

        // Reste der alten Version "Gäste-Schutz" entfernen (Programmordner + Verknüpfungen)
        static void RemoveOldVersion(Action<string> log)
        {
            bool found = false;
            foreach (string l in OldShortcuts)
                try { if (File.Exists(l)) { File.Delete(l); found = true; } } catch { }
            try { if (Directory.Exists(OldInstallDir)) { Directory.Delete(OldInstallDir, true); found = true; } } catch { }
            if (found && log != null)
                log(L.T("Alte Version „Gäste-Schutz“ ersetzt.", "Vecchia versione «Gäste-Schutz» sostituita.", "Old version \"Gäste-Schutz\" replaced."));
        }

        public static bool IsAdmin()
        {
            var p = new WindowsPrincipal(WindowsIdentity.GetCurrent());
            return p.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static DateTime BootTimeUtc()
        {
            DateTime t = DateTime.UtcNow.AddMilliseconds(-(double)GetTickCount64());
            return new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, DateTimeKind.Utc);
        }

        public static string EditionText(out bool supported)
        {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                string edition = Convert.ToString(k.GetValue("EditionID"));
                string name = Convert.ToString(k.GetValue("ProductName"));
                int build;
                int.TryParse(Convert.ToString(k.GetValue("CurrentBuildNumber")), out build);
                if (build >= 22000) name = name.Replace("Windows 10", "Windows 11");
                supported = !edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase);
                return name;
            }
        }

        // Datenordner absichern: Gäste dürfen dort nichts ablegen (Skripte laufen als SYSTEM)
        public static void SecureDataDir()
        {
            var ok = new[] {
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                WindowsIdentity.GetCurrent().User };
            if (Directory.Exists(DataDir))
            {
                var owner = Directory.GetAccessControl(DataDir).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                if (owner == null || !ok.Contains(owner)) Directory.Delete(DataDir, true);
            }
            Directory.CreateDirectory(DataDir);
            RunQuiet("icacls.exe", "\"" + DataDir + "\" /setowner *S-1-5-32-544 /T /C /Q");
            RunQuiet("icacls.exe", "\"" + DataDir + "\" /inheritance:r /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX /C /Q");
            if (Directory.EnumerateFileSystemEntries(DataDir).Any())
                RunQuiet("icacls.exe", "\"" + Path.Combine(DataDir, "*") + "\" /reset /T /C /Q");
        }

        // schtasks legt Aufgaben mit „nur im Netzbetrieb starten“ an – auf Laptops im Akkubetrieb liefen sie dann nie.
        // (Nicht „verpassten Start nachholen“: der tägliche Neustart soll nicht morgens nach dem Einschalten kommen.)
        public static void TaskRunOnBattery(string task)
        {
            if (Program.Demo) return;
            try
            {
                RunQuiet("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"$t = Get-ScheduledTask -TaskName '" + task + "' -ErrorAction Stop; " +
                    "$t.Settings.DisallowStartIfOnBatteries = $false; $t.Settings.StopIfGoingOnBatteries = $false; Set-ScheduledTask -InputObject $t | Out-Null\"");
            }
            catch { }
        }

        public static int RunQuiet(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true };
            using (Process p = Process.Start(psi)) { p.WaitForExit(); return p.ExitCode; }
        }

        // Führt GaestePC.ps1 aus. Rückgabe: 0 = OK
        public static Task<int> RunScript(string modus, Action<string> onLine)
        {
            if (Program.Demo) return DemoScript(modus, onLine);
            string sprache = L.Code;
            return Task.Run(() =>
            {
                SecureDataDir();
                string ps1 = Path.Combine(DataDir, "GaestePC.ps1");
                if (File.Exists(ps1)) File.Delete(ps1);
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("GaestePC.ps1"))
                using (FileStream f = File.Create(ps1)) s.CopyTo(f);

                var psi = new ProcessStartInfo("powershell.exe",
                    string.Format("-NoProfile -ExecutionPolicy Bypass -File \"{0}\" -Modus {1} -Sprache {2} -OhneRueckfrage",
                        ps1, modus, sprache));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                // PowerShell schreibt in der OEM-Codepage der Konsole (z. B. 850).
                // Ohne diese Angabe würden Windows-Meldungen mit Akzenten/Umlauten verstümmelt.
                Encoding oem = Encoding.GetEncoding((int)GetOEMCP());
                psi.StandardOutputEncoding = oem;
                psi.StandardErrorEncoding = oem;
                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    p.OutputDataReceived += (o, e) => { if (e.Data != null) onLine(e.Data); };
                    p.ErrorDataReceived += (o, e) => { if (!string.IsNullOrEmpty(e.Data)) onLine(L.T("FEHLER: ", "ERRORE: ", "ERROR: ") + e.Data); };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit();
                    p.WaitForExit();
                    return p.ExitCode;
                }
            });
        }

        static Task<int> DemoScript(string modus, Action<string> onLine)
        {
            return Task.Run(() =>
            {
                var lines = new List<string> { L.T("DEMO-MODUS: Am PC wird nichts verändert.",
                    "MODALITÀ DEMO: il PC non viene modificato.", "DEMO MODE: nothing is changed on the PC.") };
                if (modus == "Einrichten")
                    lines.AddRange(new[] { L.T("Wiederherstellungspunkt erstellt.", "Punto di ripristino creato.", "Restore point created."),
                        L.T("Geschützt: PC\\Admin", "Protetto: PC\\Admin", "Protected: PC\\Admin"),
                        "  EnableSharedPCMode = True", "OK",
                        L.T("FERTIG. Bitte den PC jetzt neu starten.", "FATTO. Ora riavviare il PC.", "DONE. Please restart the PC now.") });
                else if (modus == "Rueckgaengig")
                    lines.AddRange(new[] { "  EnableSharedPCMode = False", "OK",
                        L.T("Ausgeschaltet. Bitte neu starten.", "Disattivato. Riavviare.", "Turned off. Please restart.") });
                else
                    lines.AddRange(new[] { "=== Status ===", L.T("(Demo) alles in Ordnung", "(Demo) tutto a posto", "(Demo) all fine") });
                foreach (string l in lines) { Thread.Sleep(350); onLine(l); }
                return 0;
            });
        }

        // Genau ein eingesteckter USB-Stick -> dessen Laufwerk, sonst null
        public static string FindUsbDrive()
        {
            var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Removable && d.IsReady).ToList();
            return drives.Count == 1 ? drives[0].RootDirectory.FullName : null;
        }

        // Ziel für die Protokolle nach der Einrichtung: liegt die exe auf einem USB-Stick, dorthin
        // (Anleitung: „landen automatisch auf dem USB-Stick“), sonst in den Installationsordner statt z. B. auf den Desktop.
        public static string SetupLogRoot()
        {
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            try
            {
                if (new DriveInfo(Path.GetPathRoot(exeDir)).DriveType == DriveType.Removable) return exeDir;
            }
            catch { }
            return Program.Demo ? exeDir : InstallDir;
        }

        // Sammelt alle Protokolle in <ziel>\FreshGuest-Protokolle\<PC>_<Datum>\ (kein Kennwort, keine Geheimnisse)
        // Info.txt bleibt deutsch (Diagnose-Datei).
        public static async Task<string> ExportLogs(Store store, string targetRoot, string guiLog)
        {
            string dir = Path.Combine(targetRoot, "FreshGuest-Protokolle",
                Environment.MachineName + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(dir);
            var info = new StringBuilder();

            File.WriteAllText(Path.Combine(dir, "Protokoll-Fenster.txt"), guiLog ?? "", Encoding.UTF8);

            bool editionOk;
            info.AppendLine("FreshGuest Version:   " + Program.Version + (Program.Demo ? "  (DEMO)" : ""));
            info.AppendLine("Erstellt:             " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            info.AppendLine("PC-Name:              " + Environment.MachineName);
            info.AppendLine("Windows:              " + EditionText(out editionOk) + "  (" + Environment.OSVersion.VersionString + ")");
            info.AppendLine("Sprache Programm:     " + L.Code + "  (Windows: " + CultureInfo.CurrentUICulture.Name + ")");
            info.AppendLine("Letzter Start (UTC):  " + BootTimeUtc().ToString("yyyy-MM-dd HH:mm"));
            info.AppendLine("Eingerichtet:         " + (store.IsInstalled ? "ja" : "nein"));
            info.AppendLine("Schutz eingestellt:   " + (store.Enabled ? "AN" : "AUS"));
            info.AppendLine("Neustart nötig:       " + (store.IsPending ? "ja" : "nein"));
            info.AppendLine("Browser-Vorlage:      " + BrowserSetup.Describe(store));
            info.AppendLine("Hintergrund Gäste:    " + (store.Get("Hintergrund") ?? "aus") + "  Stil " + (store.Get("HintergrundStil") ?? "-"));
            info.AppendLine("Autostart Gäste:      " + (store.Get("Autostart") ?? "aus"));
            info.AppendLine("Desktop Gäste:        " + string.Join(", ", GuestExtras.DesktopNames(store).ToArray()));
            info.AppendLine("Willkommensfenster:   " + (GuestExtras.WelcomeOn(store) ? "an" : "aus"));
            info.AppendLine("Ausschalten nach (min): " + GuestExtras.IdleMinutes(store) + "   Hinweis Gäste: " + (GuestExtras.NoticeOn(store) ? "an" : "aus"));
            info.AppendLine("Täglicher Neustart:   " + (GuestExtras.RestartTime(store) ?? "aus"));
            info.AppendLine("Profilbild Gäste:     " + (store.Get("Profilbild") ?? "aus"));
            info.AppendLine("Sperren Gäste:        " + GuestExtras.Locks(store) + "   Drucker Gäste: " + (GuestExtras.GuestPrinter(store) ?? "aus"));
            info.AppendLine("Anmeldebildschirm:    " + SignIn.StatusText(store));
            info.AppendLine("Sprache Gäste:        " + (GuestExtras.GuestLanguage(store) ?? "Windows-Vorgabe") + "   Installationssprache: " + GuestExtras.InstallLanguage() +
                "   Sprachpakete: " + string.Join(", ", GuestExtras.InstalledUiLanguages().ToArray()) + "   Tastatur Admin: " + (GuestExtras.AdminKeyboard() ?? "-") +
                "   Sprachwahl Gäste: " + (GuestExtras.ChoiceOn(store) ? "an" : "aus"));
            try
            {
                using (RegistryKey wl = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"))
                    if (wl != null)
                        info.AppendLine("Auto-Anmeldung:       AutoAdminLogon=" + wl.GetValue("AutoAdminLogon", "-") +
                            "  DefaultUserName=" + wl.GetValue("DefaultUserName", "-") + "  DefaultDomainName=" + wl.GetValue("DefaultDomainName", "-") +
                            "  ForceAutoLogon=" + wl.GetValue("ForceAutoLogon", "-") + "  AutoLogonCount=" + wl.GetValue("AutoLogonCount", "-") +
                            "  DefaultPassword=" + (wl.GetValue("DefaultPassword") != null ? "vorhanden" : "-"));   // Kennwort selbst nie
            }
            catch { }
            info.AppendLine("Browser gefunden:     " + BrowserSetup.Found() + "  (Edge: " + (BrowserSetup.EdgeExe ?? "-") +
                "; Chrome: " + (BrowserSetup.ChromeExe ?? "-") + "; Firefox: " + (BrowserSetup.FirefoxExe ?? "-") + ")");

            // Aktueller Status (fragt auch die Live-Einstellungen als SYSTEM ab)
            var status = new StringBuilder();
            int code = await RunScript("Status", l => { lock (status) status.AppendLine(l); }).ConfigureAwait(false);
            status.AppendLine("Rückgabecode: " + code);
            File.WriteAllText(Path.Combine(dir, "Status.txt"), status.ToString(), Encoding.UTF8);

            var files = new[] {
                Path.Combine(DataDir, "GaestePC.log"),
                Path.Combine(DataDir, "Bereinigung.log"),
                Path.Combine(DataDir, "Anmeldung.log"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SharedPCSetup.log") };
            foreach (string f in files)
            {
                try
                {
                    if (File.Exists(f)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
                    else info.AppendLine("Nicht vorhanden:      " + f);
                }
                catch (Exception ex) { info.AppendLine("Nicht kopierbar:      " + f + " (" + ex.Message + ")"); }
            }
            try
            {
                RunQuiet("reg.exe", "export \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\SharedPC\" \"" +
                    Path.Combine(dir, "SharedPC-Registry.reg") + "\" /y");
                RunQuiet("reg.exe", "export \"HKLM\\SOFTWARE\\Policies\" \"" +
                    Path.Combine(dir, "Richtlinien-Registry.reg") + "\" /y");
                // Anmeldebildschirm: wer ist vorausgewählt (LastLoggedOn*), welche Anmelde-Kacheln gibt es
                RunQuiet("reg.exe", "export \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Authentication\\LogonUI\" \"" +
                    Path.Combine(dir, "Anmeldebildschirm-Registry.reg") + "\" /y");
                RunQuiet("reg.exe", "export \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Authentication\\Credential Providers\" \"" +
                    Path.Combine(dir, "Anmelde-Anbieter-Registry.reg") + "\" /y");
                // u. a. DontDisplayLastUserName (Shared PC schaltet es ein -> leeres Formular „Anderer Benutzer“)
                RunQuiet("reg.exe", "export \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\" \"" +
                    Path.Combine(dir, "Anmelde-Richtlinien-Registry.reg") + "\" /y");
                RunQuiet("reg.exe", "export \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AccountPicture\" \"" +
                    Path.Combine(dir, "Profilbilder-Registry.reg") + "\" /y");
            }
            catch { }

            File.WriteAllText(Path.Combine(dir, "Info.txt"), info.ToString(), Encoding.UTF8);
            return dir;
        }

        public static void InstallFiles(Action<string> log)
        {
            if (Program.Demo)
            {
                log(L.T("DEMO: würde nach ", "DEMO: copierebbe in ", "DEMO: would copy to ") + InstallDir);
                return;
            }
            Directory.CreateDirectory(InstallDir);
            string self = Application.ExecutablePath;
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(self, InstalledExe, true);
                log(L.T("Programm kopiert nach ", "Programma copiato in ", "Program copied to ") + InstalledExe);
            }
            // Verknüpfungen nur für dich (Admin) – Gäste sehen sie nicht
            CreateShortcut(ShortcutStart, InstalledExe, null);
            CreateShortcut(ShortcutDesktop, InstalledExe, null);
            RemoveOldVersion(log);
            log(L.T("Verknüpfungen im Startmenü und auf deinem Desktop angelegt.",
                    "Collegamenti creati nel menu Start e sul tuo desktop.",
                    "Shortcuts created in the Start menu and on your desktop."));
        }

        // Läuft diese exe von woanders (z. B. USB-Stick) und unterscheidet sie sich von der installierten?
        public static bool InstalledExeDiffers()
        {
            try
            {
                string self = Path.GetFullPath(Application.ExecutablePath);
                if (string.Equals(self, Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase)) return false;
                if (!File.Exists(InstalledExe)) return true;
                var a = new FileInfo(self);
                var b = new FileInfo(InstalledExe);
                if (a.Length != b.Length) return true;
                return !File.ReadAllBytes(self).SequenceEqual(File.ReadAllBytes(InstalledExe));
            }
            catch { return false; }
        }

        // Nach einem Update: Skripte auf dem PC auf den Stand dieser Version bringen
        public static void RefreshScripts(Store store)
        {
            var log = new StringBuilder();
            try { RunScript("Aktualisieren", l => log.AppendLine(l)).Wait(); } catch (Exception ex) { log.AppendLine(ex.Message); }
            if (GuestExtras.PictureOn(store))
                try { GuestExtras.ApplyPicture(store, store.Get("Profilbild"), l => log.AppendLine(l)); }
                catch (Exception ex) { log.AppendLine("Profilbild: " + ex.Message); }
            // Option „Nur Gast“ gibt es nicht mehr: Sie holte das Formular „Anderer Benutzer“ nach vorne.
            // Der Standard wählt „Gast“ vor und zeigt nur „Gast“ + „Anderer Benutzer“ – also zurückschalten.
            if (GuestExtras.GuestOnlyTile())
                try { GuestExtras.ApplyGuestOnlyTile(false, l => log.AppendLine(l)); } catch (Exception ex) { log.AppendLine("Anmeldung: " + ex.Message); }
            if (GuestExtras.RestartTime(store) != null) GuestExtras.SyncRestartTime(GuestExtras.RestartTime(store));   // ab 1.18: für den Gast-Hinweis
            try
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\FreshGuest"))      // ab 1.25: für die Sprachwahl der Gäste
                    if (GuestExtras.GuestLanguage(store) != null) k.SetValue("GastStandardSprache", GuestExtras.GuestLanguage(store), RegistryValueKind.String);
                    else k.DeleteValue("GastStandardSprache", false);
            }
            catch { }
            try { GuestExtras.SyncFastStart(GuestExtras.StartLinkNeeded()); } catch { }        // ab 1.26: Gast-Fenster ohne Startverzögerung
            foreach (string task in new[] { "FreshGuest-Profilbild", "FreshGuest-Neustart" }) TaskRunOnBattery(task);       // ab 1.19: auch im Akkubetrieb
            // Anmeldebildschirm: Aufgabe (neu) anlegen – ab 1.17 wird „Guest“ automatisch ausgewählt
            try { SignIn.Sync(store, l => log.AppendLine(l)); } catch (Exception ex) { log.AppendLine("Anmeldung: " + ex.Message); }
            BrowserOpt browser = BrowserSetup.Current(store);
            if (browser != BrowserOpt.None)     // bis 1.7 in HKLM (galt auch für dein Konto) -> jetzt nur Gäste
                try { BrowserSetup.Apply(store, browser, store.Get("Startseite"), l => log.AppendLine(l)); }
                catch (Exception ex) { log.AppendLine("Browser: " + ex.Message); }
            try { BrowserSetup.SyncPublicDuplicates(store, l => log.AppendLine(l)); store.Save(); }      // ab 1.27: jedes Programm nur einmal
            catch (Exception ex) { log.AppendLine("Desktop: " + ex.Message); }
        }

        // Kopiert diese exe über die installierte (nur wenn sie von woanders gestartet wurde)
        public static bool UpdateInstalledExe()
        {
            string self = Application.ExecutablePath;
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(InstallDir);
                SignIn.StopRunning();           // die Wache für den Anmeldebildschirm läuft aus dieser exe
                File.Copy(self, InstalledExe, true);
                CreateShortcut(ShortcutStart, InstalledExe, null);
                CreateShortcut(ShortcutDesktop, InstalledExe, null);
            }
            RemoveOldVersion(null);
            return true;
        }

        public static void RemoveFiles(Action<string> log)
        {
            if (Program.Demo) { log("DEMO: " + L.T("würde Programmdateien entfernen.", "rimuoverebbe i file del programma.", "would remove program files.")); return; }
            foreach (string l in new[] { ShortcutStart, ShortcutDesktop })
                try { if (File.Exists(l)) File.Delete(l); } catch { }
            RemoveOldVersion(null);
            // Ordner erst löschen, wenn dieses Programm beendet ist
            string cmd = string.Format("/c ping 127.0.0.1 -n 4 >nul & rmdir /s /q \"{0}\" & rmdir /s /q \"{1}\"",
                InstallDir, DataDir);
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { UseShellExecute = false, CreateNoWindow = true });
            log(L.T("Programmdateien werden nach dem Schließen entfernt.", "I file del programma verranno rimossi alla chiusura.",
                    "Program files will be removed after closing."));
        }

        public static void Reboot()
        {
            if (Program.Demo)
            {
                MessageBox.Show(L.T("DEMO: Hier würde der PC jetzt neu starten.", "DEMO: qui il PC si riavvierebbe.",
                    "DEMO: the PC would restart now."), L.AppName);
                return;
            }
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5")
            { UseShellExecute = false, CreateNoWindow = true });
        }

        public static void CreateShortcut(string lnk, string target, string arguments)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            try
            {
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                Type st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
                if (arguments != null) st.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { arguments });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                Marshal.FinalReleaseComObject(sc);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
    }

    // ------------------------------------------------------------------
    // Browser-Vorlage für Gäste: Richtlinien in HKLM\SOFTWARE\Policies.
    // Gelten für jedes (neue) Konto, also auch für jeden Gast, und für das Admin-Konto.
    // Startseite: Firefox per Richtlinie. Edge/Chrome ignorieren Startseiten-Richtlinien
    // auf PCs ohne Firmen-Domäne -> stattdessen Symbol "Internet" auf dem öffentlichen Desktop.
    // ------------------------------------------------------------------
    [Flags]
    enum BrowserOpt { None = 0, NoWelcome = 1, NoPasswords = 2, NoDefaultPrompt = 4, StartPage = 8, Shortcuts = 16 }

    static class BrowserSetup
    {
        const string Edge = @"SOFTWARE\Policies\Microsoft\Edge";
        const string Chrome = @"SOFTWARE\Policies\Google\Chrome";
        const string Firefox = @"SOFTWARE\Policies\Mozilla\Firefox";
        const BrowserOpt All = BrowserOpt.NoWelcome | BrowserOpt.NoPasswords | BrowserOpt.NoDefaultPrompt | BrowserOpt.StartPage | BrowserOpt.Shortcuts;

        class Val
        {
            public string Key, Name; public object Data; public RegistryValueKind Kind;
            public Val(string key, string name, object data, RegistryValueKind kind) { Key = key; Name = name; Data = data; Kind = kind; }
        }

        static Val D(string key, string name, int v) { return new Val(key, name, v, RegistryValueKind.DWord); }
        static Val S(string key, string name, string v) { return new Val(key, name, v, RegistryValueKind.String); }

        static List<Val> Values(BrowserOpt o, string url)
        {
            var v = new List<Val>();
            if ((o & BrowserOpt.NoWelcome) != 0)
            {
                v.Add(D(Edge, "HideFirstRunExperience", 1));
                v.Add(D(Chrome, "PromotionalTabsEnabled", 0));
                v.Add(D(Chrome, "PrivacySandboxPromptEnabled", 0));
                v.Add(D(Chrome, "BrowserSignin", 0));            // überspringt den Anmelde-Assistenten
                v.Add(S(Firefox, "OverrideFirstRunPage", ""));
                v.Add(S(Firefox, "OverridePostUpdatePage", ""));
                v.Add(D(Firefox + @"\UserMessaging", "SkipOnboarding", 1));
                v.Add(D(Firefox + @"\UserMessaging", "MoreFromMozilla", 0));
                v.Add(D(Firefox + @"\UserMessaging", "ExtensionRecommendations", 0));
                v.Add(D(Firefox + @"\UserMessaging", "FeatureRecommendations", 0));
            }
            if ((o & BrowserOpt.NoPasswords) != 0)
            {
                v.Add(D(Edge, "PasswordManagerEnabled", 0));
                v.Add(D(Chrome, "PasswordManagerEnabled", 0));
                v.Add(D(Firefox, "PasswordManagerEnabled", 0));
                v.Add(D(Firefox, "OfferToSaveLogins", 0));
            }
            if ((o & BrowserOpt.NoDefaultPrompt) != 0)
            {
                v.Add(D(Edge, "DefaultBrowserSettingEnabled", 0));
                v.Add(D(Edge, "DefaultBrowserSettingsCampaignEnabled", 0));
                v.Add(D(Chrome, "DefaultBrowserSettingEnabled", 0));
                v.Add(D(Firefox, "DontCheckDefaultBrowser", 1));
                v.Add(D(Firefox, "DisableDefaultBrowserAgent", 1));
            }
            if ((o & BrowserOpt.StartPage) != 0)
            {
                v.Add(S(Firefox + @"\Homepage", "URL", url ?? ""));
                v.Add(S(Firefox + @"\Homepage", "StartPage", "homepage"));
                v.Add(D(Firefox + @"\Homepage", "Locked", 0));
            }
            return v;
        }

        public static string EdgeExe
        {
            get
            {
                foreach (var pf in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
                {
                    string p = Path.Combine(Environment.GetFolderPath(pf), @"Microsoft\Edge\Application\msedge.exe");
                    if (File.Exists(p)) return p;
                }
                return null;
            }
        }
        public static string ChromeExe
        {
            get
            {
                foreach (var pf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
                {
                    string p = Path.Combine(Environment.GetFolderPath(pf), @"Google\Chrome\Application\chrome.exe");
                    if (File.Exists(p)) return p;
                }
                return null;
            }
        }
        public static string FirefoxExe
        {
            get
            {
                foreach (var pf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
                {
                    string p = Path.Combine(Environment.GetFolderPath(pf), @"Mozilla Firefox\firefox.exe");
                    if (File.Exists(p)) return p;
                }
                return null;
            }
        }

        public static string Found()
        {
            var l = new List<string>();
            if (EdgeExe != null) l.Add("Edge");
            if (ChromeExe != null) l.Add("Chrome");
            if (FirefoxExe != null) l.Add("Firefox");
            return l.Count == 0 ? L.T("keiner", "nessuno", "none") : string.Join(", ", l.ToArray());
        }

        // Symbole liegen auf dem Desktop des Windows-Standardprofils -> jeder NEUE Gast hat sie, dein Konto nicht.
        // (Bis Version 1.7 lagen sie auf dem öffentlichen Desktop – die werden beim Aufräumen mit entfernt.)
        // „Internet (Firefox)“ wird seit 1.24 nicht mehr angelegt (Firefox bleibt in seiner Installationssprache) –
        // steht nur noch in der Liste, damit ein altes Symbol beim Aufräumen entfernt wird.
        static readonly string[] LnkNames = { "Internet (Edge).lnk", "Internet (Chrome).lnk", "Internet (Firefox).lnk" };
        static string PublicDesktop { get { return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory); } }
        static string GuestDesktop { get { return GuestExtras.DefaultDesktop; } }
        static string EdgeLnk { get { return Path.Combine(GuestDesktop, LnkNames[0]); } }
        static string ChromeLnk { get { return Path.Combine(GuestDesktop, LnkNames[1]); } }

        // Nur für das eigene Konto installiert (AppData) -> Gäste haben diesen Browser nicht
        static bool OnlyForMe(string relExe)
        {
            try { return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), relExe)); }
            catch { return false; }
        }

        // Symbole auf dem öffentlichen Desktop (sieht jeder Gast). Edge/Chrome öffnen damit die Startseite.
        // Gibt die Programme zurück, für die ein Symbol angelegt wurde (für HidePublicDuplicates)
        static List<string> MakeShortcuts(BrowserOpt o, string url, Action<string> log)
        {
            var exes = new List<string>();
            bool all = (o & BrowserOpt.Shortcuts) != 0;
            string arg = (o & BrowserOpt.StartPage) != 0 && url != null ? "\"" + url + "\"" : null;
            if (!all && arg == null) return exes;
            var made = new List<string>();
            var missing = new List<string>();
            Action<string, string, string, bool> one = (name, exe, lnk, withArg) =>
            {
                if (exe == null) { missing.Add(name); return; }
                if (Program.Demo) { made.Add(name); return; }
                try { Directory.CreateDirectory(GuestDesktop); Sys.CreateShortcut(lnk, exe, withArg ? arg : null); made.Add(name); exes.Add(exe); }
                catch (Exception ex) { log("  " + name + ": " + ex.Message); }
            };
            one("Edge", EdgeExe, EdgeLnk, true);
            one("Chrome", ChromeExe, ChromeLnk, true);
            if (made.Count > 0)
                log(L.T("✔ Symbole auf dem Desktop der Gäste: ", "✔ Icone sul desktop degli ospiti: ", "✔ Icons on the guests' desktop: ") +
                    string.Join(", ", made.Select(n => "Internet (" + n + ")").ToArray()));
            foreach (string n in missing)
            {
                bool mine = n == "Chrome" && OnlyForMe(@"Google\Chrome\Application\chrome.exe");
                if (mine)
                    log("⚠ " + n + L.T(" ist nur für dein Konto installiert – Gäste haben ihn nicht. Bitte für alle Benutzer installieren.",
                                       " è installato solo per il tuo account – gli ospiti non lo hanno. Installarlo per tutti gli utenti.",
                                       " is installed for your account only – guests don't have it. Please install it for all users."));
                else if (all || n != "Edge")
                    log("  " + n + L.T(" nicht installiert – kein Symbol.", " non installato – nessuna icona.", " not installed – no icon."));
            }
            return exes;
        }

        // Chrome und Edge legen bei der Installation eigene Symbole auf den öffentlichen Desktop („Google Chrome“,
        // „Microsoft Edge“). Die sieht jeder Gast – zusammen mit „Internet (…)“ wäre jeder Browser doppelt da.
        // Deshalb kommen sie in eine Sicherung; dein eigenes Konto bekommt eine Kopie auf seinen Desktop.
        // Beim Abschalten der Browser-Einstellungen bzw. Deinstallieren kommt alles zurück.
        static string PublicBackup { get { return Path.Combine(Sys.DataDir, "Desktop-Sicherung"); } }
        const string EdgeUpdatePolicy = @"SOFTWARE\Policies\Microsoft\EdgeUpdate";

        static List<string> Names(Store store, string key)
        {
            return (store.Get(key) ?? "").Split('|').Where(n => n.Length > 0).ToList();
        }

        static void HidePublicDuplicates(Store store, List<string> exes, Action<string> log)
        {
            if (exes.Count == 0 || !Directory.Exists(PublicDesktop)) return;
            var moved = Names(store, "SymboleVerschoben");
            var copied = Names(store, "SymboleKopiert");
            string mine = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var hidden = new List<string>();
            bool edge = false;
            foreach (string lnk in Directory.GetFiles(PublicDesktop, "*.lnk"))
            {
                string target = GuestExtras.ShortcutTarget(lnk);
                if (string.IsNullOrEmpty(target)) continue;
                string exe = exes.FirstOrDefault(x => SameProgram(x, target));
                if (exe == null) continue;
                string name = Path.GetFileName(lnk);
                try
                {
                    Directory.CreateDirectory(PublicBackup);
                    if (mine.Length > 0 && !File.Exists(Path.Combine(mine, name)))
                    {
                        File.Copy(lnk, Path.Combine(mine, name));
                        if (!copied.Contains(name)) copied.Add(name);
                    }
                    File.Copy(lnk, Path.Combine(PublicBackup, name), true);
                    File.Delete(lnk);
                    if (!moved.Contains(name)) moved.Add(name);
                    hidden.Add(Path.GetFileNameWithoutExtension(name));
                    if (Path.GetFileName(target).Equals("msedge.exe", StringComparison.OrdinalIgnoreCase)) edge = true;
                }
                catch (Exception ex) { log("  " + name + ": " + ex.Message); }
            }
            // Edge-Updates legen das Symbol sonst wieder auf den öffentlichen Desktop
            if (edge && store.Get("EdgeUpdateSymbol") == null)
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.CreateSubKey(EdgeUpdatePolicy))
                        if (k.GetValue("CreateDesktopShortcutDefault") == null)
                        {
                            k.SetValue("CreateDesktopShortcutDefault", 0, RegistryValueKind.DWord);
                            store.Set("EdgeUpdateSymbol", "1");
                        }
                }
                catch { }
            store.Set("SymboleVerschoben", string.Join("|", moved.ToArray()));
            store.Set("SymboleKopiert", string.Join("|", copied.ToArray()));
            if (hidden.Count > 0)
                log(L.T("✔ Doppelte Symbole vom gemeinsamen Desktop genommen (dein Konto behält sie): ",
                        "✔ Icone doppie tolte dal desktop comune (il tuo account le mantiene): ",
                        "✔ Duplicate icons removed from the shared desktop (your account keeps them): ") + string.Join(", ", hidden.ToArray()));
        }

        static string NormPath(string p)
        {
            try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(p ?? "")).TrimEnd('\\'); } catch { return p ?? ""; }
        }

        static bool SameProgram(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (string.Equals(NormPath(a), NormPath(b), StringComparison.OrdinalIgnoreCase)) return true;
            string fa = Path.GetFileName(a), fb = Path.GetFileName(b);
            return string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase) &&
                   (fa.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase) || fa.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase));
        }

        // Verknüpfungen auf dem Desktop der Gäste (Standardprofil): Name -> Ziel
        static List<KeyValuePair<string, string>> GuestDesktopLinks()
        {
            var l = new List<KeyValuePair<string, string>>();
            try
            {
                if (Directory.Exists(GuestDesktop))
                    foreach (string f in Directory.GetFiles(GuestDesktop, "*.lnk"))
                    {
                        string t = GuestExtras.ShortcutTarget(f);
                        if (!string.IsNullOrEmpty(t)) l.Add(new KeyValuePair<string, string>(Path.GetFileName(f), t));
                    }
            }
            catch { }
            return l;
        }

        static string GuestBackup { get { return Path.Combine(PublicBackup, "Gast-Desktop"); } }

        // Jedes Programm soll beim Gast nur EINMAL auf dem Desktop sein. Der Gast sieht den gemeinsamen Desktop UND
        // den Desktop des Standardprofils (Reiter „Desktop“ + „Internet (…)“). Doppelte vom gemeinsamen Desktop kommen
        // in die Sicherung (dein Konto behält eine Kopie); eine zweite Verknüpfung zu Edge/Chrome neben „Internet (…)“
        // ebenso. Läuft nach jeder Änderung am Desktop oder an den Browser-Einstellungen und beim Aktualisieren.
        public static void SyncPublicDuplicates(Store store, Action<string> log)
        {
            if (Program.Demo) return;
            if (log == null) log = s => { };
            RestorePublicDuplicates(store);
            var links = GuestDesktopLinks();
            // 1) Auf dem Gast-Desktop selbst: neben „Internet (Edge/Chrome)“ keine zweite Verknüpfung zum selben Browser
            var ours = links.Where(kv => LnkNames.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)).ToList();
            var movedGuest = new List<string>();
            foreach (var kv in links)
            {
                if (LnkNames.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)) continue;
                if (!ours.Any(o => SameProgram(o.Value, kv.Value))) continue;
                try
                {
                    Directory.CreateDirectory(GuestBackup);
                    File.Copy(Path.Combine(GuestDesktop, kv.Key), Path.Combine(GuestBackup, kv.Key), true);
                    File.Delete(Path.Combine(GuestDesktop, kv.Key));
                    movedGuest.Add(kv.Key);
                }
                catch (Exception ex) { log("  " + kv.Key + ": " + ex.Message); }
            }
            if (movedGuest.Count > 0)
            {
                store.Set("GastSymboleVerschoben", string.Join("|", movedGuest.ToArray()));
                log(L.T("✔ Schon als „Internet (…)“ da – nicht doppelt auf den Gast-Desktop: ",
                        "✔ Già presente come «Internet (…)» – non doppio sul desktop ospiti: ",
                        "✔ Already there as \"Internet (…)\" – not twice on the guest desktop: ") +
                    string.Join(", ", movedGuest.Select(n => Path.GetFileNameWithoutExtension(n)).ToArray()));
            }
            // 2) Gemeinsamer Desktop: alles, was es auf dem Gast-Desktop schon gibt
            var targets = links.Where(kv => !movedGuest.Contains(kv.Key)).Select(kv => kv.Value).ToList();
            HidePublicDuplicates(store, targets, log);
        }

        static void RestorePublicDuplicates(Store store)
        {
            foreach (string name in Names(store, "GastSymboleVerschoben"))      // Gast-Desktop: zurück
                try
                {
                    string bak = Path.Combine(GuestBackup, name), dst = Path.Combine(GuestDesktop, name);
                    if (!File.Exists(bak)) continue;
                    if (!File.Exists(dst)) File.Copy(bak, dst);
                    File.Delete(bak);
                }
                catch { }
            store.Remove("GastSymboleVerschoben");
            try { if (Directory.Exists(GuestBackup) && !Directory.EnumerateFileSystemEntries(GuestBackup).Any()) Directory.Delete(GuestBackup); } catch { }
            string mine = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            foreach (string name in Names(store, "SymboleVerschoben"))
                try
                {
                    string bak = Path.Combine(PublicBackup, name), pub = Path.Combine(PublicDesktop, name);
                    if (!File.Exists(bak)) continue;
                    if (!File.Exists(pub)) File.Copy(bak, pub);
                    File.Delete(bak);
                }
                catch { }
            foreach (string name in Names(store, "SymboleKopiert"))     // unsere Kopie auf deinem Desktop – sonst wieder doppelt
                try
                {
                    string own = Path.Combine(mine, name);
                    if (mine.Length > 0 && File.Exists(own) && File.Exists(Path.Combine(PublicDesktop, name))) File.Delete(own);
                }
                catch { }
            try { if (Directory.Exists(PublicBackup) && !Directory.EnumerateFileSystemEntries(PublicBackup).Any()) Directory.Delete(PublicBackup); } catch { }
            if (store.Get("EdgeUpdateSymbol") != null)
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(EdgeUpdatePolicy, true))
                        if (k != null) k.DeleteValue("CreateDesktopShortcutDefault", false);
                    DeleteIfEmpty(Registry.LocalMachine, EdgeUpdatePolicy);
                }
                catch { }
            foreach (string key in new[] { "SymboleVerschoben", "SymboleKopiert", "EdgeUpdateSymbol" }) store.Remove(key);
        }

        // "www.google.it" -> "https://www.google.it/"; ungültig -> null
        public static string NormalizeUrl(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return null;
            if (!Regex.IsMatch(s, "^https?://", RegexOptions.IgnoreCase)) s = "https://" + s;
            Uri u;
            if (!Uri.TryCreate(s, UriKind.Absolute, out u)) return null;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
            if (u.Host.Length == 0 || u.Host.IndexOf('.') < 0 || s.IndexOfAny(new[] { '"', ' ' }) >= 0) return null;
            return u.AbsoluteUri;
        }

        public static BrowserOpt Current(Store s)
        {
            int v;
            return s.Get("BrowserAktiv") == "1" && int.TryParse(s.Get("BrowserOptionen"), out v) ? (BrowserOpt)v : BrowserOpt.None;
        }

        public static string Describe(Store s)
        {
            BrowserOpt o = Current(s);
            if (o == BrowserOpt.None) return "aus";
            return o.ToString() + ((o & BrowserOpt.StartPage) != 0 ? "  (" + s.Get("Startseite") + ")" : "");
        }

        // Die Richtlinien stehen im Windows-Standardprofil (wird zu HKCU jedes NEUEN Gastes).
        // Dein eigenes Konto bekommt sie nicht. Bis Version 1.7 standen sie in HKLM (galten für alle) –
        // die werden hier jedes Mal mit aufgeräumt.
        public static void Apply(Store store, BrowserOpt o, string url, Action<string> log)
        {
            var vals = Values(o, url);
            if (Program.Demo)
            {
                log("DEMO: " + vals.Count + L.T(" Browser-Richtlinien würden gesetzt.", " criteri del browser verrebbero impostati.", " browser policies would be set."));
            }
            else
            {
                ClearMachine();
                ClearShortcuts();
                GuestExtras.WithDefaultHive(root =>
                {
                    ClearHive(root);
                    foreach (Val v in vals)
                        using (RegistryKey k = root.CreateSubKey(v.Key))
                            k.SetValue(v.Name, v.Data, v.Kind);
                });
            }
            MakeShortcuts(o, url, log);
            SyncPublicDuplicates(store, log);
            if ((o & BrowserOpt.NoWelcome) != 0) log(L.T("✔ Kein Willkommensassistent", "✔ Nessuna procedura di benvenuto", "✔ No welcome wizard"));
            if ((o & BrowserOpt.NoPasswords) != 0) log(L.T("✔ Kennwörter werden nicht gespeichert", "✔ Le password non vengono salvate", "✔ Passwords are not saved"));
            if ((o & BrowserOpt.NoDefaultPrompt) != 0) log(L.T("✔ Keine Standardbrowser-Frage", "✔ Nessuna richiesta browser predefinito", "✔ No default-browser prompt"));
            if ((o & BrowserOpt.StartPage) != 0)
                log(L.T("✔ Startseite: ", "✔ Pagina iniziale: ", "✔ Start page: ") + url + "  (Firefox; Edge/Chrome: " +
                    L.T("über ihr Symbol auf dem Desktop", "tramite la loro icona sul desktop", "via their desktop icon") + ")");
            if (o != BrowserOpt.None)
                log(L.T("Gilt nur für Gäste, ab dem nächsten Gast – dein Konto bleibt unverändert.",
                        "Vale solo per gli ospiti, dal prossimo ospite – il tuo account resta invariato.",
                        "Applies to guests only, from the next guest – your account stays unchanged."));
            store.Set("BrowserAktiv", o == BrowserOpt.None ? "0" : "1");
            store.Set("BrowserOptionen", ((int)o).ToString());
            if (url != null) store.Set("Startseite", url);
            store.Save();
        }

        // Entfernt NUR die Werte, die dieses Programm setzt (andere Richtlinien bleiben).
        public static void Remove(Store store, Action<string> log)
        {
            if (!Program.Demo)
            {
                ClearMachine();
                ClearShortcuts();
                GuestExtras.WithDefaultHive(ClearHive);
                SyncPublicDuplicates(store, null);       // Browser-Symbole weg – Doppelte vom Reiter „Desktop“ bleiben versteckt
            }
            store.Set("BrowserAktiv", "0");
            if (log != null) log(L.T("Browser-Einstellungen für Gäste entfernt.", "Impostazioni browser per gli ospiti rimosse.",
                                     "Guest browser settings removed."));
        }

        static readonly string[] CleanupKeys = { Firefox + @"\UserMessaging", Firefox + @"\Homepage", Firefox,
                                                 @"SOFTWARE\Policies\Mozilla", Edge, Chrome, @"SOFTWARE\Policies\Google" };

        // Unsere Werte unter einem Schlüssel (HKLM oder Standardprofil) löschen, leere Schlüssel aufräumen
        static void ClearValues(RegistryKey root)
        {
            foreach (Val v in Values(All, ""))
                using (RegistryKey k = root.OpenSubKey(v.Key, true))
                    if (k != null) k.DeleteValue(v.Name, false);
            foreach (string key in CleanupKeys) DeleteIfEmpty(root, key);
        }
        static void ClearMachine() { ClearValues(Registry.LocalMachine); }       // alte Versionen (bis 1.7)
        static void ClearHive(RegistryKey root) { ClearValues(root); }

        static void ClearShortcuts()
        {
            foreach (string dir in new[] { PublicDesktop, GuestDesktop })
                foreach (string n in LnkNames)
                    try { string f = Path.Combine(dir, n); if (File.Exists(f)) File.Delete(f); } catch { }
        }

        static void DeleteIfEmpty(RegistryKey root, string key)
        {
            try
            {
                bool empty;
                using (RegistryKey k = root.OpenSubKey(key))
                {
                    if (k == null) return;
                    empty = k.ValueCount == 0 && k.SubKeyCount == 0;
                }
                if (empty) root.DeleteSubKey(key, false);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------
    // Gemeinsame Optik
    // ------------------------------------------------------------------
    static class Ui
    {
        // Dunkles Design, passend zur Kopfzeile mit dem Neon-Pinguin
        public static readonly Color Bg = Color.FromArgb(22, 19, 46);         // Fenster        #16132E
        public static readonly Color Surface = Color.FromArgb(32, 28, 66);    // Reiter, Log    #201C42
        public static readonly Color Field = Color.FromArgb(42, 37, 82);      // Eingabefelder  #2A2552
        public static readonly Color FieldHover = Color.FromArgb(54, 48, 104);
        public static readonly Color Border = Color.FromArgb(78, 70, 136);    // Rahmen         #4E4688
        public static readonly Color Text = Color.FromArgb(238, 236, 250);    // Schrift        #EEECFA
        public static readonly Color Muted = Color.FromArgb(184, 178, 218);   // Hinweise       #B8B2DA
        public static readonly Color Disabled = Color.FromArgb(120, 114, 160);
        public static readonly Color Link = Color.FromArgb(34, 228, 255);     // Neon-Cyan      #22E4FF
        public static readonly Color Blue = Color.FromArgb(106, 61, 240);     // Marken-Violett #6A3DF0
        public static readonly Color BlueHover = Color.FromArgb(128, 88, 248);
        public static readonly Color Green = Color.FromArgb(27, 127, 69);     // Status AN
        public static readonly Color Grey = Color.FromArgb(70, 66, 104);     // Status AUS
        public static readonly Color Orange = Color.FromArgb(194, 94, 0);     // Neustart nötig
        public static readonly Color OkText = Color.FromArgb(91, 227, 142);   // Häkchen
        public static readonly Color Red = Color.FromArgb(255, 122, 122);     // Fehlertext

        public static void Style(Form f, int w, int h)
        {
            f.Font = new Font("Segoe UI", 10f);
            f.AutoScaleDimensions = new SizeF(96f, 96f);
            f.AutoScaleMode = AutoScaleMode.Dpi;
            f.ClientSize = new Size(w, h);
            f.FormBorderStyle = FormBorderStyle.FixedSingle;
            f.MaximizeBox = false;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.BackColor = Bg;
            f.ForeColor = Text;
            f.Icon = AppIcon.Get();
            f.HandleCreated += (s, e) => DarkWindow(f.Handle);
            f.ControlAdded += (s, e) => Theme(e.Control);
        }

        // Eingabefelder, Auswahllisten und Unterelemente dunkel einfärben (Labels/Häkchen erben die Farben vom Fenster)
        static void Theme(Control c)
        {
            var tb = c as TextBox;
            if (tb != null)
            {
                if (tb.BackColor.IsSystemColor) tb.BackColor = Field;
                tb.ForeColor = Text;
                tb.BorderStyle = BorderStyle.FixedSingle;
                if (tb.Multiline) tb.HandleCreated += (s, e) => DarkScrollbars(tb.Handle);
            }
            var cb = c as ComboBox;
            if (cb != null && cb.DrawMode == DrawMode.Normal)
            {
                cb.FlatStyle = FlatStyle.Flat;
                cb.BackColor = Field;
                cb.ForeColor = Text;
                cb.DrawMode = DrawMode.OwnerDrawFixed;
                cb.DrawItem += (s, e) =>
                {
                    if (e.Index < 0) return;
                    bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                    using (var br = new SolidBrush(sel ? Blue : Field)) e.Graphics.FillRectangle(br, e.Bounds);
                    var r = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height);
                    TextRenderer.DrawText(e.Graphics, cb.Items[e.Index].ToString(), cb.Font, r,
                        cb.Enabled ? Text : Disabled, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                };
            }
            var lb = c as ListBox;
            if (lb != null) { lb.BackColor = Field; lb.ForeColor = Text; lb.BorderStyle = BorderStyle.FixedSingle; }
            var ll = c as LinkLabel;
            if (ll != null) { ll.LinkColor = ll.ActiveLinkColor = ll.VisitedLinkColor = Link; ll.DisabledLinkColor = Disabled; }
            if (c is Panel || c is DarkTabs)
            {
                foreach (Control k in c.Controls) Theme(k);
                c.ControlAdded += (s, e) => Theme(e.Control);
            }
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        // Dunkle Titelleiste (Windows 10 ab 20H1 und Windows 11; ältere ignorieren es)
        static void DarkWindow(IntPtr h)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(h, 20, ref on, 4) != 0) DwmSetWindowAttribute(h, 19, ref on, 4);
            }
            catch { }
        }

        static void DarkScrollbars(IntPtr h)
        {
            try { SetWindowTheme(h, "DarkMode_Explorer", null); } catch { }
        }

        public static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Button MakeButton(string text, int x, int y, int w, int h, bool primary)
        {
            var b = new Button { Text = text, Location = new Point(x, y), Size = new Size(w, h),
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            if (primary)
            {
                b.BackColor = Blue; b.ForeColor = Color.White;
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = BlueHover;
                b.Font = new Font("Segoe UI Semibold", 11f);
            }
            else
            {
                b.BackColor = Field; b.ForeColor = Text;
                b.FlatAppearance.BorderColor = Border;
                b.FlatAppearance.MouseOverBackColor = FieldHover;
            }
            return b;
        }

        public static Label MakeLabel(string text, int x, int y, int w, int h)
        {
            return new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h), BackColor = Color.Transparent };
        }

        public static TextBox MakeLog(int x, int y, int w, int h)
        {
            return new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Location = new Point(x, y), Size = new Size(w, h), Font = new Font("Consolas", 9f),
                BackColor = Surface, BorderStyle = BorderStyle.FixedSingle };
        }

        // Sprachauswahl oben rechts. onChange wird nach dem Umstellen aufgerufen.
        public static ComboBox MakeLanguageBox(int right, int y, Action onChange)
        {
            var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
            cb.Location = new Point(right - cb.Width, y);
            cb.Items.AddRange(L.Names);
            cb.SelectedIndex = Array.IndexOf(L.Codes, L.Code);
            cb.SelectedIndexChanged += (s, e) =>
            {
                string neu = L.Codes[cb.SelectedIndex];
                if (neu == L.Code) return;
                L.Code = neu;
                onChange();
            };
            return cb;
        }
    }

    // ------------------------------------------------------------------
    // Logo: Neon-Pinguin. Die Bilder stecken als Ressourcen in der exe
    // (Quelle: logo\*.svg, gerendert mit logo\render.ps1).
    // ------------------------------------------------------------------
    static class Logo
    {
        public static readonly Color HeaderLeft = Color.FromArgb(31, 22, 92);    // #1F165C
        public static readonly Color HeaderRight = Color.FromArgb(10, 9, 30);    // #0A091E
        public static readonly Color Cyan = Color.FromArgb(34, 228, 255);        // #22E4FF
        public static readonly Color Pink = Color.FromArgb(255, 61, 154);        // #FF3D9A

        static Image lockup;

        // Schriftzug (Pinguin + "FreshGuest") für dunklen Hintergrund
        public static Image Lockup()
        {
            if (lockup != null) return lockup;
            using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("FreshGuest-Schriftzug.png"))
            {
                if (st == null) return null;
                using (Image tmp = Image.FromStream(st)) lockup = new Bitmap(tmp);
            }
            return lockup;
        }

        // Dunkle Kopfzeile mit Schriftzug und Neon-Linie unten. Rückgabe: rechter Rand des Schriftzugs.
        public static Panel MakeHeader(Form f, int height, out int lockupRight)
        {
            var p = new HeaderPanel { Location = Point.Empty, Size = new Size(f.ClientSize.Width, height) };
            lockupRight = 18;
            Image img = Lockup();
            if (img != null)
            {
                int h = height - 26;
                int w = (int)Math.Round(h * (double)img.Width / img.Height);
                p.Controls.Add(new PictureBox { Image = img, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent,
                    Location = new Point(16, (height - h) / 2 - 1), Size = new Size(w, h) });
                lockupRight = 16 + w;
            }
            f.Controls.Add(p);
            p.SendToBack();
            return p;
        }

        class HeaderPanel : Panel
        {
            public HeaderPanel() { DoubleBuffered = true; }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                Rectangle r = ClientRectangle;
                if (r.Width <= 0 || r.Height <= 0) return;
                using (var br = new LinearGradientBrush(r, HeaderLeft, HeaderRight, 0f)) e.Graphics.FillRectangle(br, r);
                var line = new Rectangle(0, r.Height - 3, r.Width, 3);
                using (var br = new LinearGradientBrush(new Rectangle(0, 0, r.Width, 3), Cyan, Pink, 0f))
                    e.Graphics.FillRectangle(br, line);
            }
        }
    }

    // Häkchen-Feld im dunklen Stil (das Windows-Kästchen bleibt sonst weiß)
    class DarkCheckBox : CheckBox
    {
        public DarkCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        float K { get { return DeviceDpi / 96f; } }

        public override Size GetPreferredSize(Size proposed)
        {
            Size t = TextRenderer.MeasureText(Text, Font);
            return new Size(t.Width + (int)(28 * K), Math.Max((int)(24 * K), t.Height + 4));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            float k = K, s = 17 * k;
            var box = new RectangleF(1 * k, (Height - s) / 2f, s, s);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = Checked ? (Enabled ? Ui.Blue : Ui.Border) : Ui.Field;
            Color edge = Focused ? Ui.Link : (Checked ? fill : (Enabled ? Ui.Muted : Ui.Border));
            using (GraphicsPath p = Ui.RoundRect(box, 3.5f * k))
            {
                using (var br = new SolidBrush(fill)) g.FillPath(br, p);
                using (var pen = new Pen(edge, 1.5f * k)) g.DrawPath(pen, p);
            }
            if (Checked)
                using (var pen = new Pen(Color.White, 2.2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, new[] { new PointF(box.X + 4.2f * k, box.Y + 8.8f * k), new PointF(box.X + 7.4f * k, box.Y + 12f * k),
                                             new PointF(box.X + 13f * k, box.Y + 5.4f * k) });
            g.SmoothingMode = SmoothingMode.Default;
            int tx = (int)(box.Right + 9 * k);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(tx, 0, Width - tx, Height), Enabled ? ForeColor : Ui.Disabled,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    // Navigation links im dunklen Stil (die Windows-Reiter lassen sich nicht dunkel färben)
    class DarkTabs : Control
    {
        readonly List<Panel> pages = new List<Panel>();
        readonly List<string> titles = new List<string>();
        int sel, hot = -1;
        const int Side = 176, ItemH = 44;

        public DarkTabs()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
        }

        public Panel Add(string title)
        {
            var p = new Panel { Location = new Point(Side, 0), Size = new Size(Width - Side, Height),
                BackColor = Ui.Surface, Visible = pages.Count == sel };
            pages.Add(p);
            titles.Add(title);
            Controls.Add(p);
            Invalidate();
            return p;
        }

        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                sel = Math.Max(0, Math.Min(pages.Count - 1, value));
                for (int i = 0; i < pages.Count; i++) pages[i].Visible = i == sel;
                Invalidate();
            }
        }

        // Einträge links; Breite = linke Kante der Seiten (passt sich der Bildschirm-Skalierung an)
        Rectangle ItemRect(int i)
        {
            int w = pages.Count > 0 ? pages[0].Left : Side, h = (int)(ItemH * DeviceDpi / 96f);
            return new Rectangle(0, i * h, w - (int)(8 * DeviceDpi / 96f), h);
        }

        int HitTest(Point pt)
        {
            for (int i = 0; i < titles.Count; i++) if (ItemRect(i).Contains(pt)) return i;
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            int bar = Math.Max(3, (int)(4 * DeviceDpi / 96f)), pad = (int)(16 * DeviceDpi / 96f);
            using (var bold = new Font(Font, FontStyle.Bold))
                for (int i = 0; i < titles.Count; i++)
                {
                    Rectangle r = ItemRect(i);
                    bool on = i == sel;
                    if (on || i == hot)
                        using (var br = new SolidBrush(on ? Ui.Surface : Ui.Field)) g.FillRectangle(br, r);
                    if (on)
                        using (var br = new LinearGradientBrush(new Rectangle(r.X, r.Y, bar, r.Height), Logo.Cyan, Logo.Pink, 90f))
                            g.FillRectangle(br, r.X, r.Y, bar, r.Height);
                    var tr = new Rectangle(r.X + pad, r.Y, r.Width - pad, r.Height);
                    TextRenderer.DrawText(g, titles[i], on ? bold : Font, tr, on ? Ui.Text : Ui.Muted,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.Location);
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
            if (h != hot) { hot = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hot != -1) { hot = -1; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int h = HitTest(e.Location);
            if (h >= 0) SelectedIndex = h;
        }
    }

    // Programm-Symbol: das Icon der exe (Neon-Pinguin, beim Bauen eingesetzt)
    static class AppIcon
    {
        static Icon icon;
        public static Icon Get()
        {
            if (icon != null) return icon;
            try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { icon = SystemIcons.Application; }
            return icon;
        }
    }

    // ------------------------------------------------------------------
    // Kennwort-Dialog
    // ------------------------------------------------------------------
    class PasswordForm : Form
    {
        readonly TextBox t1;
        readonly TextBox t2;
        readonly Label err;
        readonly bool confirm;

        PasswordForm(string title, string prompt, bool confirm)
        {
            this.confirm = confirm;
            Ui.Style(this, 380, confirm ? 214 : 156);
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;

            Controls.Add(Ui.MakeLabel(prompt, 16, 14, 350, 22));
            t1 = new TextBox { Location = new Point(18, 40), Width = 342, UseSystemPasswordChar = true };
            Controls.Add(t1);
            int y = 76;
            if (confirm)
            {
                Controls.Add(Ui.MakeLabel(L.T("Kennwort wiederholen:", "Ripeti password:", "Repeat password:"), 16, 74, 350, 22));
                t2 = new TextBox { Location = new Point(18, 100), Width = 342, UseSystemPasswordChar = true };
                Controls.Add(t2);
                y = 136;
            }
            err = Ui.MakeLabel("", 16, y, 350, 22);
            err.ForeColor = Ui.Red;
            Controls.Add(err);

            Button ok = Ui.MakeButton("OK", 160, y + 30, 96, 34, true);
            Button cancel = Ui.MakeButton(L.T("Abbrechen", "Annulla", "Cancel"), 264, y + 30, 96, 34, false);
            cancel.DialogResult = DialogResult.Cancel;
            ok.Click += (s, e) => OnOk();
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        void OnOk()
        {
            if (confirm)
            {
                if (t1.Text.Length < 4) { err.Text = L.T("Bitte mindestens 4 Zeichen.", "Almeno 4 caratteri.", "At least 4 characters."); return; }
                if (t1.Text != t2.Text) { err.Text = L.T("Die Kennwörter stimmen nicht überein.", "Le password non coincidono.", "The passwords do not match."); return; }
            }
            else if (t1.Text.Length == 0) { err.Text = L.T("Bitte Kennwort eingeben.", "Inserire la password.", "Please enter the password."); return; }
            DialogResult = DialogResult.OK;
            Close();
        }

        public static string PromptText
        {
            get { return L.T("Kennwort für FreshGuest:", "Password di FreshGuest:", "FreshGuest password:"); }
        }

        public static string Ask(IWin32Window owner, string title, string prompt)
        {
            using (var f = new PasswordForm(title, prompt, false))
                return f.ShowDialog(owner) == DialogResult.OK ? f.t1.Text : null;
        }

        public static string AskNew(IWin32Window owner, string title)
        {
            using (var f = new PasswordForm(title, L.T("Neues Kennwort (mindestens 4 Zeichen):",
                    "Nuova password (almeno 4 caratteri):", "New password (at least 4 characters):"), true))
                return f.ShowDialog(owner) == DialogResult.OK ? f.t1.Text : null;
        }
    }

    // ------------------------------------------------------------------
    // Einrichtung beim ersten Start
    // ------------------------------------------------------------------
    class SetupForm : Form
    {
        public bool OpenMainAfter;
        public bool SwitchLanguage;
        readonly Store store;
        readonly TextBox pw1, pw2, log;
        readonly CheckBox chkEnable;
        readonly Button btnInstall, btnCancel;
        readonly ComboBox cbLang;
        readonly Label hint;

        public SetupForm(Store store)
        {
            this.store = store;
            Ui.Style(this, 560, 674);
            Text = L.AppName + " " + Program.Version + " – " + L.T("Einrichtung", "Installazione", "Setup") +
                (Program.ShowDemoHints ? "   [DEMO – " + L.T("ändert nichts", "non modifica nulla", "changes nothing") + "]" : "");

            int lockupRight;
            Panel header = Logo.MakeHeader(this, 78, out lockupRight);
            var title = Ui.MakeLabel(L.T("Einrichtung", "Installazione", "Setup"), lockupRight + 14, 25, 160, 28);
            title.Font = new Font("Segoe UI Semibold", 13f);
            title.ForeColor = Color.FromArgb(200, 192, 255);
            header.Controls.Add(title);
            cbLang = Ui.MakeLanguageBox(540, 26, () => { store.Set("Sprache", L.Code); SwitchLanguage = true; Close(); });
            header.Controls.Add(cbLang);

            Controls.Add(Ui.MakeLabel(L.T(
                "Dieses Programm richtet den PC für Feriengäste ein:\r\n" +
                "•  Auf dem Anmeldebildschirm gibt es „Gast“ – ohne Kennwort.\r\n" +
                "•  Beim Abmelden oder Neustart wird alles vom Gast gelöscht.\r\n" +
                "•  Dein eigenes Konto und deine Daten bleiben erhalten.\r\n" +
                "•  Ein- und Ausschalten nur mit Kennwort.",
                "Questo programma prepara il PC per gli ospiti:\r\n" +
                "•  Nella schermata di accesso c'è «Ospite» – senza password.\r\n" +
                "•  Alla disconnessione o al riavvio tutto dell'ospite viene cancellato.\r\n" +
                "•  Il tuo account e i tuoi dati restano.\r\n" +
                "•  Attivare e disattivare solo con password.",
                "This program prepares the PC for holiday guests:\r\n" +
                "•  The sign-in screen offers \"Guest\" – no password.\r\n" +
                "•  At sign-out or restart everything from the guest is deleted.\r\n" +
                "•  Your own account and data stay.\r\n" +
                "•  Turning on and off only with a password."), 22, 92, 520, 100));

            // Prüfungen
            var lnkGuide = new LinkLabel { Text = L.T("Anleitung öffnen", "Apri la guida", "Open guide"), Location = new Point(340, 202),
                Size = new Size(200, 22), TextAlign = ContentAlignment.TopRight, LinkColor = Ui.Link };
            lnkGuide.LinkClicked += (s, e) => Guide.Open(this);
            Controls.Add(lnkGuide);
            var checksTitle = Ui.MakeLabel(L.T("Prüfung", "Verifica", "Checks"), 20, 200, 300, 24);
            checksTitle.Font = new Font("Segoe UI Semibold", 11f);
            Controls.Add(checksTitle);

            bool editionOk;
            string edition = Sys.EditionText(out editionOk);
            bool admin = Sys.IsAdmin();

            int y = 228;
            AddCheck(ref y, admin || Program.Demo,
                (admin || Program.CleanShots) ? L.T("Administrator-Rechte vorhanden", "Diritti di amministratore presenti", "Administrator rights present")
                      : L.T("Demo-Modus (keine Administrator-Rechte nötig)", "Modalità demo (non servono diritti di amministratore)", "Demo mode (no administrator rights needed)"));
            AddCheck(ref y, editionOk, edition + (editionOk ? "" : L.T(" – Home wird nicht unterstützt (Pro nötig)",
                " – Home non è supportato (serve Pro)", " – Home is not supported (Pro needed)")));
            bool blankPw = !Program.Demo && Sys.BlankPassword();
            AddCheck(ref y, !blankPw, blankPw
                ? L.T("Dein Windows-Konto hat kein Kennwort", "Il tuo account Windows non ha una password", "Your Windows account has no password")
                : L.T("Dein Windows-Konto hat ein Kennwort", "Il tuo account Windows ha una password", "Your Windows account has a password"));

            hint = Ui.MakeLabel("", 22, y + 2, 520, 0);
            hint.ForeColor = Ui.Red;
            if (blankPw)
            {
                hint.Text = L.T("Bitte zuerst ein Kennwort festlegen: Einstellungen → Konten → Anmeldeoptionen → Kennwort. " +
                                "Sonst kann jeder Gast über „Anderer Benutzer“ in dein Konto. Danach dieses Programm neu starten.",
                                "Impostare prima una password: Impostazioni → Account → Opzioni di accesso → Password. " +
                                "Altrimenti ogni ospite può entrare nel tuo account con «Altro utente». Poi riavviare questo programma.",
                                "Please set a password first: Settings → Accounts → Sign-in options → Password. " +
                                "Otherwise any guest can get into your account via \"Other user\". Then restart this program.");
                hint.Height = 58;
            }
            Controls.Add(hint);
            y += hint.Height + 10;

            // Kennwort
            var pwTitle = Ui.MakeLabel(L.T("Kennwort für FreshGuest", "Password per FreshGuest", "Password for FreshGuest"), 20, y, 520, 24);
            pwTitle.Font = new Font("Segoe UI Semibold", 11f);
            Controls.Add(pwTitle);
            y += 28;
            Controls.Add(Ui.MakeLabel(L.T("Kennwort:", "Password:", "Password:"), 22, y + 3, 140, 22));
            pw1 = new TextBox { Location = new Point(170, y), Width = 370, UseSystemPasswordChar = true };
            Controls.Add(pw1);
            y += 34;
            Controls.Add(Ui.MakeLabel(L.T("Wiederholen:", "Ripeti:", "Repeat:"), 22, y + 3, 140, 22));
            pw2 = new TextBox { Location = new Point(170, y), Width = 370, UseSystemPasswordChar = true };
            Controls.Add(pw2);
            y += 40;

            chkEnable = new DarkCheckBox { Text = L.T("Schutz sofort einschalten", "Attiva subito la protezione", "Turn on protection now"),
                Checked = true, Location = new Point(22, y), AutoSize = true };
            Controls.Add(chkEnable);
            y += 28;
            // Kein „danach neu starten“: Erst kommen die Einstellungen für Gäste, dann der Neustart (im Hauptfenster)
            var next = Ui.MakeLabel(L.T("Danach: Einstellungen für Gäste machen – erst dann neu starten.",
                "Poi: fare le impostazioni per gli ospiti – solo dopo riavviare.",
                "Afterwards: set up the guest settings – only then restart."), 42, y + 2, 498, 22);
            next.ForeColor = Ui.Muted;
            Controls.Add(next);
            y += 38;

            btnInstall = Ui.MakeButton(L.T("Installieren", "Installa", "Install"), 300, y, 240, 44, true);
            btnCancel = Ui.MakeButton(L.T("Abbrechen", "Annulla", "Cancel"), 170, y, 120, 44, false);
            btnInstall.Enabled = editionOk && !blankPw && (admin || Program.Demo);
            if (!btnInstall.Enabled) btnInstall.BackColor = Ui.Field;
            btnInstall.Click += OnInstall;
            btnCancel.Click += (s, e) => Close();
            Controls.Add(btnInstall);
            Controls.Add(btnCancel);
            AcceptButton = btnInstall;
            y += 56;

            log = Ui.MakeLog(20, y, 520, 674 - y - 16);
            Controls.Add(log);
        }

        void AddCheck(ref int y, bool ok, string text)
        {
            var mark = Ui.MakeLabel(ok ? "✔" : "✖", 22, y, 24, 24);
            mark.ForeColor = ok ? Ui.OkText : Ui.Red;
            mark.Font = new Font("Segoe UI Symbol", 11f, FontStyle.Bold);
            Controls.Add(mark);
            Controls.Add(Ui.MakeLabel(text, 48, y + 1, 490, 24));
            y += 28;
        }

        void Log(string line)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), line); return; }
            log.AppendText(line + Environment.NewLine);
        }

        async void OnInstall(object sender, EventArgs e)
        {
            if (pw1.Text.Length < 4) { Warn(L.T("Bitte ein Kennwort mit mindestens 4 Zeichen eingeben.", "Inserire una password di almeno 4 caratteri.", "Please enter a password with at least 4 characters.")); return; }
            if (pw1.Text != pw2.Text) { Warn(L.T("Die beiden Kennwörter stimmen nicht überein.", "Le due password non coincidono.", "The two passwords do not match.")); return; }

            btnInstall.Enabled = btnCancel.Enabled = pw1.Enabled = pw2.Enabled = false;
            chkEnable.Enabled = cbLang.Enabled = false;
            UseWaitCursor = true;
            try
            {
                Log(L.T("Installiere Programm ...", "Installo il programma ...", "Installing program ..."));
                Sys.InstallFiles(Log);
                Pwd.Set(store, pw1.Text);
                store.Set("Installiert", "1");
                store.Set("Version", Program.Version);
                store.Set("SchutzAn", "0");
                store.Set("Sprache", L.Code);
                store.Save();
                Log(L.T("Kennwort gespeichert.", "Password salvata.", "Password saved."));

                int code = 0;
                if (chkEnable.Checked)
                {
                    Log(L.T("Schalte Schutz ein (kann 1–2 Minuten dauern) ...", "Attivo la protezione (può durare 1–2 minuti) ...", "Turning on protection (may take 1–2 minutes) ..."));
                    code = await Sys.RunScript("Einrichten", Log);
                    if (code == 0)
                    {
                        store.MarkChanged(true);
                        store.Save();
                        try { SignIn.Sync(store, Log); } catch (Exception ex) { Log(ex.Message); }
                    }
                }

                // Protokolle automatisch speichern: bei Start vom USB-Stick auf den Stick, sonst in den Installationsordner
                Log(L.T("Speichere Protokolle ...", "Salvo i log ...", "Saving logs ..."));
                string logDir = await SaveLogsNextToExe();

                UseWaitCursor = false;
                if (code != 0)
                {
                    MessageBox.Show(this, L.T("Das Programm ist installiert, aber der Schutz konnte nicht eingeschaltet werden.\n\nDu kannst es im Programm erneut versuchen.",
                                              "Il programma è installato, ma non è stato possibile attivare la protezione.\n\nPuoi riprovare dal programma.",
                                              "The program is installed, but protection could not be turned on.\n\nYou can try again in the program.") +
                        (logDir != null ? "\n\n" + L.T("Protokolle gespeichert in:", "Log salvati in:", "Logs saved to:") + "\n" + logDir : ""),
                        L.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    OpenMainAfter = true;
                    Close();
                    return;
                }
                MessageBox.Show(this, chkEnable.Checked
                    ? L.T("Fertig! Der Schutz wird nach dem nächsten Neustart aktiv.\n\n" +
                          "Als Nächstes: auf den großen Knopf „⚙ Einstellungen für Gäste“ klicken und alles einstellen. " +
                          "Erst danach auf „Jetzt neu starten“ klicken.",
                          "Fatto! La protezione sarà attiva dopo il prossimo riavvio.\n\n" +
                          "Prossimo passo: clicca sul grande pulsante «⚙ Impostazioni per gli ospiti» e imposta tutto. " +
                          "Solo dopo clicca su «Riavvia ora».",
                          "Done! Protection becomes active after the next restart.\n\n" +
                          "Next: click the big \"⚙ Guest settings\" button and set everything up. " +
                          "Only then click \"Restart now\".")
                    : L.T("Fertig!", "Fatto!", "Done!"), L.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                OpenMainAfter = true;
                Close();
            }
            catch (Exception ex)
            {
                UseWaitCursor = false;
                Log(L.T("FEHLER: ", "ERRORE: ", "ERROR: ") + ex.Message);
                MessageBox.Show(this, L.T("Fehler bei der Einrichtung:", "Errore durante l'installazione:", "Error during setup:") + "\n\n" + ex.Message,
                    L.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                btnInstall.Enabled = btnCancel.Enabled = pw1.Enabled = pw2.Enabled = cbLang.Enabled = true;
                chkEnable.Enabled = true;
            }
        }

        async Task<string> SaveLogsNextToExe()
        {
            try
            {
                string dir = await Sys.ExportLogs(store, Sys.SetupLogRoot(), log.Text);
                Log(L.T("Protokolle gespeichert in: ", "Log salvati in: ", "Logs saved to: ") + dir);
                return dir;
            }
            catch (Exception ex)
            {
                Log(L.T("Protokolle konnten nicht gespeichert werden: ", "Impossibile salvare i log: ", "Logs could not be saved: ") + ex.Message);
                return null;
            }
        }

        void Warn(string text)
        {
            MessageBox.Show(this, text, L.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ------------------------------------------------------------------
    // Hintergrundbild und Autostart für Gäste
    // ------------------------------------------------------------------
    static partial class GuestExtras
    {
        // --- Hintergrundbild ---------------------------------------------------
        // Jeder Gast bekommt ein NEUES Konto, das aus dem Windows-Standardprofil
        // (C:\Users\Default) erzeugt wird. Dort wird die Hintergrund-Richtlinie
        // eingetragen -> gilt für jeden neuen Gast, NICHT für bestehende Konten (dich).
        const string HiveMount = "FreshGuest_Default";
        const string PolicyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
        public static readonly string[] StyleCodes = { "4", "3", "2", "0" };   // Füllen, Anpassen, Strecken, Zentriert

        public static string[] StyleNames()
        {
            return new[] { L.T("Füllen", "Riempi", "Fill"), L.T("Anpassen", "Adatta", "Fit"),
                           L.T("Strecken", "Estendi", "Stretch"), L.T("Zentriert", "Al centro", "Center") };
        }

        static string DefaultHivePath
        {
            get
            {
                string dir = null;
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"))
                    if (k != null) dir = Convert.ToString(k.GetValue("Default", "", RegistryValueOptions.DoNotExpandEnvironmentNames));
                if (string.IsNullOrEmpty(dir)) dir = @"%SystemDrive%\Users\Default";
                return Path.Combine(Environment.ExpandEnvironmentVariables(dir), "NTUSER.DAT");
            }
        }

        public static void WithDefaultHive(Action<RegistryKey> action)
        {
            string hive = DefaultHivePath;
            if (!File.Exists(hive)) throw new FileNotFoundException(hive);
            int rc = Sys.RunQuiet("reg.exe", "load \"HKU\\" + HiveMount + "\" \"" + hive + "\"");
            if (rc != 0)
                throw new InvalidOperationException(L.T("Windows-Standardprofil konnte nicht geöffnet werden",
                    "Impossibile aprire il profilo predefinito di Windows", "Could not open the Windows default profile") + " (reg load " + rc + ").");
            try
            {
                using (RegistryKey root = Registry.Users.OpenSubKey(HiveMount, true)) action(root);
            }
            finally
            {
                // Standardprofil unbedingt wieder freigeben, sonst können keine neuen Konten angelegt werden
                GC.Collect();
                GC.WaitForPendingFinalizers();
                bool ok = false;
                for (int i = 0; i < 8 && !ok; i++)
                {
                    ok = Sys.RunQuiet("reg.exe", "unload \"HKU\\" + HiveMount + "\"") == 0;
                    if (!ok) Thread.Sleep(500);
                }
                if (!ok)
                    throw new InvalidOperationException(L.T(
                        "Das Windows-Standardprofil konnte nicht wieder freigegeben werden. Bitte den PC neu starten, bevor sich ein Gast anmeldet.",
                        "Impossibile rilasciare il profilo predefinito di Windows. Riavviare il PC prima che un ospite acceda.",
                        "The Windows default profile could not be released. Please restart the PC before a guest signs in."));
            }
        }

        public static bool WallpaperOn(Store s) { return !string.IsNullOrEmpty(s.Get("Hintergrund")); }

        // Prüft, ob die Datei ein Bild ist, das Windows anzeigen kann
        public static bool IsImage(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".bmp") return false;
                using (Image img = Image.FromFile(path)) return img.Width > 0;
            }
            catch { return false; }
        }

        public static void ApplyWallpaper(Store store, string source, string style, Action<string> log)
        {
            string ext = Path.GetExtension(source).ToLowerInvariant();
            if (ext == ".jpeg") ext = ".jpg";
            string dest = Path.Combine(Sys.DataDir, "Hintergrund" + ext);
            if (Program.Demo)
            {
                log("DEMO: " + L.T("Hintergrundbild würde gesetzt: ", "lo sfondo verrebbe impostato: ", "wallpaper would be set: ") + source);
            }
            else
            {
                byte[] bytes = File.ReadAllBytes(source);      // zuerst lesen (Quelle kann die alte Kopie sein)
                Sys.SecureDataDir();
                foreach (string old in Directory.GetFiles(Sys.DataDir, "Hintergrund.*")) File.Delete(old);
                File.WriteAllBytes(dest, bytes);
                Sys.RunQuiet("icacls.exe", "\"" + dest + "\" /reset /Q");   // erbt vom Ordner: Benutzer dürfen lesen
                WithDefaultHive(root =>
                {
                    using (RegistryKey k = root.CreateSubKey(PolicyPath))
                    {
                        k.SetValue("Wallpaper", dest, RegistryValueKind.String);
                        k.SetValue("WallpaperStyle", style, RegistryValueKind.String);
                    }
                });
            }
            store.Set("Hintergrund", dest);
            store.Set("HintergrundStil", style);
            store.Save();
            log(L.T("✔ Hintergrundbild für Gäste gesetzt (gilt ab dem nächsten Gast).",
                    "✔ Sfondo per gli ospiti impostato (vale dal prossimo ospite).",
                    "✔ Guest wallpaper set (applies from the next guest)."));
        }

        public static void RemoveWallpaper(Store store, Action<string> log)
        {
            if (!Program.Demo)
            {
                WithDefaultHive(root =>
                {
                    using (RegistryKey k = root.OpenSubKey(PolicyPath, true))
                    {
                        if (k != null) { k.DeleteValue("Wallpaper", false); k.DeleteValue("WallpaperStyle", false); }
                    }
                });
                if (Directory.Exists(Sys.DataDir))
                    foreach (string old in Directory.GetFiles(Sys.DataDir, "Hintergrund.*"))
                        try { File.Delete(old); } catch { }
            }
            store.Remove("Hintergrund");
            store.Remove("HintergrundStil");
            store.Save();
            if (log != null) log(L.T("Hintergrundbild für Gäste entfernt (Windows-Standard).", "Sfondo per gli ospiti rimosso (predefinito di Windows).",
                                     "Guest wallpaper removed (Windows default)."));
        }

        // --- Autostart ---------------------------------------------------------
        // Verknüpfung im Autostart-Ordner für ALLE Benutzer -> "FreshGuest.exe --gast-start".
        // Das prüft beim Anmelden, ob es ein Gast ist (kein geschütztes Konto), und startet nur dann.
        const string ConfigKey = @"SOFTWARE\FreshGuest";     // für Gäste lesbar (Standardrechte: nur lesen)

        static string StartupLnk
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "FreshGuest-Gaststart.lnk"); }
        }

        public static bool AutostartOn(Store s) { return !string.IsNullOrEmpty(s.Get("Autostart")); }

        // Programm (.exe) oder Internet-Adresse prüfen. Rückgabe: bereinigtes Ziel oder null
        public static string NormalizeTarget(string s)
        {
            s = (s ?? "").Trim().Trim('"');
            if (s.Length == 0) return null;
            if (Regex.IsMatch(s, @"^[a-zA-Z]:\\") || s.StartsWith(@"\\"))
                return File.Exists(s) && s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s : null;
            return BrowserSetup.NormalizeUrl(s);
        }

        public static void ApplyAutostart(Store store, string target, Action<string> log)
        {
            if (Program.Demo)
            {
                log("DEMO: " + L.T("Autostart würde gesetzt: ", "l'avvio automatico verrebbe impostato: ", "autostart would be set: ") + target);
            }
            else
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(ConfigKey))
                    k.SetValue("GastAutostart", target, RegistryValueKind.String);
                UpdateStartupLink();
            }
            store.Set("Autostart", target);
            store.Save();
            log(L.T("✔ Autostart für Gäste: ", "✔ Avvio automatico per gli ospiti: ", "✔ Guest autostart: ") + target);
        }

        public static void RemoveAutostart(Store store, Action<string> log)
        {
            if (!Program.Demo)
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ConfigKey, true))
                    if (k != null) k.DeleteValue("GastAutostart", false);
                UpdateStartupLink();
            }
            store.Remove("Autostart");
            store.Save();
            if (log != null) log(L.T("Autostart für Gäste entfernt.", "Avvio automatico per gli ospiti rimosso.", "Guest autostart removed."));
        }

    }

    // ------------------------------------------------------------------
    // Anleitung (HTML, drei Sprachen) – steckt in der exe
    // ------------------------------------------------------------------
    static class Guide
    {
        public static void Open(IWin32Window owner)
        {
            try
            {
                string html;
                using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("FreshGuest-Anleitung.html"))
                {
                    if (st == null) throw new FileNotFoundException("FreshGuest-Anleitung.html");
                    using (var r = new StreamReader(st, Encoding.UTF8)) html = r.ReadToEnd();
                }
                html = html.Replace("data-start-lang=\"auto\"", "data-start-lang=\"" + L.Code + "\"");
                string file = Path.Combine(Path.GetTempPath(), "FreshGuest-Anleitung.html");
                File.WriteAllText(file, html, new UTF8Encoding(false));
                // über den Explorer öffnen -> Browser läuft OHNE Administrator-Rechte
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + file + "\"") { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, L.T("Die Anleitung konnte nicht geöffnet werden:", "Impossibile aprire la guida:",
                    "The guide could not be opened:") + "\n\n" + ex.Message, L.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    // ------------------------------------------------------------------
    // Einstellungen: Browser, Hintergrund, Autostart
    // ------------------------------------------------------------------
    class SettingsForm : Form
    {
        readonly Store store;
        readonly DarkTabs tabs;
        // Browser
        readonly CheckBox chkWelcome, chkPw, chkDefault, chkIcons, chkStart;
        readonly TextBox txtUrl;
        // Hintergrund
        readonly CheckBox chkWall;
        readonly TextBox txtImg;
        readonly ComboBox cbStyle;
        readonly PictureBox preview;
        readonly Button btnImg;
        // Autostart
        readonly CheckBox chkAuto;
        readonly TextBox txtAuto;
        readonly Button btnAuto, btnAutoUrl;

        // Ergebnis
        public BrowserOpt Options;
        public string Url;
        public bool WallChanged, WallOn, AutoChanged, AutoOn;
        public string WallSource, WallStyle, AutoTarget;
        public bool DeskChanged, SessionChanged, RestartChanged, LocksChanged, WelcomeOn;
        public bool SignInChanged, Preselect, GuestOnly, NoticeOn;
        public bool LangChanged, ChoiceChanged, ChoiceOn;
        public string GuestLang;
        CheckBox chkLang, chkChoice;
        ComboBox cbGuestLang;
        List<string> langTags;
        CheckBox chkNotice;
        CheckBox chkPreselect, chkGuestOnly;
        public List<DeskItem> DeskItems;
        public WelcomeInfo Welcome;
        public int IdleMin, Locks;
        public string RestartTime, Printer;
        public bool PicChanged, PicOn;
        public string PicSource;

        // Profilbild
        CheckBox chkPic;
        CirclePicture picPreview;
        Button btnPicFile, btnPicLogo;

        // Desktop
        DropListBox lstDesk;
        // Willkommen
        CheckBox chkWelcome2;
        TextBox txtWlan, txtWlanPw, txtCheckout, txtContact, txtText, txtExtraName, txtExtra;
        List<LangTab> langTabs = new List<LangTab>();
        Panel welcomePanel;
        Label lblEditLang;
        readonly ToolTip langTip = new ToolTip();
        WelcomeInfo editInfo;                 // alle Texte aller Sprachen, während das Fenster offen ist
        string editSlot = "de";               // Sprache, deren Felder gerade zu sehen sind
        bool loadingWelcome;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        Button btnPreview;
        // Abmelden & Neustart
        CheckBox chkIdle, chkRestart;
        ComboBox cbIdle, cbRestart;
        static readonly int[] IdleChoices = { 15, 30, 60, 120, 240 };
        // Sperren & Drucker
        CheckBox chkLockSettings, chkLockTools, chkLockInstall, chkLockDownloads, chkPrinter;
        ComboBox cbPrinter;
        Label lblPrinterNote;

        public SettingsForm(Store store, int startTab)
        {
            this.store = store;
            Ui.Style(this, 732, 532);
            Text = L.T("Einstellungen für Gäste", "Impostazioni per gli ospiti", "Guest settings");
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;

            tabs = new DarkTabs { Location = new Point(14, 14), Size = new Size(704, 444) };
            Panel tBrowser = tabs.Add(L.T("Browser", "Browser", "Browser"));
            Panel tWall = tabs.Add(L.T("Hintergrund", "Sfondo", "Wallpaper"));
            Panel tPic = tabs.Add(L.T("Profilbild", "Immagine profilo", "Profile picture"));
            Panel tDesk = tabs.Add(L.T("Desktop", "Desktop", "Desktop"));
            Panel tAuto = tabs.Add(L.T("Autostart", "Avvio automatico", "Autostart"));
            Panel tWelcome = tabs.Add(L.T("Willkommen", "Benvenuto", "Welcome"));
            Panel tTimer = tabs.Add(L.T("Automatik", "Automatismi", "Automatic"));
            Panel tLock = tabs.Add(L.T("Sperren & Drucker", "Blocchi e stampante", "Locks & printer"));
            Panel tSignIn = tabs.Add(L.T("Anmeldung", "Accesso", "Sign-in"));
            Panel tLang = tabs.Add(L.T("Sprache", "Lingua", "Language"));
            Controls.Add(tabs);
            BuildSignIn(tSignIn);
            BuildLanguage(tLang);

            // ---------- Browser ----------
            AddInfo(tBrowser, L.T(
                "Gilt nur für Gäste (Edge, Chrome, Firefox), ab dem nächsten Gast. Dein eigenes Konto bleibt unverändert.",
                "Vale solo per gli ospiti (Edge, Chrome, Firefox), dal prossimo ospite. Il tuo account resta invariato.",
                "Applies to guests only (Edge, Chrome, Firefox), from the next guest. Your own account stays unchanged."), 12, 44);
            AddInfo(tBrowser, L.T("Gefunden: ", "Trovati: ", "Found: ") + BrowserSetup.Found(), 58, 22);
            BrowserOpt cur = BrowserSetup.Current(store);
            bool neu = store.Get("BrowserOptionen") == null;
            chkWelcome = AddCheck(tBrowser, L.T("Kein Willkommensassistent beim ersten Start", "Nessuna procedura di benvenuto al primo avvio",
                "No welcome wizard on first start"), 88, neu || (cur & BrowserOpt.NoWelcome) != 0);
            chkPw = AddCheck(tBrowser, L.T("Kennwörter nicht speichern (keine Nachfrage)", "Non salvare le password (nessuna richiesta)",
                "Don't save passwords (no prompt)"), 118, neu || (cur & BrowserOpt.NoPasswords) != 0);
            chkDefault = AddCheck(tBrowser, L.T("Nicht fragen „Als Standardbrowser festlegen?“", "Non chiedere «Imposta come browser predefinito?»",
                "Don't ask \"Make default browser?\""), 148, neu || (cur & BrowserOpt.NoDefaultPrompt) != 0);
            chkIcons = AddCheck(tBrowser, L.T("Browser-Symbole auf den Desktop der Gäste", "Icone dei browser sul desktop degli ospiti",
                "Browser icons on the guests' desktop"), 178, neu || (cur & BrowserOpt.Shortcuts) != 0);
            chkStart = AddCheck(tBrowser, L.T("Feste Startseite (eigenen Link eintippen):", "Pagina iniziale fissa (inserisci il tuo link):",
                "Fixed start page (type your own link):"), 208, neu || (cur & BrowserOpt.StartPage) != 0);
            txtUrl = new TextBox { Location = new Point(34, 238), Width = 470, Text = store.Get("Startseite") ?? "https://www.google.it/" };
            tBrowser.Controls.Add(txtUrl);
            chkStart.CheckedChanged += (s, e) => txtUrl.Enabled = chkStart.Checked;
            txtUrl.Enabled = chkStart.Checked;
            var note = AddInfo(tBrowser, L.T(
                "Edge und Chrome öffnen die Startseite über ihr Symbol „Internet (…)“ auf dem Desktop. Firefox bekommt kein Symbol (bleibt in einer Sprache).",
                "Edge e Chrome aprono la pagina iniziale tramite la loro icona «Internet (…)» sul desktop. Firefox non riceve icone (resta in una sola lingua).",
                "Edge and Chrome open the start page via their \"Internet (…)\" desktop icon. Firefox gets no icon (it stays in one language)."), 268, 40);
            note.Left = 32; note.Width = 474;
            AddInfo(tBrowser, L.T("Alle Häkchen weg + Übernehmen = Browser-Einstellungen entfernen.",
                "Togli tutte le spunte + Applica = rimuovi le impostazioni del browser.",
                "Untick everything + Apply = remove the browser settings."), 322, 22);

            // ---------- Hintergrund ----------
            AddInfo(tWall, L.T(
                "Eigenes Bild als Desktop-Hintergrund für jeden neuen Gast, z. B. ein Foto deiner Ferienwohnung mit WLAN-Name. Dein eigenes Konto bleibt unverändert.",
                "Un'immagine propria come sfondo per ogni nuovo ospite, ad es. una foto del tuo appartamento con il nome del Wi-Fi. Il tuo account resta invariato.",
                "Your own picture as desktop wallpaper for every new guest, e.g. a photo of your apartment with the Wi-Fi name. Your own account stays unchanged."), 12, 62);
            chkWall = AddCheck(tWall, L.T("Eigenes Hintergrundbild für Gäste", "Sfondo personalizzato per gli ospiti", "Custom wallpaper for guests"), 80,
                GuestExtras.WallpaperOn(store));
            txtImg = new TextBox { Location = new Point(34, 110), Width = 330, ReadOnly = true,
                Text = store.Get("Hintergrund") ?? "" };
            btnImg = Ui.MakeButton(L.T("Bild wählen …", "Scegli immagine …", "Choose picture …"), 372, 108, 132, 28, false);
            btnImg.Click += (s, e) => PickImage();
            tWall.Controls.Add(txtImg);
            tWall.Controls.Add(btnImg);
            var lblStyle = Ui.MakeLabel(L.T("Anordnung:", "Posizione:", "Position:"), 32, 147, 110, 22);
            tWall.Controls.Add(lblStyle);
            cbStyle = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(146, 144), Width = 150 };
            cbStyle.Items.AddRange(GuestExtras.StyleNames());
            int si = Array.IndexOf(GuestExtras.StyleCodes, store.Get("HintergrundStil") ?? "4");
            cbStyle.SelectedIndex = si < 0 ? 0 : si;
            tWall.Controls.Add(cbStyle);
            preview = new PictureBox { Location = new Point(34, 182), Size = new Size(256, 144), SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Ui.Field, BorderStyle = BorderStyle.FixedSingle };
            tWall.Controls.Add(preview);
            var wallNote = AddInfo(tWall, L.T("JPG, PNG oder BMP. Gilt ab der nächsten Gast-Anmeldung.",
                "JPG, PNG o BMP. Vale dal prossimo accesso di un ospite.", "JPG, PNG or BMP. Applies from the next guest sign-in."), 182, 60);
            wallNote.Left = 302; wallNote.Width = 204;
            LoadPreview(txtImg.Text);
            chkWall.CheckedChanged += (s, e) => UpdateWallEnabled();
            UpdateWallEnabled();

            // ---------- Autostart ----------
            AddInfo(tAuto, L.T(
                "Startet ein Programm oder eine Internetseite automatisch, sobald sich ein Gast anmeldet – z. B. deine Willkommensseite. Bei deinem eigenen Konto startet nichts.",
                "Avvia automaticamente un programma o una pagina internet quando un ospite accede – ad es. la tua pagina di benvenuto. Con il tuo account non parte nulla.",
                "Starts a program or web page automatically when a guest signs in – e.g. your welcome page. Nothing starts for your own account."), 12, 62);
            chkAuto = AddCheck(tAuto, L.T("Beim Anmelden eines Gastes öffnen:", "All'accesso di un ospite apri:", "When a guest signs in, open:"), 80,
                GuestExtras.AutostartOn(store));
            txtAuto = new TextBox { Location = new Point(34, 110), Width = 470, Text = store.Get("Autostart") ?? "" };
            tAuto.Controls.Add(txtAuto);
            btnAuto = Ui.MakeButton(L.T("Programm wählen …", "Scegli programma …", "Choose program …"), 34, 144, 170, 30, false);
            btnAuto.Click += (s, e) => PickProgram();
            btnAutoUrl = Ui.MakeButton(L.T("Startseite übernehmen", "Usa pagina iniziale", "Use start page"), 212, 144, 170, 30, false);
            btnAutoUrl.Click += (s, e) => { txtAuto.Text = txtUrl.Text; };
            tAuto.Controls.Add(btnAuto);
            tAuto.Controls.Add(btnAutoUrl);
            AddInfo(tAuto, L.T("Beispiele:", "Esempi:", "Examples:") + "\r\n   " +
                L.T("https://www.meine-ferienwohnung.it", "https://www.mio-appartamento.it", "https://www.my-holiday-flat.com") +
                "\r\n   C:\\Program Files\\VideoLAN\\VLC\\vlc.exe", 186, 64);
            chkAuto.CheckedChanged += (s, e) => UpdateAutoEnabled();
            UpdateAutoEnabled();

            BuildPicture(tPic);
            BuildDesktop(tDesk);
            BuildWelcome(tWelcome);
            BuildTimer(tTimer);
            BuildLocks(tLock);

            // ---------- Knöpfe ----------
            Button ok = Ui.MakeButton(L.T("Übernehmen", "Applica", "Apply"), 548, 472, 170, 42, true);
            Button cancel = Ui.MakeButton(L.T("Abbrechen", "Annulla", "Cancel"), 418, 472, 120, 42, false);
            cancel.DialogResult = DialogResult.Cancel;
            ok.Click += (s, e) => OnOk();
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            tabs.SelectedIndex = startTab;
        }

        Label AddInfo(Control parent, string text, int y, int h)
        {
            var l = Ui.MakeLabel(text, 12, y, 494, h);
            l.ForeColor = Ui.Muted;
            parent.Controls.Add(l);
            return l;
        }

        CheckBox AddCheck(Control parent, string text, int y, bool on)
        {
            var c = new DarkCheckBox { Text = text, Checked = on, Location = new Point(14, y), AutoSize = true };
            parent.Controls.Add(c);
            return c;
        }

        Label Indent(Label l, int left)
        {
            l.Width = l.Right - left;
            l.Left = left;
            return l;
        }

        // ---------- Profilbild ----------
        void BuildPicture(Panel t)
        {
            AddInfo(t, L.T(
                "Eigenes Profilbild für jeden Gast – im Startmenü, sobald der Gast angemeldet ist. Zum Beispiel ein Foto deiner Ferienwohnung.",
                "Un'immagine del profilo per ogni ospite – nel menu Start, appena l'ospite ha effettuato l'accesso. Ad esempio una foto del tuo appartamento.",
                "Your own profile picture for every guest – in the Start menu once the guest is signed in. For example a photo of your holiday flat."), 12, 44);
            chkPic = AddCheck(t, L.T("Eigenes Profilbild für Gäste", "Immagine del profilo personalizzata per gli ospiti", "Custom profile picture for guests"),
                62, GuestExtras.PictureOn(store));
            PicSource = store.Get("Profilbild") ?? (Brand.HasPicture ? GuestExtras.BrandPic : GuestExtras.LogoPic);   // Vorschlag: eigenes Logo bzw. Pinguin
            picPreview = new CirclePicture { Location = new Point(34, 98), Size = new Size(160, 160) };
            t.Controls.Add(picPreview);
            btnPicFile = Ui.MakeButton(L.T("Bild wählen …", "Scegli immagine …", "Choose picture …"), 214, 108, 180, 32, false);
            btnPicFile.Click += (s, e) => PickPicture();
            btnPicLogo = Ui.MakeButton(L.T("Pinguin-Logo", "Logo pinguino", "Penguin logo"), 214, 148, 180, 32, false);
            btnPicLogo.Click += (s, e) => { PicSource = GuestExtras.LogoPic; ShowPicture(); };
            t.Controls.Add(btnPicFile);
            t.Controls.Add(btnPicLogo);
            int noteY = 192;
            if (Brand.HasPicture)        // persönliche Fassung: eigenes Logo zur Wahl
            {
                btnPicBrand = Ui.MakeButton(Brand.PictureName, 214, 188, 180, 32, false);
                btnPicBrand.Click += (s, e) => { PicSource = GuestExtras.BrandPic; ShowPicture(); };
                t.Controls.Add(btnPicBrand);
                noteY = 228;
            }
            var note = AddInfo(t, L.T("Ein Foto wird in der Mitte quadratisch zugeschnitten.", "Una foto viene ritagliata al centro in formato quadrato.",
                "A photo is cropped to a square in the middle."), noteY, 44);
            note.Left = 212; note.Width = 294;
            AddInfo(t, L.T(
                "Dein eigenes Konto bleibt unverändert. Das Bild erscheint auch auf der Kachel „Guest“ am Anmeldebildschirm (statt der Figur mit dem Fragezeichen).",
                "Il tuo account resta invariato. L'immagine compare anche sul riquadro «Guest» nella schermata di accesso (al posto della figura con il punto interrogativo).",
                "Your own account stays unchanged. The picture also appears on the \"Guest\" tile on the sign-in screen (instead of the figure with the question mark)."), 278, 44);
            chkPic.CheckedChanged += (s, e) => UpdatePicEnabled();
            ShowPicture();
            UpdatePicEnabled();
        }

        Button btnPicBrand;
        void UpdatePicEnabled()
        {
            picPreview.Enabled = btnPicFile.Enabled = btnPicLogo.Enabled = chkPic.Checked;
            if (btnPicBrand != null) btnPicBrand.Enabled = chkPic.Checked;
        }

        void ShowPicture()
        {
            try { picPreview.Image = GuestExtras.LoadPicture(PicSource); }
            catch { picPreview.Image = null; }
        }

        void PickPicture()
        {
            using (var d = new OpenFileDialog())
            {
                d.Title = L.T("Profilbild wählen", "Scegli immagine del profilo", "Choose profile picture");
                d.Filter = L.T("Bilder", "Immagini", "Pictures") + " (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!GuestExtras.IsImage(d.FileName))
                {
                    MessageBox.Show(this, L.T("Diese Datei ist kein gültiges Bild.", "Questo file non è un'immagine valida.", "This file is not a valid picture."),
                        L.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                PicSource = d.FileName;
                ShowPicture();
            }
        }

        // ---------- Desktop ----------
        void BuildDesktop(Panel t)
        {
            AddInfo(t, L.T(
                "Alles in dieser Liste liegt bei jedem neuen Gast auf dem Desktop – z. B. ein PDF mit Hausinfos oder Verknüpfungen. " +
                "Dateien einfach aus dem Explorer hier hineinziehen. Dein eigener Desktop bleibt unverändert.",
                "Tutto ciò che è in questa lista appare sul desktop di ogni nuovo ospite – ad es. un PDF con le info sulla casa o dei collegamenti. " +
                "Basta trascinare qui i file da Esplora file. Il tuo desktop resta invariato.",
                "Everything in this list appears on every new guest's desktop – e.g. a PDF with house info or shortcuts. " +
                "Just drag files here from Explorer. Your own desktop stays unchanged."), 12, 62);
            lstDesk = new DropListBox { Location = new Point(14, 80), Size = new Size(492, 196), IntegralHeight = false,
                SelectionMode = SelectionMode.MultiExtended,
                EmptyText = L.T("Dateien hierher ziehen", "Trascina qui i file", "Drag files here") };
            foreach (string n in GuestExtras.DesktopNames(store)) lstDesk.Items.Add(new DeskItem(n, null));
            lstDesk.FilesDropped += AddDeskFiles;
            t.Controls.Add(lstDesk);

            Button add = Ui.MakeButton(L.T("Datei …", "File …", "File …"), 14, 286, 112, 32, false);
            add.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Multiselect = true, Title = L.T("Dateien für den Gast-Desktop", "File per il desktop degli ospiti", "Files for the guest desktop") })
                    if (d.ShowDialog(this) == DialogResult.OK) AddDeskFiles(d.FileNames);
            };
            Button web = Ui.MakeButton(L.T("Internetseite …", "Pagina internet …", "Web page …"), 134, 286, 150, 32, false);
            web.Click += (s, e) => AddDeskUrl();
            Button del = Ui.MakeButton(L.T("Entfernen", "Rimuovi", "Remove"), 394, 286, 112, 32, false);
            del.Click += (s, e) =>
            {
                foreach (object o in lstDesk.SelectedItems.Cast<object>().ToList()) lstDesk.Items.Remove(o);
                lstDesk.Invalidate();
            };
            t.Controls.Add(add);
            t.Controls.Add(web);
            t.Controls.Add(del);
            AddInfo(t, L.T("Programme (.exe) kommen als Verknüpfung auf den Desktop. Ordner gehen nicht.",
                "I programmi (.exe) vengono messi come collegamento. Le cartelle non sono possibili.",
                "Programs (.exe) are placed as shortcuts. Folders are not possible."), 328, 44);
        }

        void AddDeskFiles(string[] files)
        {
            var skipped = new List<string>();
            foreach (string f in files)
            {
                if (Directory.Exists(f)) { skipped.Add(Path.GetFileName(f) + L.T(" (Ordner)", " (cartella)", " (folder)")); continue; }
                if (!File.Exists(f)) continue;
                if (new FileInfo(f).Length > 200L * 1024 * 1024) { skipped.Add(Path.GetFileName(f) + L.T(" (größer als 200 MB)", " (più di 200 MB)", " (larger than 200 MB)")); continue; }
                string name = GuestExtras.DesktopNameFor(f);
                foreach (DeskItem old in lstDesk.Items.Cast<DeskItem>().Where(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)).ToList())
                    lstDesk.Items.Remove(old);
                lstDesk.Items.Add(new DeskItem(name, f));
            }
            lstDesk.Invalidate();
            if (skipped.Count > 0)
                MessageBox.Show(this, L.T("Nicht übernommen:", "Non aggiunti:", "Not added:") + "\n\n" + string.Join("\n", skipped.ToArray()),
                    L.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void AddDeskUrl()
        {
            using (var f = new UrlForm())
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                string tmpDir = Path.Combine(Path.GetTempPath(), "FreshGuest-Desktop");
                Directory.CreateDirectory(tmpDir);
                string file = Path.Combine(tmpDir, f.FileName);
                File.WriteAllText(file, "[InternetShortcut]\r\nURL=" + f.Url + "\r\n", Encoding.ASCII);
                AddDeskFiles(new[] { file });
            }
        }

        // ---------- Willkommen ----------
        void BuildWelcome(Panel t)
        {
            AddInfo(t, L.T("Ein Fenster für jeden Gast nach dem Anmelden.",
                "Una finestra per ogni ospite dopo l'accesso.",
                "One window for every guest after sign-in."), 12, 22);
            editInfo = GuestExtras.WelcomeFromStore(store);
            // Persönliche Fassung: beim ersten Einrichten leere Felder mit den Vorgaben füllen (WLAN-Name, Check-out, Texte)
            bool brandFresh = Brand.HasDefaults && store.Get("MarkeVorgaben") == null;
            if (brandFresh) Brand.ApplyDefaults(editInfo);
            chkWelcome2 = AddCheck(t, L.T("Willkommensfenster beim Anmelden zeigen", "Mostra la finestra di benvenuto all'accesso",
                "Show welcome window at sign-in"), 38, GuestExtras.WelcomeOn(store) || brandFresh);
            txtWlan = WelcomeRow(t, L.T("WLAN-Name:", "Nome Wi-Fi:", "Wi-Fi name:"), 68, editInfo.Wlan);
            txtWlanPw = WelcomeRow(t, L.T("WLAN-Kennwort:", "Password Wi-Fi:", "Wi-Fi password:"), 98, editInfo.WlanPw);

            // Ab hier je Sprache: erst die Flagge antippen, dann die Felder in dieser Sprache ausfüllen. „+“ = weitere Sprache
            welcomePanel = t;
            lblEditLang = Ui.MakeLabel("", 0, 133, 100, 22);
            lblEditLang.Font = new Font("Segoe UI Semibold", 10f);
            lblEditLang.ForeColor = Logo.Cyan;
            lblEditLang.AutoEllipsis = true;
            t.Controls.Add(lblEditLang);
            BuildLangTabs();
            txtCheckout = WelcomeRow(t, "Check-out:", 164, "");
            txtContact = WelcomeRow(t, L.T("Kontakt:", "Contatto:", "Contact:"), 194, "");
            // Zusatzfeld: links die eigene Beschriftung, rechts der Inhalt
            txtExtraName = new TextBox { Location = new Point(32, 224), Width = 146 };
            t.Controls.Add(txtExtraName);
            txtExtra = new TextBox { Location = new Point(186, 224), Width = 320 };
            t.Controls.Add(txtExtra);
            txtText = WelcomeRow(t, L.T("Eigener Text:", "Testo proprio:", "Own text:"), 254, "");
            txtText.Multiline = true;
            txtText.ScrollBars = ScrollBars.Vertical;
            txtText.Height = 54;
            btnPreview = Ui.MakeButton(L.T("Vorschau …", "Anteprima …", "Preview …"), 376, 34, 130, 28, false);
            // Vorschau: mit Sprachwahl so, wie der Gast es sieht – nur die gerade gewählte Sprache
            // (Französisch und die zusätzliche Sprache gibt es nur so – die immer einzeln zeigen)
            btnPreview.Click += (s, e) => WelcomeForm.Preview(this, CurrentWelcome(),
                chkChoice.Checked || GuestTexts.All.All(l => l.Code != editSlot) ? editSlot : null);
            t.Controls.Add(btnPreview);
            Indent(AddInfo(t, L.T(
                "Es ist EIN Fenster: Flagge antippen und die Felder in dieser Sprache ausfüllen (✔ = ausgefüllt). „+“ fügt eine weitere installierte Sprache hinzu. Der Gast sieht DE/IT/EN/ES nebeneinander, mit Sprachwahl (Reiter „Sprache“) nur seine – Französisch und die weitere Sprache nur so. Leer = deutscher Text (FR/weitere: englischer).",
                "È UNA sola finestra: tocca una bandiera e compila i campi in quella lingua (✔ = compilata). «+» aggiunge un'altra lingua installata. L'ospite vede DE/IT/EN/ES affiancate, con la scelta della lingua (scheda «Lingua») solo la sua – francese e lingua aggiunta solo così. Vuoto = testo tedesco (FR/aggiunta: inglese).",
                "It is ONE window: tap a flag and fill in the fields in that language (✔ = filled in). “+” adds another installed language. The guest sees DE/IT/EN/ES side by side, with language choice (\"Language\" tab) only their own – French and the added language only that way. Empty = German text (FR/added: English)."),
                316, 100), 32);

            LoadWelcomeFields();
            foreach (TextBox tb in new[] { txtCheckout, txtContact, txtExtraName, txtExtra, txtText })
                tb.TextChanged += (s, e) => { if (!loadingWelcome) MarkFilledTabs(); };
            chkWelcome2.CheckedChanged += (s, e) => UpdateWelcomeEnabled();
            UpdateWelcomeEnabled();
        }

        string CurrentLang4() { return editInfo.Lang4 ?? ""; }

        // Flaggen-Reiter: DE IT EN ES FR, die zusätzliche Sprache (falls gewählt) und „+“; daneben der Name der Sprache
        void BuildLangTabs()
        {
            foreach (LangTab old in langTabs) { welcomePanel.Controls.Remove(old); old.Dispose(); }
            langTabs.Clear();
            var tabs = new List<string[]>();          // Slot, Flagge, Name
            foreach (GuestLang gl in GuestTexts.All.Concat(new[] { GuestTexts.Fr })) tabs.Add(new[] { gl.Code, gl.Code, gl.Name });
            if (GuestTexts.IsExtra(editInfo.Lang4))
            {
                string slot = WelcomeInfo.SlotOf(editInfo.Lang4);
                tabs.Add(new[] { slot, editInfo.Lang4, GuestTexts.Find(slot).Name });
            }
            tabs.Add(new[] { "+", "", L.T("Weitere Sprache hinzufügen / ändern …", "Aggiungi / cambia un'altra lingua …", "Add / change another language …") });
            int tx = 32;
            foreach (string[] d in tabs)
            {
                var tab = new LangTab(d[0], d[1], d[2]) { Location = new Point(tx, 128), Size = new Size(50, 30), Enabled = chkWelcome2 == null || chkWelcome2.Checked };
                if (d[0] == "+") tab.Click += (s, e) => ChooseExtraLanguage();
                else tab.Click += (s, e) => SelectEditLang(tab.Slot);
                langTip.SetToolTip(tab, d[2]);
                langTabs.Add(tab);
                welcomePanel.Controls.Add(tab);
                tx += 56;
            }
            lblEditLang.Location = new Point(tx + 4, 133);
            lblEditLang.Width = Math.Max(40, 506 - tx - 4);
        }

        // Flaggen-Reiter: Felder der bisherigen Sprache merken, die der gewählten Sprache zeigen
        void SelectEditLang(string slot)
        {
            if (slot == editSlot || !editInfo.AllSlots.Contains(slot)) return;
            StoreWelcomeFields();
            editSlot = slot;
            LoadWelcomeFields();
        }

        // „+“: eine weitere Sprache (installiertes Sprachpaket außer DE/IT/EN/ES/FR) wählen, ändern oder entfernen
        void ChooseExtraLanguage()
        {
            StoreWelcomeFields();
            List<string> tags = GuestExtras.InstalledUiLanguages().Where(GuestTexts.IsExtra).ToList();
            if (Program.Demo && tags.Count == 0) tags.AddRange(new[] { "nl-NL", "pl-PL", "pt-PT" });
            string cur = GuestTexts.IsExtra(editInfo.Lang4) ? editInfo.Lang4 : null;
            if (cur != null && !tags.Contains(cur)) tags.Insert(0, cur);
            if (tags.Count == 0)
            {
                MessageBox.Show(this, L.T(
                    "Auf diesem PC ist außer Deutsch, Italienisch, Englisch, Spanisch und Französisch keine weitere Sprache installiert.\n\nSprachpaket hinzufügen (mit deinem Konto): Einstellungen → Zeit und Sprache → Sprache und Region → Sprache hinzufügen. Danach FreshGuest neu öffnen.",
                    "Su questo PC, oltre a tedesco, italiano, inglese, spagnolo e francese, non è installata nessun'altra lingua.\n\nAggiungere un pacchetto di lingua (con il tuo account): Impostazioni → Data/ora e lingua → Lingua e area geografica → Aggiungi una lingua. Poi riaprire FreshGuest.",
                    "Apart from German, Italian, English, Spanish and French, no other language is installed on this PC.\n\nAdd a language pack (with your account): Settings → Time & language → Language & region → Add a language. Then reopen FreshGuest."),
                    L.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var f = new ExtraLangForm(tags, cur))
            {
                f.ShowDialog(this);
                string result = f.Result;
                bool remove = f.RemoveIt;

                if (remove)
                {
                    string slot = WelcomeInfo.SlotOf(cur);
                    if (WelcomeInfo.LangFields.Any(fl => editInfo.Raw(fl, slot).Length > 0) &&
                        MessageBox.Show(this, string.Format(L.T("Die Texte in {0} werden gelöscht. Fortfahren?", "I testi in {0} verranno cancellati. Continuare?",
                            "The texts in {0} will be deleted. Continue?"), GuestTexts.Find(slot).Name), L.AppName,
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    foreach (string fl in WelcomeInfo.LangFields) editInfo.Set(fl, slot, "");
                    editInfo.Lang4 = "";
                    if (editSlot == slot) editSlot = "de";
                }
                else if (result != null && result != cur)
                {
                    // Texte der bisherigen weiteren Sprache gehen an die neue (z. B. pt-PT -> pt-BR: gleiche Felder)
                    if (cur != null)
                    {
                        string from = WelcomeInfo.SlotOf(cur), to = WelcomeInfo.SlotOf(result);
                        if (from != to)
                            foreach (string fl in WelcomeInfo.LangFields) { editInfo.Set(fl, to, editInfo.Raw(fl, from)); editInfo.Set(fl, from, ""); }
                    }
                    editInfo.Lang4 = result;
                    editSlot = WelcomeInfo.SlotOf(result);
                }
                else return;
            }
            BuildLangTabs();
            LoadWelcomeFields();
        }

        // ✔ an den Flaggen, in deren Sprache schon etwas steht; gewählte Sprache hervorheben
        void MarkFilledTabs()
        {
            if (!loadingWelcome) StoreWelcomeFields();
            foreach (LangTab tab in langTabs)
            {
                tab.Selected = tab.Slot == editSlot;
                tab.Filled = tab.Slot != "+" && WelcomeInfo.LangFields.Any(f => editInfo.Raw(f, tab.Slot).Trim().Length > 0);
            }
            GuestLang cur = GuestTexts.Find(editSlot);
            if (lblEditLang != null) lblEditLang.Text = cur != null ? cur.Name : editSlot;
        }

        void StoreWelcomeFields()
        {
            editInfo.Set("Checkout", editSlot, txtCheckout.Text);
            editInfo.Set("Kontakt", editSlot, txtContact.Text);
            editInfo.Set("ExtraName", editSlot, txtExtraName.Text);
            editInfo.Set("Extra", editSlot, txtExtra.Text);
            editInfo.Set("Text", editSlot, txtText.Text);
        }

        void LoadWelcomeFields()
        {
            loadingWelcome = true;
            txtCheckout.Text = editInfo.Raw("Checkout", editSlot);
            txtContact.Text = editInfo.Raw("Kontakt", editSlot);
            txtExtraName.Text = editInfo.Raw("ExtraName", editSlot);
            txtExtra.Text = editInfo.Raw("Extra", editSlot);
            txtText.Text = editInfo.Raw("Text", editSlot);
            // Grauer Hinweis im leeren Feld: der Eintrag, der sonst gilt (Deutsch; bei Französisch/weiterer Sprache erst Englisch)
            bool de = editSlot == "de";
            string fb = editSlot == "it" || editSlot == "es" || editSlot == "en" ? "de" : "en";
            Cue(txtCheckout, de ? "" : editInfo.Get("Checkout", fb));
            Cue(txtContact, de ? "" : editInfo.Get("Kontakt", fb));
            Cue(txtExtra, de ? "" : editInfo.Get("Extra", fb));
            Cue(txtExtraName, de || editInfo.Get("ExtraName", fb).Length == 0
                ? L.T("Zusatzfeld, z. B. Parkplatz", "Campo extra, ad es. Parcheggio", "Extra field, e.g. Parking") : editInfo.Get("ExtraName", fb));
            loadingWelcome = false;
            MarkFilledTabs();
        }

        static void Cue(TextBox tb, string text)
        {
            try { SendMessage(tb.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, text ?? ""); } catch { }
        }

        TextBox WelcomeRow(Panel t, string label, int y, string value)
        {
            t.Controls.Add(Ui.MakeLabel(label, 32, y + 3, 150, 22));
            var tb = new TextBox { Location = new Point(186, y), Width = 320, Text = value ?? "" };
            t.Controls.Add(tb);
            return tb;
        }

        void UpdateWelcomeEnabled()
        {
            txtWlan.Enabled = txtWlanPw.Enabled = txtCheckout.Enabled = txtContact.Enabled = txtText.Enabled = txtExtraName.Enabled = txtExtra.Enabled =
                btnPreview.Enabled = chkWelcome2.Checked;
            foreach (LangTab tab in langTabs) tab.Enabled = chkWelcome2.Checked;
        }

        WelcomeInfo CurrentWelcome()
        {
            StoreWelcomeFields();
            editInfo.Wlan = txtWlan.Text.Trim();
            editInfo.WlanPw = txtWlanPw.Text.Trim();
            editInfo.Lang4 = CurrentLang4();
            editInfo.LangChoice = chkChoice.Checked;
            editInfo.IdleMin = chkIdle.Checked ? IdleChoices[Math.Max(0, cbIdle.SelectedIndex)] : 0;
            editInfo.RestartTime = chkRestart.Checked ? Convert.ToString(cbRestart.SelectedItem) : "";
            return editInfo;
        }

        // ---------- Abmelden & Neustart ----------
        void BuildTimer(Panel t)
        {
            AddInfo(t, L.T(
                "Damit nach der Abreise niemand angemeldet bleibt (z. B. mit Gmail oder Netflix) – du musst nicht mehr selbst daran denken.",
                "Così dopo la partenza nessuno resta connesso (ad es. con Gmail o Netflix) – non devi più pensarci tu.",
                "So nobody stays signed in after departure (e.g. with Gmail or Netflix) – you no longer have to remember."), 12, 44);
            int idle = GuestExtras.IdleMinutes(store);
            chkIdle = AddCheck(t, L.T("PC ausschalten, wenn ein Gast ihn nicht benutzt – nach:", "Spegni il PC se un ospite non lo usa – dopo:",
                "Shut the PC down when a guest isn't using it – after:"), 62, idle > 0);
            cbIdle = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(34, 94), Width = 170 };
            foreach (int m in IdleChoices) cbIdle.Items.Add(m + L.T(" Minuten", " minuti", " minutes"));
            int ii = Array.IndexOf(IdleChoices, idle > 0 ? idle : 60);
            cbIdle.SelectedIndex = ii < 0 ? 2 : ii;
            t.Controls.Add(cbIdle);
            Indent(AddInfo(t, L.T(
                "Zählt ab der letzten Maus- oder Tastatureingabe – wer arbeitet oder ein Video schaut, wird nie unterbrochen. Danach 60 Sekunden „Noch da?“; klickt niemand, fährt der PC herunter.",
                "Conta dall'ultimo uso di mouse o tastiera – chi lavora o guarda un video non viene mai interrotto. Poi 60 secondi «Ci sei ancora?»; se nessuno clicca, il PC si spegne.",
                "Counts from the last mouse or keyboard input – anyone working or watching a video is never interrupted. Then 60 seconds of \"Still there?\"; if nobody clicks, the PC shuts down."), 128, 66), 32);

            string time = GuestExtras.RestartTime(store);
            chkRestart = AddCheck(t, L.T("PC jeden Tag neu starten um:", "Riavvia il PC ogni giorno alle:", "Restart the PC every day at:"), 200, time != null);
            cbRestart = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(34, 232), Width = 170 };
            cbRestart.Items.AddRange(GuestExtras.TimeChoices());
            cbRestart.SelectedItem = time ?? "04:00";
            if (cbRestart.SelectedIndex < 0) cbRestart.SelectedItem = "04:00";
            t.Controls.Add(cbRestart);
            Indent(AddInfo(t, L.T("Wer dann angemeldet ist, bekommt 5 Minuten vorher eine Meldung von Windows. Der PC muss dafür eingeschaltet sein.",
                "Chi è connesso riceve un avviso di Windows 5 minuti prima. Il PC deve essere acceso.",
                "Whoever is signed in gets a Windows message 5 minutes before. The PC must be switched on."), 266, 44), 32);
            // Hinweis-Fenster für Gäste (fester Text in vier Sprachen)
            chkNotice = AddCheck(t, L.T("Hinweis für Gäste beim Anmelden zeigen", "Mostra un avviso agli ospiti all'accesso",
                "Show a notice to guests at sign-in"), 316, GuestExtras.NoticeOn(store));
            var noticeInfo = Indent(AddInfo(t, L.T(
                "Fester Text in allen Sprachen: Der PC wird zurückgesetzt, alles wird gelöscht.",
                "Testo fisso in tutte le lingue: il PC viene ripristinato, tutto viene cancellato.",
                "Fixed text in all languages: the PC is reset, everything is deleted."), 344, 44), 32);
            noticeInfo.Width = 336;
            Button btnNotice = Ui.MakeButton(L.T("Vorschau …", "Anteprima …", "Preview …"), 376, 348, 130, 32, false);
            btnNotice.Click += (s, e) => NoticeForm.Preview(this, chkIdle.Checked ? IdleChoices[Math.Max(0, cbIdle.SelectedIndex)] : 0,
                chkRestart.Checked ? Convert.ToString(cbRestart.SelectedItem) : "", CurrentLang4(), chkChoice.Checked);
            t.Controls.Add(btnNotice);
            chkIdle.CheckedChanged += (s, e) => cbIdle.Enabled = chkIdle.Checked;
            chkRestart.CheckedChanged += (s, e) => cbRestart.Enabled = chkRestart.Checked;
            cbIdle.Enabled = chkIdle.Checked;
            cbRestart.Enabled = chkRestart.Checked;
        }

        // ---------- Sprache ----------
        void BuildLanguage(Panel t)
        {
            AddInfo(t, L.T(
                "Sprache von Windows für Gäste: Startmenü, Einstellungen, Explorer. Ohne Auswahl nimmt Windows für jeden Gast die Sprache, mit der es installiert wurde – nicht die deines Kontos.",
                "Lingua di Windows per gli ospiti: menu Start, Impostazioni, Esplora file. Senza scelta Windows usa per ogni ospite la lingua con cui è stato installato – non quella del tuo account.",
                "Windows language for guests: Start menu, Settings, Explorer. Without a choice, Windows gives every guest the language it was installed with – not the one of your account."),
                12, 66);
            string cur = GuestExtras.GuestLanguage(store);
            langTags = GuestExtras.InstalledUiLanguages();
            if (cur != null && !langTags.Contains(cur)) langTags.Insert(0, cur);
            chkLang = AddCheck(t, L.T("Windows-Sprache für Gäste:", "Lingua di Windows per gli ospiti:", "Windows language for guests:"), 84, cur != null);
            cbGuestLang = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(34, 114), Width = 472 };
            foreach (string tag in langTags) cbGuestLang.Items.Add(GuestExtras.LanguageName(tag));
            int sel = langTags.IndexOf(cur ?? GuestExtras.InstallLanguage());
            if (sel < 0) sel = langTags.IndexOf(System.Globalization.CultureInfo.CurrentUICulture.Name);
            cbGuestLang.SelectedIndex = sel < 0 ? 0 : sel;
            t.Controls.Add(cbGuestLang);
            chkLang.CheckedChanged += (s, e) => cbGuestLang.Enabled = chkLang.Checked;
            cbGuestLang.Enabled = chkLang.Checked;

            Indent(AddInfo(t, L.T("Tastatur für Gäste: wie bei deinem Konto – ", "Tastiera per gli ospiti: come nel tuo account – ",
                "Keyboard for guests: as in your account – ") + GuestExtras.KeyboardName(GuestExtras.AdminKeyboard()) +
                L.T(". So passt sie zur Tastatur am PC.", ". Così corrisponde alla tastiera del PC.", ". That way it matches the PC's keyboard."), 150, 44), 32);
            Indent(AddInfo(t, L.T(
                "Zur Auswahl stehen die Sprachpakete, die auf diesem PC installiert sind (gefunden: ",
                "Si possono scegliere i pacchetti di lingua installati su questo PC (trovati: ",
                "You can choose the language packs installed on this PC (found: ") + langTags.Count + "). " + L.T(
                "Weitere Sprachen fügst du mit deinem Konto hinzu: Einstellungen → Zeit und Sprache → Sprache und Region → Sprache hinzufügen.",
                "Altre lingue si aggiungono con il tuo account: Impostazioni → Data/ora e lingua → Lingua e area geografica → Aggiungi una lingua.",
                "Add more languages with your account: Settings → Time & language → Language & region → Add a language."), 200, 88), 32);
            Indent(AddInfo(t, L.T(
                "Gilt ab dem nächsten Gast.", "Vale dal prossimo ospite.", "Applies from the next guest."), 292, 22), 32);

            chkChoice = AddCheck(t, L.T("Gäste dürfen die Windows-Sprache selbst wählen", "Gli ospiti possono scegliere la lingua di Windows",
                "Guests may choose the Windows language themselves"), 322, GuestExtras.ChoiceOn(store));
            Indent(AddInfo(t, L.T(
                "Beim Anmelden sieht der Gast zuerst nur die Flaggen (DE, IT, EN, ES; Französisch und die weitere Sprache aus „Willkommen“, wenn installiert). Wählt er eine andere Sprache, meldet sich der PC kurz ab und wieder an – dann kommt das Willkommen nur in seiner Sprache. Der nächste Gast bekommt wieder die Standardsprache.",
                "All'accesso l'ospite vede prima solo le bandiere (DE, IT, EN, ES; francese e la lingua aggiunta in «Benvenuto», se installati). Se sceglie un'altra lingua, il PC si disconnette per un attimo e si riconnette – poi il benvenuto appare solo nella sua lingua. L'ospite successivo riceve di nuovo la lingua predefinita.",
                "At sign-in the guest first sees only the flags (DE, IT, EN, ES; French and the added language from “Welcome”, if installed). If they choose another language, the PC signs out briefly and back in – then the welcome appears in their language only. The next guest gets the default language again."),
                350, 80), 32);
        }

        // ---------- Anmeldung ----------
        void BuildSignIn(Panel t)
        {
            AddInfo(t, L.T("Der Anmeldebildschirm nach dem Einschalten und nach jeder Abmeldung.",
                "La schermata di accesso dopo l'accensione e dopo ogni disconnessione.",
                "The sign-in screen after switching on and after every sign-out."), 12, 22);
            chkPreselect = AddCheck(t, L.T("„Guest“ automatisch auswählen", "Seleziona automaticamente «Guest»", "Select \"Guest\" automatically"), 42,
                SignIn.PreselectOn(store));
            Indent(AddInfo(t, L.T("Gäste müssen nur noch auf „Anmelden“ klicken. Du meldest dich weiter unten links über „Anderer Benutzer“ an.",
                "Gli ospiti devono solo cliccare su «Accedi». Tu accedi come sempre in basso a sinistra con «Altro utente».",
                "Guests only have to click \"Sign in\". You still sign in via \"Other user\" at the bottom left."), 70, 40), 32);

            chkGuestOnly = AddCheck(t, L.T("Nur „Guest“ anzeigen („Anderer Benutzer“ verstecken)", "Mostra solo «Guest» (nascondi «Altro utente»)",
                "Show \"Guest\" only (hide \"Other user\")"), 122, SignIn.GuestOnlyOn(store));
            var warn = Indent(AddInfo(t, L.T(
                "ACHTUNG: Dein Konto ist dann am Anmeldebildschirm nicht mehr zu sehen.\r\n" +
                "So kommst du hinein: direkt am Anmeldebildschirm (nicht als Gast anmelden)  Strg + Alt + F  drücken. Es kommt keine Meldung – der Bildschirm flackert kurz, dann erscheint unten links „Anderer Benutzer“ für 10 Minuten. Dort mit Benutzername und Windows-Kennwort anmelden.",
                "ATTENZIONE: il tuo account non sarà più visibile nella schermata di accesso.\r\n" +
                "Per entrare: direttamente nella schermata di accesso (senza accedere come ospite) premi  Ctrl + Alt + F . Non compare alcun messaggio – lo schermo lampeggia un attimo, poi in basso a sinistra compare «Altro utente» per 10 minuti. Accedi lì con nome utente e password di Windows.",
                "WARNING: your account is then no longer shown on the sign-in screen.\r\n" +
                "To get in: press  Ctrl + Alt + F  directly on the sign-in screen (do not sign in as a guest). There is no message – the screen flickers briefly, then \"Other user\" appears at the bottom left for 10 minutes. Sign in there with user name and Windows password."),
                150, 118), 32);
            warn.ForeColor = Color.FromArgb(255, 196, 87);
            Indent(AddInfo(t, L.T(
                "Schutz vor dem Aussperren: Beim ersten Mal musst du die Tastenkombination innerhalb von 3 Minuten nach dem Start benutzen und dich anmelden – sonst schaltet sich die Option wieder aus.",
                "Protezione contro il blocco: la prima volta devi usare la combinazione di tasti entro 3 minuti dall'avvio e accedere. Altrimenti questa opzione si disattiva da sola.",
                "Lock-out protection: the first time you must use the key combination within 3 minutes after start-up and sign in. Otherwise this option turns itself off again."),
                272, 64), 32);
            bool conf = SignIn.Confirmed();
            var state = Indent(AddInfo(t, L.T("Tastenkombination an diesem PC: ", "Combinazione di tasti su questo PC: ", "Key combination on this PC: ") +
                (conf ? L.T("✔ funktioniert (bestätigt)", "✔ funziona (confermata)", "✔ works (confirmed)")
                      : L.T("noch nicht benutzt", "non ancora usata", "not used yet")), 340, 22), 32);
            if (conf) state.ForeColor = Color.FromArgb(120, 200, 140);
            if (!store.Enabled)
                Indent(AddInfo(t, L.T("Gilt nur, solange der Schutz eingeschaltet ist.", "Vale solo con la protezione attiva.", "Applies only while protection is on."), 364, 22), 32);

            chkGuestOnly.Click += (s, e) =>
            {
                if (!chkGuestOnly.Checked) return;
                if (MessageBox.Show(this, L.T(
                        "ACHTUNG\n\nDanach zeigt der Anmeldebildschirm nur noch „Guest“. Dein eigenes Konto erreichst du nur noch so:\n\n" +
                        "     Direkt am Anmeldebildschirm  Strg + Alt + F  drücken\n     (nicht vorher als Gast anmelden)\n\n" +
                        "Es kommt keine Meldung: Der Bildschirm flackert kurz, dann erscheint unten links „Anderer Benutzer“ für 10 Minuten.\n" +
                        "Bitte merke dir die Tastenkombination oder schreibe sie auf.\n\nEinschalten?",
                        "ATTENZIONE\n\nDopo, la schermata di accesso mostra solo «Guest». Il tuo account è raggiungibile solo così:\n\n" +
                        "     Direttamente nella schermata di accesso premi  Ctrl + Alt + F\n     (senza accedere prima come ospite)\n\n" +
                        "Non compare alcun messaggio: lo schermo lampeggia un attimo, poi in basso a sinistra compare «Altro utente» per 10 minuti.\n" +
                        "Ricorda la combinazione di tasti o annotala.\n\nAttivare?",
                        "WARNING\n\nAfterwards the sign-in screen shows \"Guest\" only. You reach your own account only like this:\n\n" +
                        "     Press  Ctrl + Alt + F  directly on the sign-in screen\n     (do not sign in as a guest first)\n\n" +
                        "There is no message: the screen flickers briefly, then \"Other user\" appears at the bottom left for 10 minutes.\n" +
                        "Please remember the key combination or write it down.\n\nTurn on?"),
                        L.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    chkGuestOnly.Checked = false;
            };
        }

        // ---------- Sperren & Drucker ----------
        void BuildLocks(Panel t)
        {
            AddInfo(t, L.T("Gilt nur für Gäste – dein eigenes Konto bleibt frei.", "Vale solo per gli ospiti – il tuo account resta libero.",
                "Applies to guests only – your own account stays free."), 12, 22);
            int locks = GuestExtras.Locks(store);
            chkLockSettings = AddCheck(t, L.T("Systemsteuerung und Windows-Einstellungen sperren", "Blocca Pannello di controllo e Impostazioni di Windows",
                "Lock Control Panel and Windows Settings"), 42, (locks & GuestExtras.LockSettings) != 0);
            Indent(AddInfo(t, L.T("WLAN, Lautstärke und Drucken gehen weiterhin.", "Wi-Fi, volume e stampa funzionano comunque.",
                "Wi-Fi, volume and printing still work."), 70, 22), 32);
            chkLockTools = AddCheck(t, L.T("Eingabeaufforderung und Registry-Editor sperren", "Blocca Prompt dei comandi ed Editor del Registro",
                "Lock Command Prompt and Registry Editor"), 100, (locks & GuestExtras.LockTools) != 0);
            chkLockInstall = AddCheck(t, L.T("Programme installieren sperren", "Blocca l'installazione di programmi",
                "Block installing programs"), 130, (locks & GuestExtras.LockInstall) != 0);
            chkLockDownloads = AddCheck(t, L.T("Programme aus Downloads und vom Desktop sperren", "Blocca i programmi da Download e dal desktop",
                "Block programs from Downloads and the desktop"), 158, (locks & GuestExtras.LockDownloads) != 0);
            Indent(AddInfo(t, L.T("Programme aus C:\\Programme (Browser, Office, VLC …) laufen normal.",
                "I programmi in C:\\Programmi (browser, Office, VLC …) funzionano normalmente.",
                "Programs in C:\\Program Files (browsers, Office, VLC …) work normally."), 186, 40), 32);

            var head = Ui.MakeLabel(L.T("Drucker", "Stampante", "Printer"), 12, 234, 300, 24);
            head.Font = new Font("Segoe UI Semibold", 11f);
            t.Controls.Add(head);
            string printer = GuestExtras.GuestPrinter(store);
            chkPrinter = AddCheck(t, L.T("Standarddrucker für Gäste:", "Stampante predefinita per gli ospiti:", "Default printer for guests:"), 262, printer != null);
            cbPrinter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(34, 292), Width = 472 };
            string[] printers = GuestExtras.Printers();
            cbPrinter.Items.AddRange(printers);
            string def = null;
            try { def = new System.Drawing.Printing.PrinterSettings().PrinterName; } catch { }
            if (printer != null && !printers.Contains(printer)) cbPrinter.Items.Insert(0, printer);
            cbPrinter.SelectedItem = printer ?? def;
            if (cbPrinter.SelectedIndex < 0 && cbPrinter.Items.Count > 0) cbPrinter.SelectedIndex = 0;
            t.Controls.Add(cbPrinter);
            lblPrinterNote = Indent(AddInfo(t, "", 324, 66), 32);
            cbPrinter.SelectedIndexChanged += (s, e) => UpdatePrinterNote();
            chkPrinter.CheckedChanged += (s, e) => { cbPrinter.Enabled = chkPrinter.Checked; UpdatePrinterNote(); };
            cbPrinter.Enabled = chkPrinter.Checked;
            UpdatePrinterNote();
        }

        void UpdatePrinterNote()
        {
            string p = cbPrinter.SelectedItem as string;
            if (cbPrinter.Items.Count == 0)
                lblPrinterNote.Text = L.T("Kein Drucker gefunden. Zuerst den Drucker mit deinem Konto installieren.",
                    "Nessuna stampante trovata. Installare prima la stampante con il tuo account.",
                    "No printer found. First install the printer with your account.");
            else if (p != null && p.StartsWith(@"\\"))
                lblPrinterNote.Text = L.T("Das ist eine Netzwerk-Verbindung – sie gilt meist nur für dein Konto. Besser den Drucker direkt (über seine IP-Adresse) installieren.",
                    "È una connessione di rete – di solito vale solo per il tuo account. Meglio installare la stampante direttamente (tramite indirizzo IP).",
                    "This is a network connection – it usually applies to your account only. Better install the printer directly (via its IP address).");
            else
                lblPrinterNote.Text = L.T("Gäste drucken dann z. B. Bordkarten direkt auf diesem Drucker – ohne ihn erst auswählen zu müssen.",
                    "Gli ospiti stampano ad es. le carte d'imbarco direttamente su questa stampante – senza doverla scegliere.",
                    "Guests then print e.g. boarding passes straight to this printer – without having to choose it.");
        }

        void UpdateWallEnabled() { txtImg.Enabled = btnImg.Enabled = cbStyle.Enabled = chkWall.Checked; }
        void UpdateAutoEnabled() { txtAuto.Enabled = btnAuto.Enabled = btnAutoUrl.Enabled = chkAuto.Checked; }

        void LoadPreview(string path)
        {
            Image old = preview.Image;
            preview.Image = null;
            if (old != null) old.Dispose();
            try
            {
                if (File.Exists(path))
                    using (Image tmp = Image.FromFile(path)) preview.Image = new Bitmap(tmp, FitSize(tmp.Size, new Size(512, 288)));
            }
            catch { }
        }

        static Size FitSize(Size s, Size max)
        {
            double f = Math.Min((double)max.Width / s.Width, (double)max.Height / s.Height);
            if (f > 1) f = 1;
            return new Size(Math.Max(1, (int)(s.Width * f)), Math.Max(1, (int)(s.Height * f)));
        }

        void PickImage()
        {
            using (var d = new OpenFileDialog())
            {
                d.Title = L.T("Hintergrundbild wählen", "Scegli sfondo", "Choose wallpaper");
                d.Filter = L.T("Bilder", "Immagini", "Pictures") + " (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!GuestExtras.IsImage(d.FileName))
                {
                    MessageBox.Show(this, L.T("Diese Datei ist kein gültiges Bild.", "Questo file non è un'immagine valida.", "This file is not a valid picture."),
                        L.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                txtImg.Text = d.FileName;
                LoadPreview(d.FileName);
            }
        }

        void PickProgram()
        {
            using (var d = new OpenFileDialog())
            {
                d.Title = L.T("Programm wählen", "Scegli programma", "Choose program");
                d.Filter = L.T("Programme", "Programmi", "Programs") + " (*.exe)|*.exe";
                d.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (d.ShowDialog(this) == DialogResult.OK) txtAuto.Text = d.FileName;
            }
        }

        void Fail(int tab, string text)
        {
            tabs.SelectedIndex = tab;
            MessageBox.Show(this, text, L.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void OnOk()
        {
            // Browser
            Options = BrowserOpt.None;
            if (chkWelcome.Checked) Options |= BrowserOpt.NoWelcome;
            if (chkPw.Checked) Options |= BrowserOpt.NoPasswords;
            if (chkDefault.Checked) Options |= BrowserOpt.NoDefaultPrompt;
            if (chkIcons.Checked) Options |= BrowserOpt.Shortcuts;
            Url = null;
            if (chkStart.Checked)
            {
                Url = BrowserSetup.NormalizeUrl(txtUrl.Text);
                if (Url == null)
                {
                    Fail(0, L.T("Bitte eine gültige Internet-Adresse eingeben, z. B. www.google.it",
                        "Inserire un indirizzo internet valido, ad es. www.google.it", "Please enter a valid web address, e.g. www.google.com"));
                    return;
                }
                Options |= BrowserOpt.StartPage;
            }

            // Hintergrund
            WallOn = chkWall.Checked;
            WallStyle = GuestExtras.StyleCodes[Math.Max(0, cbStyle.SelectedIndex)];
            WallSource = txtImg.Text;
            if (WallOn && !GuestExtras.IsImage(WallSource))
            {
                Fail(1, L.T("Bitte ein Hintergrundbild wählen.", "Scegliere un'immagine di sfondo.", "Please choose a wallpaper picture."));
                return;
            }
            bool wasOn = GuestExtras.WallpaperOn(store);
            WallChanged = WallOn != wasOn ||
                (WallOn && (!string.Equals(WallSource, store.Get("Hintergrund"), StringComparison.OrdinalIgnoreCase) ||
                            WallStyle != (store.Get("HintergrundStil") ?? "4")));

            // Autostart
            AutoOn = chkAuto.Checked;
            AutoTarget = null;
            if (AutoOn)
            {
                AutoTarget = GuestExtras.NormalizeTarget(txtAuto.Text);
                if (AutoTarget == null)
                {
                    Fail(4, L.T("Bitte eine Internet-Adresse oder ein vorhandenes Programm (.exe) angeben.",
                        "Indicare un indirizzo internet o un programma esistente (.exe).",
                        "Please enter a web address or an existing program (.exe)."));
                    return;
                }
            }
            AutoChanged = AutoOn != GuestExtras.AutostartOn(store) || (AutoOn && AutoTarget != store.Get("Autostart"));

            // Profilbild
            PicOn = chkPic.Checked;
            if (PicOn && PicSource == null)
            {
                Fail(2, L.T("Bitte ein Bild wählen.", "Scegliere un'immagine.", "Please choose a picture."));
                return;
            }
            PicChanged = PicOn != GuestExtras.PictureOn(store) || (PicOn && PicSource != store.Get("Profilbild"));

            // Desktop
            DeskItems = lstDesk.Items.Cast<DeskItem>().ToList();
            DeskChanged = DeskItems.Any(i => i.Source != null) ||
                !DeskItems.Select(i => i.Name).SequenceEqual(GuestExtras.DesktopNames(store), StringComparer.OrdinalIgnoreCase);

            // Willkommen + Abmelden
            WelcomeOn = chkWelcome2.Checked;
            Welcome = CurrentWelcome();
            IdleMin = chkIdle.Checked ? IdleChoices[Math.Max(0, cbIdle.SelectedIndex)] : 0;
            WelcomeInfo old = GuestExtras.WelcomeFromStore(store);
            NoticeOn = chkNotice.Checked;
            SessionChanged = WelcomeOn != GuestExtras.WelcomeOn(store) || IdleMin != GuestExtras.IdleMinutes(store) || NoticeOn != GuestExtras.NoticeOn(store) ||
                Welcome.Lang4 != old.Lang4 || (WelcomeOn && !Welcome.SameTexts(old));

            // Täglicher Neustart
            RestartTime = chkRestart.Checked ? (string)cbRestart.SelectedItem : null;
            RestartChanged = RestartTime != GuestExtras.RestartTime(store);

            // Sperren + Drucker
            Locks = (chkLockSettings.Checked ? GuestExtras.LockSettings : 0) | (chkLockTools.Checked ? GuestExtras.LockTools : 0) |
                    (chkLockInstall.Checked ? GuestExtras.LockInstall : 0) | (chkLockDownloads.Checked ? GuestExtras.LockDownloads : 0);
            Printer = chkPrinter.Checked && cbPrinter.SelectedItem != null ? (string)cbPrinter.SelectedItem : null;
            if (chkPrinter.Checked && Printer == null)
            {
                Fail(7, L.T("Bitte einen Drucker auswählen.", "Scegliere una stampante.", "Please choose a printer."));
                return;
            }
            int storedLocks;
            int.TryParse(store.Get("Sperren"), out storedLocks);
            LocksChanged = Locks != storedLocks || Printer != GuestExtras.GuestPrinter(store);   // roh vergleichen: alte Werte (1.10) werden neu geschrieben

            // Sprache
            GuestLang = chkLang.Checked && cbGuestLang.SelectedIndex >= 0 ? langTags[cbGuestLang.SelectedIndex] : null;
            LangChanged = GuestLang != GuestExtras.GuestLanguage(store);
            ChoiceOn = chkChoice.Checked;
            ChoiceChanged = ChoiceOn != GuestExtras.ChoiceOn(store);

            // Anmeldung
            Preselect = chkPreselect.Checked;
            GuestOnly = chkGuestOnly.Checked;
            SignInChanged = Preselect != SignIn.PreselectOn(store) || GuestOnly != SignIn.GuestOnlyOn(store);

            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && preview != null && preview.Image != null) { preview.Image.Dispose(); preview.Image = null; }
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------
    // Hauptfenster
    // ------------------------------------------------------------------
    class MainForm : Form
    {
        public bool SwitchLanguage;
        readonly Store store;
        readonly Panel statusPanel;
        readonly Label lblStatus, lblDetail;
        readonly Button btnToggle, btnReboot, btnPw, btnCheck, btnUninstall, btnSettings, btnGuide;
        readonly ComboBox cbLang;
        readonly TextBox log;
        readonly LinkLabel lnkExport;
        int failed;
        bool busy;

        public MainForm(Store store)
        {
            this.store = store;
            Ui.Style(this, 560, 612);
            Text = L.AppName + " " + Program.Version + (Program.ShowDemoHints ? "   [DEMO – " + L.T("ändert nichts", "non modifica nulla", "changes nothing") + "]" : "");

            int lockupRight;
            Panel header = Logo.MakeHeader(this, 78, out lockupRight);
            cbLang = Ui.MakeLanguageBox(540, 26, OnLanguage);
            header.Controls.Add(cbLang);

            statusPanel = new Panel { Location = new Point(20, 92), Size = new Size(520, 108) };
            lblStatus = Ui.MakeLabel("", 18, 12, 490, 44);
            lblStatus.Font = new Font("Segoe UI Semibold", 20f);
            lblStatus.ForeColor = Color.White;
            lblDetail = Ui.MakeLabel("", 20, 58, 490, 48);
            lblDetail.ForeColor = Color.White;
            statusPanel.Controls.Add(lblStatus);
            statusPanel.Controls.Add(lblDetail);
            Controls.Add(statusPanel);

            // Der wichtigste Knopf oben und farbig: die Einstellungen für Gäste
            btnSettings = Ui.MakeButton("⚙  " + L.T("Einstellungen für Gäste …", "Impostazioni per gli ospiti …", "Guest settings …"),
                20, 212, 520, 46, true);
            btnToggle = Ui.MakeButton("", 20, 268, 255, 36, false);
            btnReboot = Ui.MakeButton(L.T("Jetzt neu starten", "Riavvia ora", "Restart now"), 285, 268, 255, 36, false);
            btnPw = Ui.MakeButton(L.T("Kennwort ändern", "Cambia password", "Change password"), 20, 314, 255, 36, false);
            btnCheck = Ui.MakeButton(L.T("Details prüfen", "Verifica dettagli", "Check details"), 285, 314, 255, 36, false);
            btnGuide = Ui.MakeButton(L.T("Anleitung", "Guida", "Guide"), 20, 360, 255, 36, false);
            btnUninstall = Ui.MakeButton(L.T("Deinstallieren", "Disinstalla", "Uninstall"), 285, 360, 255, 36, false);
            btnToggle.Click += OnToggle;
            btnReboot.Click += (s, e) => { if (!busy) Sys.Reboot(); };
            btnPw.Click += OnChangePassword;
            btnCheck.Click += OnCheck;
            btnUninstall.Click += OnUninstall;
            btnSettings.Click += OnSettings;
            btnGuide.Click += (s, e) => Guide.Open(this);
            Controls.AddRange(new Control[] { btnToggle, btnReboot, btnPw, btnCheck, btnUninstall, btnSettings, btnGuide });

            var logTitle = Ui.MakeLabel(L.T("Protokoll", "Registro", "Log"), 20, 408, 200, 22);
            logTitle.ForeColor = Ui.Muted;
            Controls.Add(logTitle);
            lnkExport = new LinkLabel { Text = L.T("Protokolle auf USB-Stick speichern", "Salva i log su chiavetta USB", "Save logs to USB stick"),
                Location = new Point(220, 408), Size = new Size(320, 22), TextAlign = ContentAlignment.TopRight, LinkColor = Ui.Link };
            lnkExport.LinkClicked += OnExport;
            Controls.Add(lnkExport);
            log = Ui.MakeLog(20, 432, 520, 144);
            Controls.Add(log);
            var copy = Ui.MakeLabel(Program.Copyright, 20, 584, 520, 20);
            copy.ForeColor = Ui.Muted;
            copy.TextAlign = ContentAlignment.TopRight;
            copy.Font = new Font("Segoe UI", 8f);
            Controls.Add(copy);
            Log(L.T("Bereit.", "Pronto.", "Ready.") + (Program.ShowDemoHints ? "  (DEMO – " +
                L.T("am PC wird nichts verändert", "il PC non viene modificato", "nothing is changed on the PC") + ")" : ""));

            RefreshStatus();
        }

        void OnLanguage()
        {
            store.Set("Sprache", L.Code);
            try { store.Save(); } catch { }
            SwitchLanguage = true;
            Close();
        }

        void RefreshStatus()
        {
            bool on = store.Enabled;
            bool pending = store.IsPending;
            if (pending)
            {
                statusPanel.BackColor = Ui.Orange;
                lblStatus.Text = on ? L.T("Neustart nötig – Schutz wird AN", "Riavvio necessario – ON", "Restart needed – turns ON")
                                    : L.T("Neustart nötig – Schutz wird AUS", "Riavvio necessario – OFF", "Restart needed – turns OFF");
                lblDetail.Text = on ? L.T("Zuerst die Einstellungen für Gäste machen – dann neu starten.",
                                          "Prima fai le impostazioni per gli ospiti – poi riavvia.",
                                          "First set up the guest settings – then restart.")
                                    : L.T("Die Änderung wird erst nach einem Neustart wirksam.",
                                          "La modifica sarà attiva solo dopo un riavvio.",
                                          "The change takes effect only after a restart.");
                btnReboot.Visible = true;
            }
            else if (on)
            {
                statusPanel.BackColor = Ui.Green;
                lblStatus.Text = "✔  " + L.T("SCHUTZ IST AN", "PROTEZIONE ATTIVA", "PROTECTION IS ON");
                lblDetail.Text = L.T("Gäste melden sich mit „Gast“ an. Beim Abmelden oder Neustart wird alles vom Gast gelöscht.",
                                     "Gli ospiti accedono con «Ospite». Alla disconnessione o al riavvio tutto viene cancellato.",
                                     "Guests sign in with \"Guest\". At sign-out or restart everything from the guest is deleted.");
                btnReboot.Visible = false;
            }
            else
            {
                statusPanel.BackColor = Ui.Grey;
                lblStatus.Text = L.T("SCHUTZ IST AUS", "PROTEZIONE DISATTIVATA", "PROTECTION IS OFF");
                lblDetail.Text = L.T("Gast-Daten werden nicht gelöscht. Programme installieren geht auch bei Schutz AN – mit deinem Konto.",
                                     "I dati ospite non vengono cancellati. Installare programmi si può anche con protezione attiva.",
                                     "Guest data is not deleted. Installing programs also works with protection ON – using your account.");
                btnReboot.Visible = false;
            }
            btnToggle.Text = on ? L.T("Schutz ausschalten", "Disattiva protezione", "Turn protection off")
                                : L.T("Schutz einschalten", "Attiva protezione", "Turn protection on");
            // Breite aus "pending" ableiten: Visible ist vor dem Anzeigen des Fensters immer false
            // (aus den Nachbar-Buttons berechnet, damit es bei jeder Bildschirm-Skalierung passt)
            btnToggle.Width = pending ? btnPw.Width : btnReboot.Right - btnToggle.Left;
        }

        void Log(string line)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), line); return; }
            log.AppendText(line + Environment.NewLine);
        }

        void SetBusy(bool b, string text)
        {
            busy = b;
            UseWaitCursor = b;
            foreach (Button x in new[] { btnToggle, btnReboot, btnPw, btnCheck, btnUninstall, btnSettings }) x.Enabled = !b;
            lnkExport.Enabled = cbLang.Enabled = !b;
            if (b)
            {
                statusPanel.BackColor = Ui.Blue;
                lblStatus.Text = L.T("Bitte warten …", "Attendere …", "Please wait …");
                lblDetail.Text = text;
            }
            else RefreshStatus();
        }

        bool CheckPassword(string action)
        {
            string pw = PasswordForm.Ask(this, action, PasswordForm.PromptText);
            if (pw == null) return false;
            if (Pwd.Check(store, pw)) { failed = 0; return true; }
            failed++;
            Thread.Sleep(1500);
            if (failed >= 5)
            {
                MessageBox.Show(this, L.T("Zu viele falsche Versuche. Das Programm wird beendet.",
                    "Troppi tentativi errati. Il programma viene chiuso.", "Too many wrong attempts. The program will close."),
                    L.AppName, MessageBoxButtons.OK, MessageBoxIcon.Stop);
                Close();
                return false;
            }
            MessageBox.Show(this, L.T("Falsches Kennwort.", "Password errata.", "Wrong password."), L.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        void AskReboot(string text)
        {
            if (MessageBox.Show(this, text + "\n\n" + L.T("Jetzt neu starten?", "Riavviare ora?", "Restart now?"), L.AppName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                Sys.Reboot();
        }

        async void OnToggle(object sender, EventArgs e)
        {
            if (busy) return;
            bool turnOn = !store.Enabled;
            if (!CheckPassword(turnOn ? L.T("Schutz einschalten", "Attiva protezione", "Turn protection on")
                                      : L.T("Schutz ausschalten", "Disattiva protezione", "Turn protection off"))) return;

            SetBusy(true, turnOn ? L.T("Schutz wird eingeschaltet (kann 1–2 Minuten dauern) …", "Attivazione in corso (può durare 1–2 minuti) …", "Turning protection on (may take 1–2 minutes) …")
                                 : L.T("Schutz wird ausgeschaltet …", "Disattivazione in corso …", "Turning protection off …"));
            log.Clear();
            int code = await Sys.RunScript(turnOn ? "Einrichten" : "Rueckgaengig", Log);
            if (code != 0)
            {
                SetBusy(false, null);
                MessageBox.Show(this, L.T("Das hat leider nicht geklappt. Details stehen im Protokoll.",
                    "Purtroppo non ha funzionato. I dettagli sono nel registro.", "Unfortunately that did not work. Details are in the log."),
                    L.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            store.MarkChanged(turnOn);
            store.Save();
            try { if (turnOn) SignIn.Sync(store, Log); else SignIn.Remove(false, Log); } catch (Exception ex) { Log(ex.Message); }
            SetBusy(false, null);
            AskReboot(turnOn ? L.T("Der Schutz ist eingestellt und wird nach dem Neustart aktiv.",
                                   "La protezione è impostata e sarà attiva dopo il riavvio.",
                                   "Protection is set and becomes active after the restart.")
                             : L.T("Der Schutz ist ausgeschaltet. Nach dem Neustart werden Gast-Daten nicht mehr gelöscht.",
                                   "La protezione è disattivata. Dopo il riavvio i dati degli ospiti non vengono più cancellati.",
                                   "Protection is off. After the restart guest data is no longer deleted."));
        }

        void OnChangePassword(object sender, EventArgs e)
        {
            string titel = L.T("Kennwort ändern", "Cambia password", "Change password");
            if (busy || !CheckPassword(titel)) return;
            string neu = PasswordForm.AskNew(this, titel);
            if (neu == null) return;
            Pwd.Set(store, neu);
            store.Save();
            Log(L.T("Kennwort geändert.", "Password cambiata.", "Password changed."));
            MessageBox.Show(this, L.T("Das neue Kennwort ist gespeichert.", "La nuova password è stata salvata.", "The new password has been saved."),
                L.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        async void OnCheck(object sender, EventArgs e)
        {
            if (busy) return;
            SetBusy(true, L.T("Einstellungen werden geprüft …", "Verifica delle impostazioni …", "Checking settings …"));
            log.Clear();
            await Sys.RunScript("Status", Log);
            Log("");
            Log(L.T("Browser-Vorlage: ", "Impostazioni browser: ", "Browser settings: ") + BrowserSetup.Describe(store));
            Log(L.T("Hintergrund Gäste: ", "Sfondo ospiti: ", "Guest wallpaper: ") + (store.Get("Hintergrund") ?? L.T("aus", "no", "off")));
            Log(L.T("Autostart Gäste: ", "Avvio automatico ospiti: ", "Guest autostart: ") + (store.Get("Autostart") ?? L.T("aus", "no", "off")));
            string aus = L.T("aus", "no", "off");
            int desk = GuestExtras.DesktopNames(store).Count;
            Log(L.T("Desktop Gäste: ", "Desktop ospiti: ", "Guest desktop: ") + (desk > 0 ? desk + L.T(" Einträge", " elementi", " items") : aus));
            Log(L.T("Willkommensfenster: ", "Finestra di benvenuto: ", "Welcome window: ") + (GuestExtras.WelcomeOn(store) ? L.T("an", "sì", "on") : aus));
            int idle = GuestExtras.IdleMinutes(store);
            Log(L.T("Automatisch ausschalten: ", "Spegnimento automatico: ", "Automatic shutdown: ") + (idle > 0 ? idle + " min" : aus));
            Log(L.T("Hinweis für Gäste: ", "Avviso per gli ospiti: ", "Guest notice: ") + (GuestExtras.NoticeOn(store) ? L.T("an", "sì", "on") : aus));
            Log(L.T("Windows-Sprache Gäste: ", "Lingua di Windows ospiti: ", "Guest Windows language: ") +
                (GuestExtras.GuestLanguage(store) != null ? GuestExtras.LanguageName(GuestExtras.GuestLanguage(store)) : L.T("Windows-Vorgabe", "predefinita", "Windows default")) +
                "   " + L.T("Sprachwahl: ", "Scelta lingua: ", "Language choice: ") + (GuestExtras.ChoiceOn(store) ? L.T("an", "sì", "on") : aus));
            Log(L.T("Täglicher Neustart: ", "Riavvio giornaliero: ", "Daily restart: ") + (GuestExtras.RestartTime(store) ?? aus));
            Log(L.T("Profilbild Gäste: ", "Immagine profilo ospiti: ", "Guest profile picture: ") +
                (GuestExtras.PictureOn(store) ? (store.Get("Profilbild") == GuestExtras.LogoPic ? L.T("Pinguin-Logo", "logo pinguino", "penguin logo") :
                    store.Get("Profilbild") == GuestExtras.BrandPic ? Brand.PictureName : L.T("eigenes Bild", "immagine propria", "own picture")) : aus));
            Log(L.T("Sperren Gäste: ", "Blocchi ospiti: ", "Guest locks: ") + (GuestExtras.Locks(store) != 0 ? GuestExtras.Locks(store).ToString() : aus) +
                "   " + L.T("Drucker: ", "Stampante: ", "Printer: ") + (GuestExtras.GuestPrinter(store) ?? aus));
            SetBusy(false, null);
        }

        async void OnSettings(object sender, EventArgs e)
        {
            string titel = L.T("Einstellungen für Gäste", "Impostazioni per gli ospiti", "Guest settings");
            if (busy || !CheckPassword(titel)) return;
            using (var f = new SettingsForm(store, 0))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                log.Clear();
                SetBusy(true, L.T("Einstellungen werden übernommen …", "Applicazione delle impostazioni …", "Applying settings …"));
                bool ok = true;
                await Task.Run(() =>
                {
                    ok &= Try(() =>
                    {
                        if (f.Options != BrowserOpt.None) BrowserSetup.Apply(store, f.Options, f.Url, Log);
                        else if (BrowserSetup.Current(store) != BrowserOpt.None) { BrowserSetup.Remove(store, Log); store.Save(); }
                    });
                    if (f.WallChanged)
                        ok &= Try(() => { if (f.WallOn) GuestExtras.ApplyWallpaper(store, f.WallSource, f.WallStyle, Log); else GuestExtras.RemoveWallpaper(store, Log); });
                    if (f.AutoChanged)
                        ok &= Try(() => { if (f.AutoOn) GuestExtras.ApplyAutostart(store, f.AutoTarget, Log); else GuestExtras.RemoveAutostart(store, Log); });
                    if (f.PicChanged)
                        ok &= Try(() => { if (f.PicOn) GuestExtras.ApplyPicture(store, f.PicSource, Log); else GuestExtras.RemovePicture(store, Log); });
                    if (f.DeskChanged)
                        ok &= Try(() => GuestExtras.ApplyDesktop(store, f.DeskItems, Log));
                    if (f.SessionChanged)
                        ok &= Try(() => GuestExtras.ApplyGuestSession(store, f.WelcomeOn, f.Welcome, f.IdleMin, f.NoticeOn, Log));
                    if (f.RestartChanged)
                        ok &= Try(() => GuestExtras.ApplyDailyRestart(store, f.RestartTime, Log));
                    if (f.LocksChanged)
                        ok &= Try(() => GuestExtras.ApplyLocksAndPrinter(store, f.Locks, f.Printer, Log));
                    if (f.LangChanged)
                        ok &= Try(() => GuestExtras.ApplyGuestLanguage(store, f.GuestLang, Log));
                    if (f.ChoiceChanged)
                        ok &= Try(() => GuestExtras.ApplyGuestChoice(store, f.ChoiceOn, Log));
                    if (f.SignInChanged)
                        ok &= Try(() => SignIn.Apply(store, f.Preselect, f.GuestOnly, Log));
                    else if (f.PicChanged)
                        ok &= Try(() => SignIn.Sync(store, Log));       // Bild auf der Kachel „Guest“: Aufgabe an/aus
                });
                SetBusy(false, null);
                if (ok && f.SignInChanged && f.GuestOnly && !SignIn.Confirmed() && store.Enabled && !Program.Demo)
                    AskReboot(L.T("„Nur Guest anzeigen“ ist eingeschaltet.\n\nBitte jetzt neu starten und direkt am Anmeldebildschirm (nicht als Gast anmelden) einmal Strg + Alt + F drücken. Der Bildschirm flackert kurz, dann unten links „Anderer Benutzer“ wählen und mit deinem Konto anmelden. Erst danach bleibt „Anderer Benutzer“ dauerhaft versteckt.",
                                  "«Mostra solo Guest» è attivo.\n\nRiavvia ora e direttamente nella schermata di accesso (senza accedere come ospite) premi una volta Ctrl + Alt + F. Lo schermo lampeggia un attimo, poi scegli «Altro utente» in basso a sinistra e accedi con il tuo account. Solo dopo «Altro utente» resta nascosto in modo permanente.",
                                  "\"Show Guest only\" is on.\n\nPlease restart now and press Ctrl + Alt + F once directly on the sign-in screen (do not sign in as a guest). The screen flickers briefly, then choose \"Other user\" at the bottom left and sign in with your account. Only after that does \"Other user\" stay hidden permanently."));
                if (ok)
                    Log(L.T("Fertig. Kein Neustart nötig – gilt ab dem nächsten Gast.",
                            "Fatto. Nessun riavvio necessario – vale dal prossimo ospite.",
                            "Done. No restart needed – applies from the next guest."));
                else
                    MessageBox.Show(this, L.T("Nicht alles hat geklappt. Details stehen im Protokoll.", "Non tutto è andato a buon fine. I dettagli sono nel registro.",
                        "Not everything worked. Details are in the log."), L.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        bool Try(Action a)
        {
            try { a(); return true; }
            catch (Exception ex) { Log(L.T("FEHLER: ", "ERRORE: ", "ERROR: ") + ex.Message); return false; }
        }

        async void OnExport(object sender, LinkLabelLinkClickedEventArgs e)
        {
            if (busy) return;
            string root = Sys.FindUsbDrive();
            if (root == null)
            {
                using (var fbd = new FolderBrowserDialog())
                {
                    fbd.Description = L.T("Wohin sollen die Protokolle gespeichert werden? (z. B. USB-Stick)",
                        "Dove salvare i log? (ad es. chiavetta USB)", "Where should the logs be saved? (e.g. USB stick)");
                    if (fbd.ShowDialog(this) != DialogResult.OK) return;
                    root = fbd.SelectedPath;
                }
            }
            string guiText = log.Text;
            SetBusy(true, L.T("Protokolle werden gespeichert …", "Salvataggio dei log …", "Saving logs …"));
            try
            {
                string dir = await Sys.ExportLogs(store, root, guiText);
                SetBusy(false, null);
                Log(L.T("Protokolle gespeichert in: ", "Log salvati in: ", "Logs saved to: ") + dir);
                MessageBox.Show(this, L.T("Protokolle gespeichert in:", "Log salvati in:", "Logs saved to:") + "\n\n" + dir, L.AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                SetBusy(false, null);
                MessageBox.Show(this, L.T("Protokolle konnten nicht gespeichert werden:", "Impossibile salvare i log:", "Logs could not be saved:") +
                    "\n\n" + ex.Message, L.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        async void OnUninstall(object sender, EventArgs e)
        {
            if (busy || !CheckPassword(L.T("Deinstallieren", "Disinstalla", "Uninstall"))) return;
            if (MessageBox.Show(this, L.T("FreshGuest wirklich deinstallieren?\n\nDer Schutz wird ausgeschaltet. Danach werden Gast-Daten nicht mehr gelöscht.",
                                          "Disinstallare davvero FreshGuest?\n\nLa protezione viene disattivata. Poi i dati degli ospiti non verranno più cancellati.",
                                          "Really uninstall FreshGuest?\n\nProtection is turned off. Guest data will no longer be deleted."),
                    L.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            SetBusy(true, L.T("Wird deinstalliert …", "Disinstallazione in corso …", "Uninstalling …"));
            log.Clear();
            int code = await Sys.RunScript("Rueckgaengig", Log);
            if (code != 0 && MessageBox.Show(this, L.T("Der Schutz konnte nicht sauber ausgeschaltet werden (siehe Protokoll).\n\nTrotzdem deinstallieren?",
                                                       "Non è stato possibile disattivare correttamente la protezione (vedi registro).\n\nDisinstallare comunque?",
                                                       "Protection could not be turned off cleanly (see log).\n\nUninstall anyway?"),
                    L.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                SetBusy(false, null);
                return;
            }
            try { if (BrowserSetup.Current(store) != BrowserOpt.None) BrowserSetup.Remove(store, Log); }
            catch (Exception ex) { Log(ex.Message); }
            try { if (GuestExtras.WallpaperOn(store)) GuestExtras.RemoveWallpaper(store, Log); }
            catch (Exception ex) { Log(ex.Message); }
            try { if (GuestExtras.AutostartOn(store)) GuestExtras.RemoveAutostart(store, Log); }
            catch (Exception ex) { Log(ex.Message); }
            GuestExtras.RemoveAllExtras(store, Log);
            try { SignIn.Remove(true, Log); } catch (Exception ex) { Log(ex.Message); }
            store.Delete();
            Sys.RemoveFiles(Log);
            busy = false;
            UseWaitCursor = false;
            AskReboot(L.T("FreshGuest wurde deinstalliert.", "FreshGuest è stato disinstallato.", "FreshGuest has been uninstalled."));
            Close();
        }
    }

    // ------------------------------------------------------------------
    // Bilder der Fenster für die Anleitung / zum Prüfen (nur Demo)
    // ------------------------------------------------------------------
    static class Snapshots
    {
        // Willkommen für die Bilder: in der persönlichen Fassung mit deren Vorgaben
        static WelcomeInfo DemoWelcome(Store st)
        {
            WelcomeInfo w = GuestExtras.WelcomeFromStore(st);
            if (Brand.HasDefaults) Brand.ApplyDefaults(w);
            return w;
        }

        public static void Run(string dir)
        {
            Program.CleanShots = true;
            Directory.CreateDirectory(dir);
            foreach (string code in L.Codes)
            {
                L.Code = code;
                string sfx = code == "de" ? "" : "-" + code;
                string file = Path.Combine(dir, "demo-zustand.txt");
                Store st = Store.OpenFile(file);
                st.Delete();

                Shot(new SetupForm(st), Path.Combine(dir, "1-einrichten" + sfx + ".png"));

                st.Set("Installiert", "1");
                st.Set("SchutzAn", "1");
                st.Save();
                Shot(new MainForm(st), Path.Combine(dir, "2-schutz-an" + sfx + ".png"));

                st.Set("SchutzAn", "0");
                Shot(new MainForm(st), Path.Combine(dir, "3-schutz-aus" + sfx + ".png"));

                st.MarkChanged(true);
                Shot(new MainForm(st), Path.Combine(dir, "4-neustart-noetig" + sfx + ".png"));

                Shot(new TestPasswordForm(), Path.Combine(dir, "5-kennwort" + sfx + ".png"));
                // Beispielwerte, damit die Bilder in der Anleitung aussagekräftig sind
                string sample = Path.Combine(dir, "beispiel-hintergrund.png");
                if (!File.Exists(sample)) MakeSampleWallpaper(sample);
                st.Set("Hintergrund", sample);
                st.Set("HintergrundStil", "4");
                st.Set("Autostart", L.T("https://www.meine-ferienwohnung.it/", "https://www.mio-appartamento.it/", "https://www.my-holiday-flat.com/"));
                st.Set("DesktopDateien", L.T("Hausinfos.pdf|Wetter Südtirol.url|VLC media player.lnk",
                    "Info casa.pdf|Meteo Alto Adige.url|VLC media player.lnk", "House info.pdf|Weather.url|VLC media player.lnk"));
                st.Set("Willkommen", "1");
                st.Set("W_WLAN", "Ferienwohnung-Ora");
                st.Set("W_WLANKennwort", "sonne2026");
                st.Set("W_Checkout", "10:00 Uhr"); st.Set("W_Checkout_it", "ore 10:00"); st.Set("W_Checkout_en", "10:00 am");
                st.Set("W_Kontakt", "+39 333 000 0000");
                st.Set("W_ExtraName", "Parkplatz"); st.Set("W_ExtraName_it", "Parcheggio"); st.Set("W_ExtraName_en", "Parking");
                st.Set("W_Extra", "Nr. 12 im Hof"); st.Set("W_Extra_it", "n. 12 nel cortile"); st.Set("W_Extra_en", "No. 12 in the yard");
                st.Set("W_Text", "Schönen Urlaub in Südtirol!"); st.Set("W_Text_it", "Buone vacanze in Alto Adige!"); st.Set("W_Text_en", "Enjoy your holiday in South Tyrol!");
                st.Set("W_Checkout_es", "10:00 h"); st.Set("W_ExtraName_es", "Aparcamiento"); st.Set("W_Extra_es", "n.º 12 en el patio"); st.Set("W_Text_es", "¡Felices vacaciones en el Tirol del Sur!");
                st.Set("W_Checkout_fr", "10h00"); st.Set("W_ExtraName_fr", "Parking"); st.Set("W_Extra_fr", "n° 12 dans la cour"); st.Set("W_Text_fr", "Bonnes vacances au Tyrol du Sud !"); st.Set("Sprache4", "nl-NL"); st.Set("W_Checkout_nl", "10.00 uur"); st.Set("W_Text_nl", "Fijne vakantie in Zuid-Tirol!");
                if (Brand.HasDefaults)      // persönliche Fassung: Bilder mit deren Vorgaben statt der Beispieltexte
                    foreach (string n in st.Keys) if (n.StartsWith("W_") && n != "W_WLANKennwort") st.Remove(n);
                st.Set("AbmeldenMin", "60");
                st.Set("NeustartZeit", "04:00");
                st.Set("Sperren", "1");
                st.Set("Sprachwahl", "1");
                Shot(new SettingsForm(st, 0), Path.Combine(dir, "6-einstellungen-browser" + sfx + ".png"));
                Shot(new SettingsForm(st, 1), Path.Combine(dir, "7-einstellungen-hintergrund" + sfx + ".png"));
                Shot(new SettingsForm(st, 4), Path.Combine(dir, "8-einstellungen-autostart" + sfx + ".png"));
                Shot(new SettingsForm(st, 3), Path.Combine(dir, "9-einstellungen-desktop" + sfx + ".png"));
                Shot(new SettingsForm(st, 5), Path.Combine(dir, "10-einstellungen-willkommen" + sfx + ".png"));
                Shot(new SettingsForm(st, 6), Path.Combine(dir, "11-einstellungen-abmelden" + sfx + ".png"));
                Shot(new SettingsForm(st, 7), Path.Combine(dir, "12-einstellungen-sperren" + sfx + ".png"));
                Shot(new SettingsForm(st, 8), Path.Combine(dir, "16-einstellungen-anmeldung" + sfx + ".png"));
                st.Set("GastSprache", "en-US");
                Shot(new SettingsForm(st, 9), Path.Combine(dir, "18-einstellungen-sprache" + sfx + ".png"));
                st.Set("Profilbild", Brand.HasPicture ? GuestExtras.BrandPic : GuestExtras.LogoPic);
                Shot(new SettingsForm(st, 2), Path.Combine(dir, "15-einstellungen-profilbild" + sfx + ".png"));
                Shot(new WelcomeForm(DemoWelcome(st), false), Path.Combine(dir, "13-gast-willkommen" + sfx + ".png"));
                Shot(new IdleWarnForm(""), Path.Combine(dir, "14-gast-noch-da" + sfx + ".png"));
                Shot(new NoticeForm(60, "04:00", false, null, false), Path.Combine(dir, "17-gast-hinweis" + sfx + ".png"));
                Shot(new LanguageForm(false), Path.Combine(dir, "19-gast-sprachwahl" + sfx + ".png"));
                Shot(new WelcomeForm(DemoWelcome(st), false, code == "de" ? "it" : code == "it" ? "de" : "es"),
                    Path.Combine(dir, "20-gast-willkommen-eine-sprache" + sfx + ".png"));
                if (code == "en")       // nur zur Kontrolle (nicht in der Anleitung): Französisch nach der Sprachwahl
                    Shot(new WelcomeForm(DemoWelcome(st), false, "fr"), Path.Combine(dir, "21-gast-willkommen-fr.png"));
                if (code == "en")
                {
                    Shot(new WelcomeForm(DemoWelcome(st), false, "nl"), Path.Combine(dir, "22-gast-willkommen-nl.png"));
                    Shot(new ExtraLangForm(new List<string> { "nl-NL", "pl-PL" }, "nl-NL"), Path.Combine(dir, "23-weitere-sprache.png"));
                }

                if (code == "de")
                {
                    // Protokoll-Export testen (Demo: nur lesen, schreibt nur in den Bilder-Ordner)
                    Sys.ExportLogs(st, dir, "Testtext aus dem Protokoll-Fenster").Wait();
                }
                st.Delete();
            }
            L.Code = "de";
        }

        class TestPasswordForm : Form
        {
            public TestPasswordForm()
            {
                Ui.Style(this, 380, 156);
                Text = L.T("Schutz ausschalten", "Disattiva protezione", "Turn protection off");
                Controls.Add(Ui.MakeLabel(PasswordForm.PromptText, 16, 14, 350, 22));
                Controls.Add(new TextBox { Location = new Point(18, 40), Width = 342, UseSystemPasswordChar = true, Text = "geheim" });
                Controls.Add(Ui.MakeButton("OK", 160, 106, 96, 34, true));
                Controls.Add(Ui.MakeButton(L.T("Abbrechen", "Annulla", "Cancel"), 264, 106, 96, 34, false));
            }
        }

        static void MakeSampleWallpaper(string path)
        {
            using (var bmp = new Bitmap(1600, 900))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                using (var br = new LinearGradientBrush(new Rectangle(0, 0, 1600, 900), Color.FromArgb(34, 35, 122), Color.FromArgb(8, 7, 30), 35f))
                    g.FillRectangle(br, 0, 0, 1600, 900);
                Image lockup = Logo.Lockup();
                if (lockup != null) g.DrawImage(lockup, 800 - 390, 450 - 100, 780, 200);
                bmp.Save(path, ImageFormat.Png);
            }
        }

        static void Shot(Form f, string path)
        {
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-5000, -5000);
            f.ShowInTaskbar = false;
            f.Show();
            Application.DoEvents();
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
                bmp.Save(path, ImageFormat.Png);
            }
            f.Close();
            f.Dispose();
        }
    }
}
