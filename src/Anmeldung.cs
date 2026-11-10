// FreshGuest – Anmeldebildschirm
// © 2026 PowerStore Computers
// SPDX-License-Identifier: GPL-3.0-or-later
//
// A) „Guest“ automatisch auswählen (Standard):
//    Windows wählt unter Shared PC immer „Anderer Benutzer“ vor – das lässt sich per Richtlinie nicht ändern.
//    Darum läuft ein kleiner Helfer direkt AUF dem Anmeldebildschirm (wie Fernwartungs-Programme) und wählt
//    dort die Kachel „Guest“ aus. Es wird nichts gesperrt.
// B) Nur „Guest“ anzeigen (Einstellung):
//    Richtlinie ExcludedCredentialProviders schließt alle Anmelde-Wege außer dem von Shared PC aus – die Kachel
//    „Anderer Benutzer“ verschwindet. Admin-Zugang: am Anmeldebildschirm Strg+Alt+F -> „Anderer Benutzer“
//    erscheint für 10 Minuten. Solange die Tastenkombination an diesem PC noch nie erfolgreich benutzt wurde,
//    schaltet sich B nach 3 Minuten am Anmeldebildschirm von selbst wieder aus (kein Aussperren möglich).
//
// Aufbau:
//   Aufgabe „FreshGuest-Anmeldung“ (SYSTEM, beim Start)  ->  FreshGuest.exe --anmeldewache   (Sitzung 0, läuft dauernd)
//   Wache startet bei jedem neuen Anmeldebildschirm      ->  FreshGuest.exe --anmeldehelfer  (winsta0\winlogon)
// Protokoll: C:\ProgramData\GaestePC\Anmeldung.log

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using Microsoft.Win32;

namespace GaesteSchutz
{
    static partial class SignIn
    {
        public const string TaskName = "FreshGuest-Anmeldung";
        public const string HotkeyText = "Strg + Alt + F";
        const string ConfigKey = @"SOFTWARE\FreshGuest";
        const string PolKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        const string PolName = "ExcludedCredentialProviders";
        const string CpRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers";
        const string SharedPcKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedPC";
        const string GuestCpName = "Windows.SharedPC.CredentialProvider";
        const int FreeMinutes = 10;          // so lange bleibt „Anderer Benutzer“ nach der Tastenkombination sichtbar
        const int UnconfirmedMinutes = 3;    // unbestätigt: nach so vielen Minuten am Anmeldebildschirm schaltet sich B aus

        // ------------------------------------------------------------------ Einstellungen
        static int CfgInt(string name, int def)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ConfigKey))
                {
                    if (k == null) return def;
                    int v;
                    return int.TryParse(Convert.ToString(k.GetValue(name, "")), out v) ? v : def;
                }
            }
            catch { return def; }
        }

        static void CfgSet(string name, object value)
        {
            using (RegistryKey k = Registry.LocalMachine.CreateSubKey(ConfigKey))
            {
                if (value == null) k.DeleteValue(name, false);
                else if (value is int) k.SetValue(name, (int)value, RegistryValueKind.DWord);
                else k.SetValue(name, Convert.ToString(value), RegistryValueKind.String);
            }
        }

        public static bool PreselectOn(Store s)
        {
            return Program.Demo ? s.Get("GastVorauswahl") != "0" : CfgInt("GastVorauswahl", 1) == 1;
        }
        public static bool GuestOnlyOn(Store s)
        {
            return Program.Demo ? s.Get("NurGuest") == "1" : CfgInt("NurGuest", 0) == 1;
        }
        public static bool Confirmed() { return !Program.Demo && CfgInt("NurGuestBestaetigt", 0) == 1; }

        public static string StatusText(Store s)
        {
            return (PreselectOn(s) ? "Guest automatisch auswählen" : "keine Vorauswahl") +
                   (GuestOnlyOn(s) ? ", nur Guest anzeigen (" + (Confirmed() ? "bestätigt" : "noch nicht bestätigt") + ")" : "") +
                   "   Aufgabe: " + (Program.Demo ? "-" : (TaskExists() ? "vorhanden" : "fehlt"));
        }

        // Einstellungen übernehmen (aus dem Einstellungs-Fenster)
        public static void Apply(Store store, bool preselect, bool guestOnly, Action<string> log)
        {
            if (Program.Demo)
            {
                store.Set("GastVorauswahl", preselect ? "1" : "0");
                store.Set("NurGuest", guestOnly ? "1" : "0");
                store.Save();
                log("DEMO: " + L.T("Anmeldebildschirm würde umgestellt.", "la schermata di accesso verrebbe modificata.", "sign-in screen would be changed."));
                return;
            }
            CfgSet("GastVorauswahl", preselect ? 1 : 0);
            CfgSet("NurGuest", guestOnly ? 1 : 0);
            CfgSet("NurGuestFrei", null);
            if (!guestOnly) RemovePolicy();
            Sync(store, log);
            log(preselect ? L.T("✔ Anmeldebildschirm: „Guest“ wird automatisch ausgewählt.", "✔ Schermata di accesso: «Guest» viene selezionato automaticamente.",
                                "✔ Sign-in screen: \"Guest\" is selected automatically.")
                          : L.T("Anmeldebildschirm: keine automatische Auswahl.", "Schermata di accesso: nessuna selezione automatica.",
                                "Sign-in screen: no automatic selection."));
            if (guestOnly)
            {
                log(L.T("✔ Anmeldebildschirm: nur „Guest“. Dein Zugang: direkt am Anmeldebildschirm Strg + Alt + F drücken (nicht als Gast anmelden).",
                        "✔ Schermata di accesso: solo «Guest». Il tuo accesso: premi Ctrl + Alt + F direttamente nella schermata di accesso (senza accedere come ospite).",
                        "✔ Sign-in screen: \"Guest\" only. Your access: press Ctrl + Alt + F directly on the sign-in screen (do not sign in as a guest)."));
                if (!Confirmed())
                    log(L.T("   Wichtig: Jetzt neu starten und die Tastenkombination einmal benutzen – sonst schaltet sich „Nur Guest“ nach 3 Minuten wieder aus.",
                            "   Importante: riavvia ora e usa una volta la combinazione di tasti – altrimenti «Solo Guest» si disattiva dopo 3 minuti.",
                            "   Important: restart now and use the key combination once – otherwise \"Guest only\" turns itself off after 3 minutes."));
                if (!store.Enabled)
                    log(L.T("   Gilt erst, wenn der Schutz eingeschaltet ist.", "   Vale solo con la protezione attiva.", "   Applies only while protection is on."));
            }
        }

        // Aufgabe passend zum Zustand anlegen oder entfernen (nach Einrichten, Update, Einstellungen)
        public static void Sync(Store store, Action<string> log)
        {
            if (Program.Demo) return;
            RemoveTestLeftovers();
            bool need = store.Enabled && File.Exists(Sys.InstalledExe) && (PreselectOn(store) || GuestOnlyOn(store) || GuestExtras.PictureOn(store) || GuestExtras.ChoiceOn(store));
            if (!need) { StopAndRemoveTask(); RemovePolicy(); return; }
            StopRunning();
            string xml = TaskXml(Sys.InstalledExe);
            string file = Path.Combine(Path.GetTempPath(), "FreshGuest-Anmeldung.xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            int rc = Sys.RunQuiet("schtasks.exe", "/Create /F /TN \"" + TaskName + "\" /XML \"" + file + "\"");
            try { File.Delete(file); } catch { }
            if (rc != 0)
                throw new InvalidOperationException(L.T("Die Aufgabe für den Anmeldebildschirm konnte nicht angelegt werden",
                    "Impossibile creare l'attività per la schermata di accesso", "Could not create the sign-in screen task") + " (schtasks " + rc + ").");
            Sys.RunQuiet("schtasks.exe", "/Run /TN \"" + TaskName + "\"");
        }

        // Schutz aus / Deinstallieren: alles zurück. Die Einstellungen bleiben (außer beim Deinstallieren).
        public static void Remove(bool uninstall, Action<string> log)
        {
            if (Program.Demo) return;
            StopAndRemoveTask();
            RemovePolicy();
            RemoveTestLeftovers();
            try { CfgSet("NurGuestFrei", null); } catch { }
            if (uninstall)
                foreach (string n in new[] { "GastVorauswahl", "NurGuest", "NurGuestBestaetigt", "KachelBildTest", "GastSprachwahl", "SprachTemp", "SprachTempVon", "SprachwunschOk", "SprachwechselAnmelden" })
                    try { CfgSet(n, null); } catch { }
            try
            {
                bool empty;
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ConfigKey))
                    empty = k != null && k.ValueCount == 0 && k.SubKeyCount == 0;
                if (empty) Registry.LocalMachine.DeleteSubKey(ConfigKey, false);
            }
            catch { }
        }

        static bool TaskExists() { try { return Sys.RunQuiet("schtasks.exe", "/Query /TN \"" + TaskName + "\"") == 0; } catch { return false; } }

        static void StopAndRemoveTask()
        {
            StopRunning();
            try { Sys.RunQuiet("schtasks.exe", "/Delete /F /TN \"" + TaskName + "\""); } catch { }
        }

        // Laufende Wache beenden (sonst lässt sich die exe nicht ersetzen/löschen). Der Helfer beendet sich selbst,
        // sobald seine Wache weg ist.
        public static void StopRunning()
        {
            if (Program.Demo) return;
            try { Sys.RunQuiet("schtasks.exe", "/End /TN \"" + TaskName + "\""); } catch { }
            int self = Process.GetCurrentProcess().Id;
            bool any = false;
            foreach (Process p in Process.GetProcessesByName("FreshGuest"))
            {
                try { if (p.Id != self && p.SessionId == 0) { p.Kill(); p.WaitForExit(3000); any = true; } }
                catch { }
                finally { p.Dispose(); }
            }
            if (any) Thread.Sleep(1200);     // Helfer bemerkt das Ende der Wache
        }

        // Reste der Test-Skripte (Gast-Vorauswahl-Test / Anmeldung-Test) entfernen
        static void RemoveTestLeftovers()
        {
            foreach (string t in new[] { "FreshGuest-Vorauswahl-Test", "FreshGuest-Anmeldung-Test-Rueckweg", "FreshGuest-Anmeldung-Test-Beobachter" })
                try { Sys.RunQuiet("schtasks.exe", "/Delete /F /TN \"" + t + "\""); } catch { }
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FreshGuest-Test");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { }
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\System", true))
                    if (k != null) { k.DeleteValue("DefaultCredentialProvider", false); k.DeleteValue(PolName, false); }
            }
            catch { }
        }

        static string TaskXml(string exe)
        {
            Func<string, string> x = s => System.Security.SecurityElement.Escape(s);
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>FreshGuest: Anmeldebildschirm (Guest automatisch auswaehlen)</Description></RegistrationInfo>\r\n" +
                "  <Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger></Triggers>\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
                "    <StartWhenAvailable>true</StartWhenAvailable>\r\n" +
                "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
                "    <Enabled>true</Enabled>\r\n" +
                "    <Hidden>false</Hidden>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                "    <Priority>4</Priority>\r\n" +
                "    <RestartOnFailure><Interval>PT1M</Interval><Count>10</Count></RestartOnFailure>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + x(exe) + "</Command><Arguments>--anmeldewache</Arguments></Exec></Actions>\r\n" +
                "</Task>\r\n";
        }

        // ------------------------------------------------------------------ Richtlinie (B)
        static string GuestCp()
        {
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(CpRoot))
                {
                    if (root == null) return null;
                    foreach (string n in root.GetSubKeyNames())
                        using (RegistryKey k = root.OpenSubKey(n))
                            if (k != null && string.Equals(Convert.ToString(k.GetValue("", "")), GuestCpName, StringComparison.OrdinalIgnoreCase)) return n;
                }
            }
            catch { }
            return null;
        }

        static bool SharedPcReady()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(SharedPcKey))
                    if (k == null || Convert.ToString(k.GetValue("SharedPCMode", "")) != "1") return false;
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(SharedPcKey + @"\AccountManagement"))
                    if (k == null || Convert.ToString(k.GetValue("AccountModel", "")) != "2") return false;   // ohne das keine Gast-Kachel
                return GuestCp() != null;
            }
            catch { return false; }
        }

        static bool PolicyPresent()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(PolKey))
                    return k != null && !string.IsNullOrEmpty(Convert.ToString(k.GetValue(PolName, "")));
            }
            catch { return false; }
        }

        static bool SetPolicy()
        {
            string guest = GuestCp();
            if (guest == null) return false;
            var list = new List<string>();
            using (RegistryKey root = Registry.LocalMachine.OpenSubKey(CpRoot))
                foreach (string n in root.GetSubKeyNames())
                    if (!string.Equals(n, guest, StringComparison.OrdinalIgnoreCase)) list.Add(n);
            if (list.Count == 0) return false;
            using (RegistryKey k = Registry.LocalMachine.CreateSubKey(PolKey))
                k.SetValue(PolName, string.Join(",", list.ToArray()), RegistryValueKind.String);
            return true;
        }

        public static void RemovePolicy()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(PolKey, true))
                    if (k != null) k.DeleteValue(PolName, false);
            }
            catch { }
        }

        // ------------------------------------------------------------------ Protokoll
        static readonly object logLock = new object();
        static void Log(string text)
        {
            try
            {
                lock (logLock)
                {
                    string file = Path.Combine(Sys.DataDir, "Anmeldung.log");
                    Directory.CreateDirectory(Sys.DataDir);
                    try { var fi = new FileInfo(file); if (fi.Exists && fi.Length > 300 * 1024) File.Delete(file); } catch { }
                    File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + text + "\r\n", new UTF8Encoding(false));
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------ Windows-Aufrufe
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
        [StructLayout(LayoutKind.Sequential)]
        struct LUID { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)]
        struct TOKEN_PRIVILEGES { public int Count; public LUID Luid; public int Attr; }
        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr tok);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr tok, uint access, IntPtr sa, int level, int type, out IntPtr dup);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr tok, int cls, ref uint info, int len);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string sys, string name, out LUID luid);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr tok, bool all, ref TOKEN_PRIVILEGES tp, int len, IntPtr prev, IntPtr ret);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcessAsUser(IntPtr tok, string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir,
            ref STARTUPINFO si, out PROCESS_INFORMATION pi);
        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WTSQuerySessionInformationW")]
        static extern bool WTSQuerySessionInformation(IntPtr server, uint session, int cls, out IntPtr buf, out int bytes);
        [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr p);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint thread);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr h, int idx, StringBuilder buf, int len, out int needed);

        static string SessionUser(uint session)
        {
            IntPtr buf; int bytes;
            if (!WTSQuerySessionInformation(IntPtr.Zero, session, 5 /* WTSUserName */, out buf, out bytes)) return "";
            try { return Marshal.PtrToStringUni(buf) ?? ""; }
            finally { WTSFreeMemory(buf); }
        }

        static int LogonUiPid(uint session)
        {
            int pid = 0;
            foreach (Process p in Process.GetProcessesByName("LogonUI"))
            {
                try { if ((uint)p.SessionId == session && pid == 0) pid = p.Id; } catch { }
                p.Dispose();
            }
            return pid;
        }

        static bool Alive(int pid)
        {
            try { using (Process p = Process.GetProcessById(pid)) return !p.HasExited; }
            catch { return false; }
        }

        // Gast = Konto, das Shared PC selbst angelegt hat
        static bool IsGuest(string user)
        {
            if (user.StartsWith("shpctac", StringComparison.OrdinalIgnoreCase)) return true;     // so nennt Shared PC seine Gast-Konten
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(SharedPcKey + @"\CreatedLocalAccounts"))
                    return k != null && k.GetSubKeyNames().Any(n => string.Equals(n, user, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        static void Privilege(IntPtr tok, string name)
        {
            var tp = new TOKEN_PRIVILEGES { Count = 1, Attr = 2 };
            if (LookupPrivilegeValue(null, name, out tp.Luid)) AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }

        // Startet eine Befehlszeile als SYSTEM in der Bildschirm-Sitzung auf dem Anmelde-Desktop
        static string StartOnLogonDesktop(uint session, string commandLine)
        {
            IntPtr tok, dup;
            if (!OpenProcessToken(GetCurrentProcess(), 0xF01FF, out tok)) return "OpenProcessToken " + Marshal.GetLastWin32Error();
            Privilege(tok, "SeTcbPrivilege"); Privilege(tok, "SeAssignPrimaryTokenPrivilege"); Privilege(tok, "SeIncreaseQuotaPrivilege");
            if (!DuplicateTokenEx(tok, 0xF01FF, IntPtr.Zero, 2, 1, out dup)) { CloseHandle(tok); return "DuplicateTokenEx " + Marshal.GetLastWin32Error(); }
            try
            {
                Privilege(dup, "SeTcbPrivilege");
                if (!SetTokenInformation(dup, 12 /* TokenSessionId */, ref session, 4)) return "SetTokenInformation " + Marshal.GetLastWin32Error();
                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                si.lpDesktop = @"winsta0\winlogon";
                si.dwFlags = 1; si.wShowWindow = 0;
                PROCESS_INFORMATION pi;
                if (!CreateProcessAsUser(dup, null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, null, ref si, out pi))
                    return "CreateProcessAsUser " + Marshal.GetLastWin32Error();
                CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
                return null;
            }
            finally { CloseHandle(dup); CloseHandle(tok); }
        }

        // ------------------------------------------------------------------ Wache (SYSTEM, Sitzung 0)
        static int lastUi, tries, killedPid;
        static DateTime? killedAt, lockedSince;
        static string lastState;
        static bool readyCache;
        static DateTime readyAt = DateTime.MinValue;

        public static int RunWatch()
        {
            bool created;
            using (var mutex = new Mutex(true, @"Global\FreshGuest-Anmeldewache", out created))
            {
                if (!created) return 0;
                Log("=== Wache gestartet (Version " + Program.Version + ", " + Environment.UserName + ")");
                while (true)
                {
                    int wait = 350;
                    try { wait = WatchTick(); }
                    catch (Exception ex) { Log("Wache FEHLER: " + ex.Message); wait = 3000; }
                    Thread.Sleep(wait);
                }
            }
        }

        // Rückgabe: Wartezeit bis zur nächsten Runde (ms)
        static int WatchTick()
        {
            uint session = WTSGetActiveConsoleSessionId();
            if (session == 0xFFFFFFFF) return 500;
            string user = SessionUser(session);
            bool nobody = user.Length == 0;
            int ui = nobody || killedAt != null ? LogonUiPid(session) : 0;      // nur am Anmeldebildschirm nötig
            bool preselect = CfgInt("GastVorauswahl", 1) == 1;
            bool guestOnly = CfgInt("NurGuest", 0) == 1;
            bool confirmed = CfgInt("NurGuestBestaetigt", 0) == 1;
            if ((DateTime.UtcNow - readyAt).TotalSeconds > 20) { readyCache = SharedPcReady(); readyAt = DateTime.UtcNow; }
            bool ready = readyCache;
            bool adminOn = !nobody && !IsGuest(user);

            // Freischaltung durch die Tastenkombination
            DateTime? free = FreeSince();
            if (free != null)
            {
                if (adminOn)
                {
                    if (!confirmed) { CfgSet("NurGuestBestaetigt", 1); confirmed = true; Log("Tastenkombination bestaetigt: Anmeldung von '" + user + "' nach Freischaltung."); }
                    CfgSet("NurGuestFrei", null); free = null;
                }
                else if ((DateTime.UtcNow - free.Value).TotalMinutes > FreeMinutes || free.Value > DateTime.UtcNow.AddMinutes(1))
                {
                    CfgSet("NurGuestFrei", null); free = null;
                    Log("Freischaltung abgelaufen (" + FreeMinutes + " Minuten ohne Anmeldung).");
                }
            }

            // Richtlinie passend setzen. Solange du (kein Gast) angemeldet bist, bleibt sie weg - sonst könntest du
            // den gesperrten PC (Win+L) nicht mehr entsperren.
            bool want = guestOnly && ready && free == null && !adminOn;
            bool have = PolicyPresent();
            if (want != have)
            {
                if (want) { if (!SetPolicy()) want = false; } else RemovePolicy();
                Log("Nur Guest: " + (want ? "EIN" : "AUS") + "  (Einstellung=" + guestOnly + ", Shared PC bereit=" + ready + ", frei=" + (free != null) +
                    ", angemeldet='" + user + "')");
                if (want != have && nobody && ui != 0 && killedAt == null)
                {
                    // Der Anmeldebildschirm liest die Richtlinie nur beim Start -> neu laden
                    try
                    {
                        using (Process p = Process.GetProcessById(ui)) p.Kill();
                        killedAt = DateTime.UtcNow; killedPid = ui;
                        Log("Anmeldebildschirm neu geladen (LogonUI " + ui + ").");
                    }
                    catch (Exception ex) { Log("Anmeldebildschirm neu laden FEHLER: " + ex.Message); }
                    return 250;
                }
                have = want;
            }

            // Kommt der Anmeldebildschirm nach dem Neuladen nicht wieder -> neu starten
            if (killedAt != null)
            {
                double sec = (DateTime.UtcNow - killedAt.Value).TotalSeconds;
                if (!nobody || (ui != 0 && ui != killedPid)) killedAt = null;                 // ist wieder da (oder jemand ist angemeldet)
                else if (ui == killedPid && sec > 5) killedAt = null;                          // wurde gar nicht beendet
                else if (ui == 0 && sec > 15)
                {
                    killedAt = null;
                    Log("Anmeldebildschirm kam nicht wieder -> Neustart.");
                    try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /f /t 0") { UseShellExecute = false, CreateNoWindow = true }); } catch { }
                    return 20000;
                }
                else return 250;
            }

            // Sicherheit: Solange die Tastenkombination hier noch nie funktioniert hat, schaltet sich „Nur Guest“
            // nach 3 Minuten am Anmeldebildschirm selbst aus.
            if (guestOnly && !confirmed && have && nobody && ui != 0)
            {
                if (lockedSince == null) lockedSince = DateTime.UtcNow;
                else if ((DateTime.UtcNow - lockedSince.Value).TotalMinutes >= UnconfirmedMinutes)
                {
                    lockedSince = null;
                    CfgSet("NurGuest", 0);
                    Log("Nur Guest hat sich ausgeschaltet: Die Tastenkombination wurde in " + UnconfirmedMinutes + " Minuten nicht benutzt (noch nie bestaetigt).");
                    return 100;     // nächste Runde entfernt die Richtlinie und lädt den Anmeldebildschirm neu
                }
            }
            else lockedSince = null;

            // Sprachwahl der Gäste: Wunsch übernehmen bzw. danach die Standardsprache zurück
            try { LanguageTick(user, nobody, adminOn); } catch (Exception ex) { Log("Sprachwahl FEHLER: " + ex.Message); }

            // Neuer Anmeldebildschirm ohne angemeldeten Benutzer -> Helfer starten (einmal je Anmeldebildschirm)
            if (!nobody) lastUi = 0;
            else if (ui != 0 && ui != lastUi && ready && (preselect || have || TilePic() || CfgInt("SprachwechselAnmelden", 0) == 1))
            {
                string cmd = "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --anmeldehelfer " + Process.GetCurrentProcess().Id + " " + ui;
                string err = StartOnLogonDesktop(session, cmd);
                if (err == null) { lastUi = ui; tries = 0; }
                else
                {
                    Log("Helfer konnte nicht gestartet werden: " + err);
                    if (++tries >= 5) { lastUi = ui; tries = 0; }
                }
            }

            string state = "angemeldet='" + user + "' Anmeldebildschirm=" + (ui != 0 ? ui.ToString() : "-") + " NurGuest=" + have;
            if (state != lastState) { lastState = state; Log("Zustand: " + state); }
            return nobody ? 350 : 1000;
        }

        static DateTime? FreeSince()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ConfigKey))
                {
                    long t;
                    if (k == null || !long.TryParse(Convert.ToString(k.GetValue("NurGuestFrei", "")), out t) || t <= 0) return null;
                    return new DateTime(t, DateTimeKind.Utc);
                }
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ Helfer (SYSTEM, auf dem Anmelde-Desktop)
        public static int RunHelper(string[] args)
        {
            int parent = 0, ui = 0;
            int i = Array.IndexOf(args, "--anmeldehelfer");
            if (i >= 0 && i + 2 < args.Length) { int.TryParse(args[i + 1], out parent); int.TryParse(args[i + 2], out ui); }
            if (parent == 0 || ui == 0) return 1;
            try
            {
                var sb = new StringBuilder(256); int n;
                GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, sb, 512, out n);
                bool locked = PolicyPresent();
                bool free = FreeSince() != null;
                Func<bool> stop = () => !Alive(parent) || !Alive(ui);
                bool overlay = TilePic();
                if (overlay) StartOverlay(stop);          // Gast-Profilbild über der Kachel „Guest“
                if (CfgInt("SprachwechselAnmelden", 0) == 1) AutoSubmit(stop, locked);     // Gast hat die Sprache gewechselt
                if (locked) { Log("Helfer (" + sb + "): warte auf " + HotkeyText); WaitHotkey(stop); }
                else if (free) Log("Helfer (" + sb + "): freigeschaltet - keine Vorauswahl");
                else if (CfgInt("GastVorauswahl", 1) == 1) Preselect(stop, sb.ToString());
                while (overlay && !stop()) Thread.Sleep(500);     // Bild bleibt, solange dieser Anmeldebildschirm steht
            }
            catch (Exception ex) { Log("Helfer FEHLER: " + ex.Message); }
            return 0;
        }

        // B: Strg+Alt+F am Anmeldebildschirm -> Freischaltung anfordern (die Wache macht den Rest)
        static void WaitHotkey(Func<bool> stop)
        {
            const int id = 0x4647;
            bool reg = RegisterHotKey(IntPtr.Zero, id, 0x0001 /* ALT */ | 0x0002 /* CTRL */ | 0x4000 /* kein Wiederholen */, 0x46);
            if (!reg) Log("Helfer: RegisterHotKey nicht moeglich (" + Marshal.GetLastWin32Error() + ") - nur Tasten-Abfrage.");
            try
            {
                int round = 0;
                while (true)
                {
                    bool hit = false;
                    MSG m;
                    while (PeekMessage(out m, IntPtr.Zero, 0, 0, 1)) if (m.message == 0x0312 && (int)m.wParam == id) hit = true;
                    if (!hit && (GetAsyncKeyState(0x11) & 0x8000) != 0 && (GetAsyncKeyState(0x12) & 0x8000) != 0 && (GetAsyncKeyState(0x46) & 0x8000) != 0) hit = true;
                    if (hit)
                    {
                        CfgSet("NurGuestFrei", DateTime.UtcNow.Ticks.ToString());
                        Log("Helfer: " + HotkeyText + " gedrueckt -> 'Anderer Benutzer' fuer " + FreeMinutes + " Minuten freigeschaltet.");
                        return;
                    }
                    if (++round % 10 == 0 && stop()) return;
                    Thread.Sleep(40);
                }
            }
            finally { if (reg) UnregisterHotKey(IntPtr.Zero, id); }
        }

        // A: Kachel „Guest“ auswählen
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Preselect(Func<bool> stop, string desktop)
        {
            DateTime start = DateTime.UtcNow;
            bool dumped = false;
            int selects = 0;
            while ((DateTime.UtcNow - start).TotalMinutes < 5)
            {
                if (stop()) return;
                string what;
                int r = TrySelectGuest(out what);
                if (r == 1)
                {
                    selects++;
                    Log("Helfer (" + desktop + "): 'Guest' ausgewaehlt nach " + (int)(DateTime.UtcNow - start).TotalMilliseconds + " ms  [" + what + "]");
                    // kurz pruefen, ob die Auswahl haelt (beim Aufbau des Anmeldebildschirms)
                    DateTime check = DateTime.UtcNow;
                    bool again = false;
                    while ((DateTime.UtcNow - check).TotalMilliseconds < 1500 && !again)
                    {
                        Thread.Sleep(250);
                        if (stop()) return;
                        string w2;
                        if (TrySelectGuest(out w2) == 1) again = true;
                    }
                    if (!again || selects >= 3) return;
                    Log("Helfer: Auswahl wurde zurueckgesetzt -> erneut ausgewaehlt.");
                    continue;
                }
                if (r == 2) { if (selects == 0) Log("Helfer (" + desktop + "): 'Guest' ist bereits ausgewaehlt  [" + what + "]"); return; }
                if (!dumped && (DateTime.UtcNow - start).TotalSeconds > 20)
                {
                    dumped = true;
                    Log("Helfer (" + desktop + "): 'Guest' nach 20 s nicht gefunden (" + what + "). Anmeldebildschirm zeigt:");
                    try { foreach (string line in DumpTree()) Log("   " + line); } catch (Exception ex) { Log("   " + ex.Message); }
                }
                Thread.Sleep(200);
            }
            Log("Helfer: beendet - 'Guest' wurde nicht gefunden.");
        }

        static readonly Regex GuestName = new Regex(@"^(Guest|Gast|Ospite|Invitado|Invit\u00e9|Convidado|Go\u015b\u0107|Vend\u00e9g|G\u00e6st|G\u00e4st|Gjest|Vieras|Misafir|\u0413\u043e\u0441\u0442\u044c)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // 0 = (noch) nicht gefunden, 1 = jetzt ausgewählt, 2 = war schon ausgewählt
        [MethodImpl(MethodImplOptions.NoInlining)]
        static int TrySelectGuest(out string what)
        {
            what = "";
            TreeWalker w = TreeWalker.RawViewWalker;
            int count = 0;
            AutomationElement list = FindById(w, AutomationElement.RootElement, "UsersList", 0, ref count);
            if (list == null) { what = "keine Kachel-Liste, " + count + " Elemente"; return 0; }
            var items = new List<AutomationElement>();
            for (AutomationElement c = w.GetFirstChild(list); c != null; c = w.GetNextSibling(c)) items.Add(c);
            AutomationElement guest = null;
            foreach (AutomationElement it in items)
                if (Texts(w, it, 0).Any(t => GuestName.IsMatch(t.Trim()))) { guest = it; break; }
            if (guest == null && items.Count == 2 && !HasImage(w, items[0]) && HasImage(w, items[1])) guest = items[1];   // andere Windows-Sprache
            if (guest == null) { what = items.Count + " Kacheln, keine heisst Guest"; return 0; }
            what = Safe(() => guest.Current.Name);
            object p;
            if (guest.TryGetCurrentPattern(SelectionItemPattern.Pattern, out p))
            {
                var sp = (SelectionItemPattern)p;
                if (sp.Current.IsSelected) return 2;
                sp.Select();
                return 1;
            }
            if (guest.TryGetCurrentPattern(InvokePattern.Pattern, out p)) { ((InvokePattern)p).Invoke(); return 1; }
            what += " (nicht auswaehlbar)";
            return 0;
        }

        static string Safe(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }

        static AutomationElement FindById(TreeWalker w, AutomationElement e, string id, int depth, ref int count)
        {
            if (e == null || depth > 12 || count > 400) return null;
            count++;
            if (Safe(() => e.Current.AutomationId) == id) return e;
            AutomationElement c = null;
            try { c = w.GetFirstChild(e); } catch { }
            while (c != null)
            {
                AutomationElement hit = FindById(w, c, id, depth + 1, ref count);
                if (hit != null) return hit;
                try { c = w.GetNextSibling(c); } catch { c = null; }
            }
            return null;
        }

        static IEnumerable<string> Texts(TreeWalker w, AutomationElement e, int depth)
        {
            var res = new List<string>();
            if (depth > 3) return res;
            AutomationElement c = null;
            try { c = w.GetFirstChild(e); } catch { }
            while (c != null)
            {
                AutomationElement cur = c;
                if (Safe(() => cur.Current.ControlType.ProgrammaticName) == "ControlType.Text") res.Add(Safe(() => cur.Current.Name));
                res.AddRange(Texts(w, cur, depth + 1));
                try { c = w.GetNextSibling(c); } catch { c = null; }
            }
            return res;
        }

        static bool HasImage(TreeWalker w, AutomationElement e)
        {
            AutomationElement c = null;
            try { c = w.GetFirstChild(e); } catch { }
            while (c != null)
            {
                AutomationElement cur = c;
                if (Safe(() => cur.Current.ControlType.ProgrammaticName) == "ControlType.Image") return true;
                try { c = w.GetNextSibling(c); } catch { c = null; }
            }
            return false;
        }

        static List<string> DumpTree()
        {
            var lines = new List<string>();
            DumpNode(TreeWalker.RawViewWalker, AutomationElement.RootElement, 0, lines);
            return lines;
        }

        static void DumpNode(TreeWalker w, AutomationElement e, int depth, List<string> lines)
        {
            if (e == null || depth > 12 || lines.Count > 150) return;
            lines.Add(new string(' ', depth * 2) + "[" + Safe(() => e.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")) + "] Name=\"" +
                      Safe(() => e.Current.Name) + "\" Id=\"" + Safe(() => e.Current.AutomationId) + "\"");
            AutomationElement c = null;
            try { c = w.GetFirstChild(e); } catch { }
            while (c != null)
            {
                DumpNode(w, c, depth + 1, lines);
                try { c = w.GetNextSibling(c); } catch { c = null; }
            }
        }
    }
}
