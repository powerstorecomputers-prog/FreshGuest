// © 2026 PowerStore Computers
// SPDX-License-Identifier: GPL-3.0-or-later
// FreshGuest – Zusatzfunktionen für Gäste (ab Version 1.5)
//   Desktop      : Dateien/Verknüpfungen auf den Desktop jedes neuen Gastes (Windows-Standardprofil)
//   Willkommen   : Fenster beim Anmelden mit WLAN, Check-out, Kontakt + Hinweis „alles wird gelöscht“
//   Abmelden     : Gast nach X Minuten ohne Benutzung abmelden (mit 60-Sekunden-Warnung)
//   Neustart     : PC jeden Tag zu einer festen Uhrzeit neu starten (geplante Aufgabe als SYSTEM)
//   Sperren      : Systemsteuerung/Einstellungen bzw. Eingabeaufforderung/Registry-Editor für Gäste sperren
//   Drucker      : Standarddrucker für Gäste
// Alles, was nur Gäste betrifft, steht im Windows-Standardprofil (C:\Users\Default) oder wird beim
// Anmelden von "FreshGuest.exe --gast-start" erledigt – dein eigenes Konto bleibt unverändert.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GaesteSchutz
{
    // Ein Eintrag der Desktop-Liste: Name auf dem Desktop + Quelle (null = liegt schon dort)
    class DeskItem
    {
        public string Name, Source;
        public DeskItem(string name, string source) { Name = name; Source = source; }
        public override string ToString() { return Name; }
    }

    // Was das Willkommensfenster zeigt
    class WelcomeInfo
    {
        public string Wlan = "", WlanPw = "";
        public int IdleMin;
        public string RestartTime = "";      // täglicher Neustart (für den Hinweis), leer = aus
        public string Lang4 = "";            // zusätzliche Sprache („+“ in den Einstellungen), z. B. "nl-NL" – leer = keine
        public bool LangChoice;              // Knöpfe „Windows: Deutsch / Italiano / English“ zeigen

        // Felder je Sprache. "de" ist die Grundlage: Bleibt ein Feld in einer anderen Sprache leer, gilt der deutsche Eintrag.
        // ExtraName/Extra = frei benennbares Zusatzfeld (z. B. „Parkplatz“).
        public static readonly string[] LangFields = { "Checkout", "Kontakt", "Text", "ExtraName", "Extra" };
        // "fr" = Französisch: eigene Felder (Reiter „Français“), gezeigt nur französischen Gästen nach der Sprachwahl
        public static readonly string[] Slots = { "de", "it", "en", "es", "fr" };
        readonly Dictionary<string, string> vals = new Dictionary<string, string>();

        public string Raw(string field, string slot) { string v; return vals.TryGetValue(field + "|" + slot, out v) ? v : ""; }
        public void Set(string field, string slot, string value)
        {
            value = (value ?? "").Trim();
            if (value.Length == 0) vals.Remove(field + "|" + slot); else vals[field + "|" + slot] = value;
        }
        // Leer: bei Französisch und der zusätzlichen Sprache erst der englische, sonst gleich der deutsche Eintrag
        public string Get(string field, string slot)
        {
            string v = Raw(field, slot);
            if (v.Length == 0 && slot != "de" && slot != "it" && slot != "es") v = Raw(field, "en");
            return v.Length > 0 ? v : Raw(field, "de");
        }

        // "nl-NL" -> "nl"
        public static string SlotOf(string tag) { return (tag ?? "").Split('-')[0].ToLowerInvariant(); }

        // Feste Sprachen + die zusätzliche (falls gewählt)
        public List<string> AllSlots
        {
            get
            {
                var l = Slots.ToList();
                if (GuestTexts.IsExtra(Lang4)) l.Add(SlotOf(Lang4));
                return l;
            }
        }

        // Name in Registry/Speicher: W_Checkout (Deutsch), W_Checkout_it, W_Checkout_en, W_Checkout_4
        public static string Key(string field, string slot) { return "W_" + field + (slot == "de" ? "" : "_" + slot); }

        public bool SameTexts(WelcomeInfo o)
        {
            if (Wlan != o.Wlan || WlanPw != o.WlanPw || Lang4 != o.Lang4) return false;
            foreach (string f in LangFields)
                foreach (string s in AllSlots.Union(o.AllSlots))
                    if (Raw(f, s) != o.Raw(f, s)) return false;
            return true;
        }
    }

    static partial class GuestExtras
    {
        // ======================================================================
        // Gemeinsam: Autostart-Verknüpfung "--gast-start" für alle Gast-Funktionen
        // ======================================================================
        static int ConfigInt(RegistryKey k, string name)
        {
            if (k == null) return 0;
            int v;
            return int.TryParse(Convert.ToString(k.GetValue(name, "")), out v) ? v : 0;
        }

        // Verknüpfung im Autostart-Ordner nur, wenn mindestens eine Gast-Funktion an ist
        static void UpdateStartupLink()
        {
            bool need = StartLinkNeeded();
            SyncFastStart(need);
            if (need) Sys.CreateShortcut(StartupLnk, Sys.InstalledExe, "--gast-start");
            else
            {
                try { if (File.Exists(StartupLnk)) File.Delete(StartupLnk); } catch { }
                try
                {
                    bool empty;
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ConfigKey))
                        empty = k != null && k.ValueCount == 0 && k.SubKeyCount == 0;
                    if (empty) Registry.LocalMachine.DeleteSubKey(ConfigKey, false);
                }
                catch { }
            }
        }

        // Gast-Fenster (Flaggen, Willkommen) sollen gleich nach dem Anmelden kommen:
        // - Windows startet den Autostart-Ordner sonst erst mit Verzögerung (Explorer\Serialize: StartupDelayInMSec,
        //   WaitForIdleState) – steht nur im Standardprofil, gilt also nur für Gäste.
        // - Die Animation bei der ersten Anmeldung („Hallo“ … „Wir bereiten alles vor“) fällt weg. Jeder Gast meldet
        //   sich zum ersten Mal an, mit Sprachwahl sogar zweimal. Nur gesetzt, wenn der Wert noch nicht da war.
        const string SerializeKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize";
        const string SystemPolicy = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

        public static void SyncFastStart(bool on)
        {
            bool have;
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ConfigKey)) have = ConfigInt(k, "SchnellStart") == 1;
            if (on == have || Program.Demo) return;
            try
            {
                WithDefaultHive(root =>
                {
                    if (on)
                        using (RegistryKey k = root.CreateSubKey(SerializeKey))
                        {
                            k.SetValue("StartupDelayInMSec", 0, RegistryValueKind.DWord);
                            k.SetValue("WaitForIdleState", 0, RegistryValueKind.DWord);
                        }
                    else
                        using (RegistryKey k = root.OpenSubKey(SerializeKey, true))
                            if (k != null) { k.DeleteValue("StartupDelayInMSec", false); k.DeleteValue("WaitForIdleState", false); }
                });
                using (RegistryKey p = Registry.LocalMachine.CreateSubKey(SystemPolicy))
                using (RegistryKey c = Registry.LocalMachine.CreateSubKey(ConfigKey))
                {
                    if (on)
                    {
                        if (p.GetValue("EnableFirstLogonAnimation") == null)
                        {
                            p.SetValue("EnableFirstLogonAnimation", 0, RegistryValueKind.DWord);
                            c.SetValue("SchnellStartAnimation", "1", RegistryValueKind.String);
                        }
                        c.SetValue("SchnellStart", "1", RegistryValueKind.String);
                    }
                    else
                    {
                        if (ConfigInt(c, "SchnellStartAnimation") == 1) p.DeleteValue("EnableFirstLogonAnimation", false);
                        c.DeleteValue("SchnellStartAnimation", false);
                        c.DeleteValue("SchnellStart", false);
                    }
                }
                if (!on)
                {
                    bool empty;
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ConfigKey))
                        empty = k != null && k.ValueCount == 0 && k.SubKeyCount == 0;
                    if (empty) Registry.LocalMachine.DeleteSubKey(ConfigKey, false);
                }
            }
            catch { }
        }

        public static bool StartLinkNeeded()
        {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ConfigKey))
                return k != null && (!string.IsNullOrEmpty(Convert.ToString(k.GetValue("GastAutostart", ""))) ||
                                     ConfigInt(k, "GastWillkommen") == 1 || ConfigInt(k, "GastAbmeldenMin") > 0 ||
                                     ConfigInt(k, "GastProfilbild") == 1 || ConfigInt(k, "GastHinweis") == 1 || ConfigInt(k, "GastSprachwahl") == 1);
        }

        // ======================================================================
        // Desktop für Gäste
        // ======================================================================
        public static string DefaultDesktop { get { return Path.Combine(Path.GetDirectoryName(DefaultHivePath), "Desktop"); } }

        public static List<string> DesktopNames(Store s)
        {
            return (s.Get("DesktopDateien") ?? "").Split('|').Where(n => n.Length > 0).ToList();
        }

        // Name, unter dem eine Quelle auf dem Desktop landet (Programme als Verknüpfung)
        public static string DesktopNameFor(string source)
        {
            return source.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(source) + ".lnk" : Path.GetFileName(source);
        }

        // Ziel einer Verknüpfung (oder null)
        internal static string ShortcutTarget(string lnk)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(t);
                try
                {
                    object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                    string target = Convert.ToString(sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null));
                    Marshal.FinalReleaseComObject(sc);
                    return target;
                }
                finally { Marshal.FinalReleaseComObject(shell); }
            }
            catch { return null; }
        }

        // Liegt das Ziel im Benutzerordner eines bestimmten Kontos? Dann können Gäste es nicht öffnen.
        static bool InPrivateProfile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string users = Path.GetDirectoryName(Path.GetDirectoryName(DefaultHivePath)) + "\\";
            if (!path.StartsWith(users, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = path.Substring(users.Length);
            return !rest.StartsWith("Public\\", StringComparison.OrdinalIgnoreCase) && !rest.StartsWith("Default\\", StringComparison.OrdinalIgnoreCase);
        }

        public static void ApplyDesktop(Store store, List<DeskItem> items, Action<string> log)
        {
            string dir = DefaultDesktop;
            List<string> old = DesktopNames(store);
            var names = items.Select(i => i.Name).ToList();
            if (Program.Demo)
            {
                log("DEMO: " + L.T("Desktop für Gäste würde gesetzt: ", "il desktop per gli ospiti verrebbe impostato: ",
                    "guest desktop would be set: ") + (names.Count == 0 ? "-" : string.Join(", ", names.ToArray())));
            }
            else
            {
                Directory.CreateDirectory(dir);
                foreach (string n in old.Where(o => !names.Contains(o, StringComparer.OrdinalIgnoreCase)))
                    try { File.Delete(Path.Combine(dir, n)); log(L.T("  entfernt: ", "  rimosso: ", "  removed: ") + n); }
                    catch (Exception ex) { log("  " + n + ": " + ex.Message); }
                foreach (DeskItem it in items.Where(i => i.Source != null))
                {
                    string dest = Path.Combine(dir, it.Name);
                    if (it.Source.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        Sys.CreateShortcut(dest, it.Source, null);
                    else
                        File.Copy(it.Source, dest, true);
                    log(L.T("  hinzugefügt: ", "  aggiunto: ", "  added: ") + it.Name);
                    string target = it.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(dest) : it.Source;
                    if (InPrivateProfile(target))
                        log("⚠ " + it.Name + L.T(": Ziel liegt in einem persönlichen Benutzerordner – Gäste können es nicht öffnen.",
                            ": la destinazione è in una cartella utente personale – gli ospiti non possono aprirla.",
                            ": the target is in a personal user folder – guests can't open it."));
                }
            }
            if (names.Count == 0) store.Remove("DesktopDateien"); else store.Set("DesktopDateien", string.Join("|", names.ToArray()));
            store.Save();
            log(names.Count == 0
                ? L.T("Desktop für Gäste: keine eigenen Dateien.", "Desktop per gli ospiti: nessun file proprio.", "Guest desktop: no own files.")
                : L.T("✔ Desktop für Gäste: ", "✔ Desktop per gli ospiti: ", "✔ Guest desktop: ") + names.Count +
                  L.T(" Einträge (ab dem nächsten Gast).", " elementi (dal prossimo ospite).", " items (from the next guest)."));
        }

        // ======================================================================
        // Willkommensfenster + automatisch abmelden (laufen in der Gast-Sitzung)
        // ======================================================================
        public static bool WelcomeOn(Store s) { return s.Get("Willkommen") == "1"; }
        public static bool NoticeOn(Store s) { return s.Get("Hinweis") == "1"; }
        public static int IdleMinutes(Store s) { int v; return int.TryParse(s.Get("AbmeldenMin"), out v) ? v : 0; }
        public static string Lang4(Store s) { string v = s.Get("Sprache4"); return GuestTexts.IsExtra(v) ? v : ""; }

        public static WelcomeInfo WelcomeFromStore(Store s)
        {
            var w = new WelcomeInfo { Wlan = s.Get("W_WLAN") ?? "", WlanPw = s.Get("W_WLANKennwort") ?? "", IdleMin = IdleMinutes(s),
                RestartTime = RestartTime(s) ?? "", Lang4 = Lang4(s), LangChoice = s.Get("Sprachwahl") == "1" };
            foreach (string f in WelcomeInfo.LangFields)
                foreach (string slot in w.AllSlots) w.Set(f, slot, s.Get(WelcomeInfo.Key(f, slot)));
            return w;
        }

        // Alle Werte des Willkommensfensters: Name -> Inhalt (leer = entfernen)
        static Dictionary<string, string> WelcomeValues(WelcomeInfo w)
        {
            var d = new Dictionary<string, string>();
            d["W_WLAN"] = w.Wlan.Trim();
            d["W_WLANKennwort"] = w.WlanPw.Trim();
            foreach (string f in WelcomeInfo.LangFields)
                foreach (string slot in w.AllSlots) d[WelcomeInfo.Key(f, slot)] = w.Raw(f, slot);
            return d;
        }

        public static void ApplyGuestSession(Store store, bool welcomeOn, WelcomeInfo w, int idleMin, bool noticeOn, Action<string> log)
        {
            Dictionary<string, string> vals = WelcomeValues(w);
            string lang4 = GuestTexts.IsExtra(w.Lang4) ? w.Lang4 : "";
            if (!Program.Demo)
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(ConfigKey))
                {
                    if (welcomeOn) k.SetValue("GastWillkommen", "1", RegistryValueKind.String); else k.DeleteValue("GastWillkommen", false);
                    foreach (var kv in vals)
                        if (welcomeOn && kv.Value.Length > 0) k.SetValue(kv.Key, kv.Value, RegistryValueKind.String);
                        else k.DeleteValue(kv.Key, false);
                    foreach (string n in k.GetValueNames())         // Texte einer entfernten zusätzlichen Sprache
                        if (n.StartsWith("W_", StringComparison.Ordinal) && !vals.ContainsKey(n)) k.DeleteValue(n, false);
                    if (idleMin > 0) k.SetValue("GastAbmeldenMin", idleMin.ToString(), RegistryValueKind.String);
                    else k.DeleteValue("GastAbmeldenMin", false);
                    if (noticeOn) k.SetValue("GastHinweis", "1", RegistryValueKind.String);
                    else k.DeleteValue("GastHinweis", false);
                    if (lang4.Length > 0) k.SetValue("GastSprache4", lang4, RegistryValueKind.String);
                    else k.DeleteValue("GastSprache4", false);
                }
                UpdateStartupLink();
            }
            if (welcomeOn) store.Set("Willkommen", "1"); else store.Remove("Willkommen");
            foreach (var kv in vals)
                if (kv.Value.Length > 0) store.Set(kv.Key, kv.Value); else store.Remove(kv.Key);
            foreach (string n in store.Keys)
                if (n.StartsWith("W_", StringComparison.Ordinal) && !vals.ContainsKey(n)) store.Remove(n);
            if (idleMin > 0) store.Set("AbmeldenMin", idleMin.ToString()); else store.Remove("AbmeldenMin");
            if (noticeOn) store.Set("Hinweis", "1"); else store.Remove("Hinweis");
            if (lang4.Length > 0) store.Set("Sprache4", lang4); else store.Remove("Sprache4");
            store.Save();
            string sprachen = string.Join(", ", GuestTexts.All.Select(l => l.Name).ToArray());
            log(welcomeOn ? L.T("✔ Willkommensfenster für Gäste an.", "✔ Finestra di benvenuto per gli ospiti attiva.", "✔ Guest welcome window on.")
                          : L.T("Willkommensfenster für Gäste aus.", "Finestra di benvenuto per gli ospiti disattivata.", "Guest welcome window off."));
            log(idleMin > 0 ? string.Format(L.T("✔ Bei Gästen fährt der PC nach {0} Minuten ohne Benutzung herunter.",
                                                "✔ Con gli ospiti il PC si spegne dopo {0} minuti di inattività.",
                                                "✔ With guests the PC shuts down after {0} minutes without use."), idleMin)
                            : L.T("Automatisches Herunterfahren aus.", "Spegnimento automatico disattivato.", "Automatic shutdown off."));
            log(noticeOn ? L.T("✔ Hinweis für Gäste beim Anmelden an.", "✔ Avviso per gli ospiti all'accesso attivo.", "✔ Guest notice at sign-in on.")
                         : L.T("Hinweis für Gäste aus.", "Avviso per gli ospiti disattivato.", "Guest notice off."));
            log(L.T("Sprachen der Gast-Fenster: ", "Lingue delle finestre per gli ospiti: ", "Languages of the guest windows: ") + sprachen);
        }

        // Läuft beim Anmelden JEDES Benutzers (Autostart-Ordner), ohne Administrator-Rechte.
        // Macht nur bei Gästen etwas: Programm/Seite starten, Willkommensfenster, Abmelde-Wächter.
        public static void RunGuestStart()
        {
            try
            {
                string target;
                bool welcome, picture, notice;
                int idle;
                var w = new WelcomeInfo();
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ConfigKey))
                {
                    if (k == null) return;
                    target = Convert.ToString(k.GetValue("GastAutostart", ""));
                    welcome = ConfigInt(k, "GastWillkommen") == 1;
                    idle = ConfigInt(k, "GastAbmeldenMin");
                    picture = ConfigInt(k, "GastProfilbild") == 1;
                    notice = ConfigInt(k, "GastHinweis") == 1;
                    w.LangChoice = ConfigInt(k, "GastSprachwahl") == 1;
                    w.RestartTime = Convert.ToString(k.GetValue("GastNeustartZeit", ""));
                    w.Wlan = Convert.ToString(k.GetValue("W_WLAN", ""));
                    w.WlanPw = Convert.ToString(k.GetValue("W_WLANKennwort", ""));
                    string l4 = Convert.ToString(k.GetValue("GastSprache4", ""));
                    w.Lang4 = GuestTexts.IsExtra(l4) ? l4 : "";
                    foreach (string f in WelcomeInfo.LangFields)
                        foreach (string slot in w.AllSlots) w.Set(f, slot, Convert.ToString(k.GetValue(WelcomeInfo.Key(f, slot), "")));
                    w.IdleMin = idle;
                }
                if (string.IsNullOrEmpty(target) && !welcome && idle <= 0 && !picture && !notice && !w.LangChoice) return;
                string sid = null;
                try
                {
                    sid = WindowsIdentity.GetCurrent().User.Value;
                    using (RegistryKey ex = Registry.LocalMachine.OpenSubKey(
                               @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedPC\Exemptions\" + sid))
                        if (ex != null) return;        // geschütztes Konto (z. B. dein Admin-Konto) -> nichts tun
                }
                catch { }

                if (!string.IsNullOrEmpty(target))
                    try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
                System.Threading.Thread pic = null;
                if (picture && sid != null)
                {
                    pic = new System.Threading.Thread(() => RefreshGuestPicture(sid)) { IsBackground = true };
                    pic.Start();
                }
                if (!welcome && idle <= 0 && !notice && !w.LangChoice) { if (pic != null) pic.Join(); return; }

                L.Init(null);                          // Sprache des Gast-Kontos (Windows-Sprache)
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new GuestSession(w, welcome, notice, idle));
            }
            catch { }
        }
    }

    // Läuft in der Gast-Sitzung: zeigt das Willkommensfenster und überwacht die Benutzung.
    // Mit Sprachwahl: Ist Windows noch in der Standardsprache, kommen zuerst nur die Flaggen. Wählt der Gast eine
    // andere Sprache, meldet sich der PC kurz ab und wieder an – in der neuen Sitzung kommt dann gleich das
    // Willkommen, nur in seiner Sprache. Ohne Sprachwahl: Willkommen in allen vier Sprachen.
    class GuestSession : ApplicationContext
    {
        readonly int idleMin;
        readonly WelcomeInfo info;
        readonly bool welcome, notice;
        readonly Timer timer;
        IdleWarnForm warn;

        public GuestSession(WelcomeInfo info, bool welcome, bool notice, int idleMin)
        {
            this.info = info;
            this.welcome = welcome;
            this.notice = notice;
            this.idleMin = idleMin;
            string cur = GuestSwitch.LangOf(System.Globalization.CultureInfo.CurrentUICulture.Name);
            if (info.LangChoice && GuestSwitch.Available())
            {
                if (cur == null || cur == GuestSwitch.LangOf(GuestSwitch.StandardLanguage())) ShowPicker();
                else ShowContent(cur);                // kommt gerade vom Sprachwechsel
            }
            else ShowContent(null);
            if (idleMin > 0)
            {
                timer = new Timer { Interval = 15000 };
                timer.Tick += (s, e) => Check();
                timer.Start();
            }
        }

        void ShowPicker()
        {
            var p = new LanguageForm(false);
            p.FormClosed += (s, e) =>
            {
                if (p.Switching) return;                // PC meldet sich gleich ab
                ShowContent(p.Chosen ?? GuestSwitch.LangOf(System.Globalization.CultureInfo.CurrentUICulture.Name));
            };
            Show(p);
        }

        void ShowContent(string only)
        {
            Form f = null;
            if (welcome) f = new WelcomeForm(info, false, only);
            else if (notice) f = new NoticeForm(info.IdleMin, info.RestartTime, false, only, info.LangChoice);
            if (f == null) { if (idleMin <= 0) ExitThread(); return; }
            f.FormClosed += (s, e) => { if (idleMin <= 0) ExitThread(); };
            Show(f);
        }

        static void Show(Form f)
        {
            f.Show();
            f.Activate();
        }

        void Check()
        {
            if (warn != null && !warn.IsDisposed) return;
            if (Idle.Seconds() < idleMin * 60 || Idle.DisplayRequired()) return;   // läuft z. B. ein Video -> nicht abmelden
            warn = new IdleWarnForm("");
            warn.Show();
        }
    }

    // Leerlauf: Sekunden seit der letzten Eingabe; Abmelden
    static class Idle
    {
        [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
        [DllImport("kernel32.dll")] static extern uint GetTickCount();
        [DllImport("powrprof.dll")] static extern uint CallNtPowerInformation(int level, IntPtr inBuf, int inLen, out uint outBuf, int outLen);
        [DllImport("user32.dll")] static extern bool ExitWindowsEx(uint flags, uint reason);

        public static uint LastInput()
        {
            var i = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            return GetLastInputInfo(ref i) ? i.dwTime : GetTickCount();
        }

        public static int Seconds() { return (int)(unchecked(GetTickCount() - LastInput()) / 1000); }

        // Ein Programm hält den Bildschirm wach (Video im Browser, Film-Player)
        public static bool DisplayRequired()
        {
            try { uint st; return CallNtPowerInformation(16, IntPtr.Zero, 0, out st, 4) == 0 && (st & 0x2) != 0; }
            catch { return false; }
        }

        public static void SignOut()
        {
            if (Program.Demo) return;          // Demo/Bilder: niemals abmelden
            ExitWindowsEx(0x00000000 /*EWX_LOGOFF*/ | 0x00000010 /*EWX_FORCEIFHUNG*/, 0x80000000 /*geplant*/);
        }

        // Niemand mehr da -> PC ausschalten (er wird nicht gebraucht; beim nächsten Start ist alles frisch).
        // Darf der Gast nicht herunterfahren, wird er wenigstens abgemeldet.
        public static void ShutDown()
        {
            if (Program.Demo) return;
            try
            {
                using (Process p = Process.Start(new ProcessStartInfo("shutdown.exe", "/s /f /t 0") { UseShellExecute = false, CreateNoWindow = true }))
                    if (p.WaitForExit(8000) && p.ExitCode == 0) return;
            }
            catch { }
            SignOut();
        }
    }

    // Eine Sprache der Gast-Fenster mit allen festen Texten
    class GuestLang
    {
        public string Code, Name, Welcome, Note, StillThere, StillHere, Contact, Reset, Idle, Restart, IdleWarn;
        public string Choose, Other, Wifi, WifiPw;        // Sprachwahl + WLAN-Beschriftung (siehe GuestTexts.Extra)
    }

    // Texte für Gäste: immer Deutsch, Italienisch, Englisch und Spanisch gleichzeitig –
    // man weiß nicht, wer vor dem PC sitzt.
    static class GuestTexts
    {
        public static readonly GuestLang[] All = {
            new GuestLang { Code = "de", Name = "Deutsch", Welcome = "Willkommen!", Note = "Bitte beachten", StillThere = "Noch da?", StillHere = "Ich bin noch da", Contact = "Kontakt",
                Reset = "Dieser PC wird nach jedem Gast zurückgesetzt. Beim Abmelden, Herunterfahren oder Neustart wird alles gelöscht: Downloads, Dateien und Anmeldungen (Gmail, Netflix …). Wichtige Dateien vorher auf einen USB-Stick speichern oder ausdrucken.",
                Idle = "Wird der PC {0} Minuten nicht benutzt, schaltet er sich automatisch aus.",
                Restart = "Jeden Tag um {0} Uhr startet der PC automatisch neu.",
                IdleWarn = "Der PC wurde eine Weile nicht benutzt. In {0} Sekunden schaltet er sich aus – dabei wird alles gelöscht." },
            new GuestLang { Code = "it", Name = "Italiano", Welcome = "Benvenuto!", Note = "Attenzione", StillThere = "Ci sei ancora?", StillHere = "Sono ancora qui", Contact = "Contatto",
                Reset = "Questo PC viene ripristinato dopo ogni ospite. Alla disconnessione, allo spegnimento o al riavvio tutto viene cancellato: download, file e accessi (Gmail, Netflix …). Salva prima i file importanti su una chiavetta USB o stampali.",
                Idle = "Se il PC non viene usato per {0} minuti, si spegne automaticamente.",
                Restart = "Ogni giorno alle {0} il PC si riavvia automaticamente.",
                IdleWarn = "Il PC non è stato usato per un po'. Tra {0} secondi si spegne – e tutto verrà cancellato." },
            new GuestLang { Code = "en", Name = "English", Welcome = "Welcome!", Note = "Please note", StillThere = "Still there?", StillHere = "I'm still here", Contact = "Contact",
                Reset = "This PC is reset after every guest. When you sign out, shut down or restart, everything is deleted: downloads, files and logins (Gmail, Netflix …). Save important files to a USB stick or print them first.",
                Idle = "If the PC is not used for {0} minutes, it shuts down automatically.",
                Restart = "Every day at {0} the PC restarts automatically.",
                IdleWarn = "The PC hasn't been used for a while. In {0} seconds it shuts down – and everything will be deleted." },
            new GuestLang { Code = "es", Name = "Español", Welcome = "¡Bienvenido!", Note = "Atención", StillThere = "¿Sigues ahí?", StillHere = "Sigo aquí", Contact = "Contacto",
                Reset = "Este PC se restablece después de cada huésped. Al cerrar sesión, apagar o reiniciar se borra todo: descargas, archivos e inicios de sesión (Gmail, Netflix …). Guarda antes los archivos importantes en una memoria USB o imprímelos.",
                Idle = "Si el PC no se usa durante {0} minutos, se apaga automáticamente.",
                Restart = "Cada día a las {0} el PC se reinicia automáticamente.",
                IdleWarn = "El PC lleva un rato sin usarse. Se apagará en {0} segundos y se borrará todo." },
        };

        // Französisch: nur, wenn der Gast es über die Flaggen wählt (Sprachpaket installiert). Die Fenster mit allen
        // Sprachen nebeneinander und die Einstellungen bleiben bei den vier Sprachen oben.
        public static readonly GuestLang Fr =
            new GuestLang { Code = "fr", Name = "Français", Welcome = "Bienvenue !", Note = "Attention", StillThere = "Toujours là ?", StillHere = "Je suis encore là", Contact = "Contact",
                Reset = "Ce PC est réinitialisé après chaque client. À la déconnexion, à l'arrêt ou au redémarrage, tout est effacé : téléchargements, fichiers et connexions (Gmail, Netflix …). Enregistre d'abord les fichiers importants sur une clé USB ou imprime-les.",
                Idle = "Si le PC n'est pas utilisé pendant {0} minutes, il s'éteint automatiquement.",
                Restart = "Chaque jour à {0} le PC redémarre automatiquement.",
                IdleWarn = "Le PC n'a pas été utilisé depuis un moment. Il s'éteint dans {0} secondes – et tout sera effacé." };

        // Feste Gast-Texte für häufige zusätzliche Sprachen („+“ in den Einstellungen). Andere Sprachen: englische Texte.
        static readonly GuestLang[] Builtin = {
            new GuestLang { Code = "nl", Name = "Nederlands", Welcome = "Welkom!", Note = "Let op", StillThere = "Ben je er nog?", StillHere = "Ik ben er nog", Contact = "Contact",
                Reset = "Deze pc wordt na elke gast teruggezet. Bij afmelden, afsluiten of opnieuw opstarten wordt alles gewist: downloads, bestanden en aanmeldingen (Gmail, Netflix …). Sla belangrijke bestanden eerst op een USB-stick op of druk ze af.",
                Idle = "Als de pc {0} minuten niet wordt gebruikt, wordt hij automatisch uitgeschakeld.",
                Restart = "Elke dag om {0} wordt de pc automatisch opnieuw opgestart.",
                IdleWarn = "De pc is een tijdje niet gebruikt. Over {0} seconden wordt hij uitgeschakeld – en wordt alles gewist." },
            new GuestLang { Code = "pl", Name = "Polski", Welcome = "Witamy!", Note = "Uwaga", StillThere = "Jesteś tam jeszcze?", StillHere = "Jestem tutaj", Contact = "Kontakt",
                Reset = "Ten komputer jest resetowany po każdym gościu. Po wylogowaniu, wyłączeniu lub ponownym uruchomieniu wszystko zostaje usunięte: pobrane pliki, dokumenty i logowania (Gmail, Netflix …). Ważne pliki zapisz wcześniej na pendrivie lub wydrukuj.",
                Idle = "Jeśli komputer nie jest używany przez {0} minut, wyłącza się automatycznie.",
                Restart = "Codziennie o {0} komputer automatycznie uruchamia się ponownie.",
                IdleWarn = "Komputer od pewnego czasu nie był używany. Za {0} sekund się wyłączy – i wszystko zostanie usunięte." },
            new GuestLang { Code = "pt", Name = "Português", Welcome = "Bem-vindo!", Note = "Atenção", StillThere = "Ainda está aí?", StillHere = "Ainda estou aqui", Contact = "Contacto",
                Reset = "Este PC é reposto após cada hóspede. Ao terminar a sessão, desligar ou reiniciar, tudo é apagado: transferências, ficheiros e inícios de sessão (Gmail, Netflix …). Guarde antes os ficheiros importantes numa pen USB ou imprima-os.",
                Idle = "Se o PC não for utilizado durante {0} minutos, desliga-se automaticamente.",
                Restart = "Todos os dias às {0} o PC reinicia automaticamente.",
                IdleWarn = "O PC não é utilizado há algum tempo. Desliga-se dentro de {0} segundos – e tudo será apagado." },
            new GuestLang { Code = "cs", Name = "Čeština", Welcome = "Vítejte!", Note = "Upozornění", StillThere = "Jste tu ještě?", StillHere = "Ještě jsem tu", Contact = "Kontakt",
                Reset = "Tento počítač se po každém hostovi obnoví. Při odhlášení, vypnutí nebo restartu se vše smaže: stažené soubory, dokumenty a přihlášení (Gmail, Netflix …). Důležité soubory si předem uložte na USB flash disk nebo je vytiskněte.",
                Idle = "Pokud se počítač {0} minut nepoužívá, automaticky se vypne.",
                Restart = "Každý den v {0} se počítač automaticky restartuje.",
                IdleWarn = "Počítač se chvíli nepoužíval. Za {0} sekund se vypne – a vše se smaže." },
        };
        static readonly Dictionary<string, GuestLang> generic = new Dictionary<string, GuestLang>();

        // de/it/en/es, Französisch, die vorbereiteten zusätzlichen Sprachen – sonst englische Texte mit dem Namen der Sprache
        public static GuestLang Find(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            GuestLang l = All.FirstOrDefault(x => x.Code == code) ?? (code == "fr" ? Fr : Builtin.FirstOrDefault(x => x.Code == code));
            if (l != null) return l;
            lock (generic)
            {
                if (generic.TryGetValue(code, out l)) return l;
                GuestLang en = All.First(x => x.Code == "en");
                string name = code;
                try { name = new System.Globalization.CultureInfo(code).NativeName; name = char.ToUpper(name[0]) + name.Substring(1); } catch { }
                l = new GuestLang { Code = code, Name = name, Welcome = en.Welcome, Note = en.Note, StillThere = en.StillThere, StillHere = en.StillHere,
                    Contact = en.Contact, Reset = en.Reset, Idle = en.Idle, Restart = en.Restart, IdleWarn = en.IdleWarn,
                    Choose = en.Choose, Other = en.Other, Wifi = en.Wifi, WifiPw = en.WifiPw };
                generic[code] = l;
                return l;
            }
        }
        public static IEnumerable<GuestLang> Extras { get { return Enumerable.Empty<GuestLang>(); } }

        // Zusätzliche Sprache („+“): gültige Windows-Sprache, die nicht schon fest dabei ist (de/it/en/es/fr)
        public static bool IsExtra(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;
            string two = WelcomeInfo.SlotOf(tag);
            if (two.Length < 2 || GuestSwitch.Allowed.Contains(two)) return false;
            try { new System.Globalization.CultureInfo(tag); return true; } catch { return false; }
        }

        // Die angezeigten Sprachen: Deutsch, Italiano, English, Español
        public static List<GuestLang> Active(string lang4) { return All.ToList(); }

        // Texte für die Sprachwahl und die WLAN-Zeile, je Sprache
        static readonly Dictionary<string, string[]> More = new Dictionary<string, string[]> {
            { "de", new[] { "Sprache wählen", "Andere Sprache", "WLAN", "Kennwort" } },
            { "it", new[] { "Scegli la lingua", "Altra lingua", "Wi-Fi", "Password" } },
            { "en", new[] { "Choose your language", "Other language", "Wi-Fi", "Password" } },
            { "es", new[] { "Elige tu idioma", "Otro idioma", "Wi-Fi", "Contraseña" } },
            { "fr", new[] { "Choisis ta langue", "Autre langue", "Wi-Fi", "Mot de passe" } },
            { "nl", new[] { "Kies je taal", "Andere taal", "Wifi", "Wachtwoord" } },
            { "pl", new[] { "Wybierz język", "Inny język", "Wi-Fi", "Hasło" } },
            { "pt", new[] { "Escolha o idioma", "Outro idioma", "Wi-Fi", "Palavra-passe" } },
            { "cs", new[] { "Vyberte jazyk", "Jiný jazyk", "Wi-Fi", "Heslo" } },
        };

        static GuestTexts()
        {
            foreach (GuestLang l in All.Concat(new[] { Fr }).Concat(Builtin))
            {
                string[] m;
                if (!More.TryGetValue(l.Code, out m)) m = More["en"];
                l.Choose = m[0]; l.Other = m[1]; l.Wifi = m[2]; l.WifiPw = m[3];
            }
        }

        // Nur diese eine Sprache (Code de/it/en/es/fr) – oder alle vier, wenn code leer/unbekannt
        public static List<GuestLang> Only(string code)
        {
            GuestLang one = string.IsNullOrEmpty(code) ? null : Find(code);
            return one != null ? new List<GuestLang> { one } : All.ToList();
        }

        public static string Dot(string lang4, Func<GuestLang, string> text)
        {
            return string.Join("  ·  ", Active(lang4).Select(text).ToArray());
        }

        // Fester Hinweis „alles wird gelöscht“ (+ automatisches Ausschalten, täglicher Neustart)
        public static string Reset(GuestLang l, int idleMin, string restartTime)
        {
            string t = l.Reset;
            if (idleMin > 0) t += " " + string.Format(l.Idle, idleMin);
            if (!string.IsNullOrEmpty(restartTime)) t += " " + string.Format(l.Restart, restartTime);
            return t;
        }

        // Ein Kasten je Sprache untereinander; Rückgabe: y darunter
        public static int AddBoxes(Form f, int y, int w, IList<string> titles, IList<string> bodies, int cols = 1,
            IList<string> flags = null, float fontSize = 0f)
        {
            int gap = 10, bw = (w - 40 - (cols - 1) * gap) / cols;
            for (int i = 0; i < titles.Count; i += cols)
            {
                var row = new List<NoteBox>();
                int h = 0;
                for (int c = 0; c < cols && i + c < titles.Count; c++)
                {
                    var box = new NoteBox(titles[i + c], bodies[i + c]) { Location = new Point(20 + c * (bw + gap), y), Width = bw };
                    if (flags != null) box.FlagTag = flags[i + c];
                    f.Controls.Add(box);             // erst einfügen: übernimmt die Schrift des Fensters
                    if (fontSize > 0) box.Font = new Font(f.Font.FontFamily, fontSize);
                    h = Math.Max(h, box.NeededHeight(bw));
                    row.Add(box);
                }
                foreach (NoteBox b in row) b.Height = h;     // Kästen einer Zeile gleich hoch
                y += h + 8;
            }
            return y;
        }

        // Kleiner Bildschirm: Fenster nicht höher als der Bildschirm, dafür mit Rollbalken
        public static void FitToScreen(Form f)
        {
            int max = Screen.FromControl(f).WorkingArea.Height - 40;
            if (f.Height <= max) return;
            f.AutoScroll = true;
            f.Width += SystemInformation.VerticalScrollBarWidth;
            f.Height = max;
            f.Top = Screen.FromControl(f).WorkingArea.Top + 20;
        }
    }

    // 60-Sekunden-Warnung vor dem automatischen Ausschalten (alle Sprachen gleichzeitig)
    class IdleWarnForm : Form
    {
        readonly Label lblText;
        readonly Timer tick;
        readonly uint shownInput;
        readonly string lang4;
        int left = 60;
        const int W = 700;

        public IdleWarnForm(string lang4)
        {
            this.lang4 = lang4 ?? "";
            int n = GuestTexts.Active(this.lang4).Count, textH = n * 50;
            Ui.Style(this, W, 62 + textH + 10 + 44 + 20);
            Text = L.AppName;
            TopMost = true;
            MinimizeBox = false;
            ControlBox = false;
            shownInput = Idle.LastInput();

            var title = Ui.MakeLabel(GuestTexts.Dot(this.lang4, l => l.StillThere), 20, 16, W - 40, 38);
            title.Font = new Font("Segoe UI Semibold", n > 3 ? 14f : 16f);
            Controls.Add(title);
            lblText = Ui.MakeLabel("", 22, 62, W - 44, textH);
            Controls.Add(lblText);
            Button stay = Ui.MakeButton(GuestTexts.Dot(this.lang4, l => l.StillHere), 20, 62 + textH + 10, W - 40, 44, true);
            stay.Click += (s, e) => Close();
            Controls.Add(stay);
            AcceptButton = stay;
            UpdateText();

            tick = new Timer { Interval = 1000 };
            tick.Tick += (s, e) =>
            {
                if (Idle.LastInput() != shownInput) { Close(); return; }   // Maus/Tastatur benutzt -> weiter
                left--;
                if (left <= 0) { tick.Stop(); Idle.ShutDown(); return; }
                UpdateText();
            };
            tick.Start();
        }

        void UpdateText()
        {
            lblText.Text = string.Join("\r\n\r\n", GuestTexts.Active(lang4).Select(l => string.Format(l.IdleWarn, left)).ToArray());
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { tick.Stop(); base.OnFormClosed(e); }
    }

    // Hinweis-Fenster für Gäste (alle Sprachen, fester Text): der PC wird zurückgesetzt
    class NoticeForm : Form
    {
        const int W = 700;

        public static void Preview(IWin32Window owner, int idleMin, string restartTime, string lang4, bool langChoice)
        {
            using (var f = new NoticeForm(idleMin, restartTime, true, null, false)) f.ShowDialog(owner);
        }

        // only: nur diese Sprache (de/it/en/es) – sonst alle vier (auf breiten Bildschirmen 2 × 2)
        public NoticeForm(int idleMin, string restartTime, bool preview, string only, bool langChoice)
        {
            List<GuestLang> langs = GuestTexts.Only(only);
            int cols = langs.Count > 1 && Screen.PrimaryScreen.WorkingArea.Width >= 1100 ? 2 : 1;
            int w = cols == 2 ? 1040 : W;
            Ui.Style(this, w, 300);
            MinimizeBox = false;
            if (preview) { StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; }
            else TopMost = true;
            Text = L.AppName + (preview ? "  (" + L.T("Vorschau", "Anteprima", "Preview") + ")" : "");
            int lockupRight;
            Logo.MakeHeader(this, 78, out lockupRight);

            int y = 92;
            var title = Ui.MakeLabel(string.Join("   ", langs.Select(l => l.Note).ToArray()), 22, y, w - 44, 44);
            title.Font = new Font("Segoe UI Semibold", langs.Count > 1 ? 16f : 20f);
            Controls.Add(title);
            y += 52;
            y = GuestTexts.AddBoxes(this, y, w, langs.Select(l => l.Name).ToList(), langs.Select(l => GuestTexts.Reset(l, idleMin, restartTime)).ToList(),
                cols, langs.Select(l => l.Code).ToList()) + 10;

            Button ok = Ui.MakeButton("OK", w - 220, y, 200, 44, true);
            ok.Click += (s, e) => Close();
            Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = ok;
            if (only != null && langChoice) GuestSwitch.AddOtherLanguage(this, 20, y, langs[0], preview);
            ClientSize = new Size(w, y + 44 + 18);
            ActiveControl = ok;
        }

        protected override void OnLoad(EventArgs e) { base.OnLoad(e); GuestTexts.FitToScreen(this); }
    }

    // Willkommensfenster für Gäste (auch als Vorschau in den Einstellungen).
    // Alle vier Sprachen: Spalten nebeneinander (Entwurf A). Nach der Sprachwahl: nur die Sprache des Gastes, groß.
    class WelcomeForm : Form
    {
        readonly WelcomeInfo info;
        readonly bool preview;
        readonly string only;
        readonly int W, cols;

        public static void Preview(IWin32Window owner, WelcomeInfo info, string only = null)
        {
            using (var f = new WelcomeForm(info, true, only)) f.ShowDialog(owner);
        }

        public WelcomeForm(WelcomeInfo info, bool preview, string only = null)
        {
            this.info = info;
            this.preview = preview;
            this.only = GuestTexts.Only(only).Count == 1 ? only : null;
            int screen = Screen.PrimaryScreen.WorkingArea.Width;
            if (this.only != null) { cols = 1; W = 760; }
            else { cols = screen >= 1260 ? 4 : screen >= 1100 ? 2 : 1; W = cols == 4 ? 1200 : cols == 2 ? 1040 : 700; }
            Ui.Style(this, W, 400);
            MinimizeBox = false;
            if (preview) { StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; }
            else TopMost = true;
            Build();
        }

        void Build()
        {
            List<GuestLang> langs = GuestTexts.Only(only);
            bool one = langs.Count == 1;
            Text = L.AppName + (preview ? "  (" + L.T("Vorschau", "Anteprima", "Preview") + ")" : "");
            int lockupRight;
            Logo.MakeHeader(this, 78, out lockupRight);

            int y = 94;
            var title = Ui.MakeLabel(string.Join("   ", langs.Select(l => l.Welcome).ToArray()), 22, y, W - 44, one ? 56 : 44);
            title.Font = new Font("Segoe UI Semibold", one ? 28f : 20f);
            Controls.Add(title);
            y += one ? 66 : 54;

            // WLAN-Zeile: Name und Kennwort nebeneinander (markier- und kopierbar)
            string wl = one ? langs[0].Wifi : "WLAN · Wi-Fi", pl = one ? langs[0].WifiPw : "Kennwort · Password · Contraseña";
            if (info.Wlan.Trim().Length > 0 || info.WlanPw.Trim().Length > 0)
            {
                var strip = new Panel { Location = new Point(22, y), Size = new Size(Math.Min(W - 44, 640), 64), BackColor = Ui.Surface };
                int x = 18;
                foreach (var kv in new[] { new KeyValuePair<string, string>(wl, info.Wlan), new KeyValuePair<string, string>(pl, info.WlanPw) })
                {
                    if (kv.Value.Trim().Length == 0) continue;
                    var k = Ui.MakeLabel(kv.Key, x, 8, 300, 20);
                    k.ForeColor = Ui.Muted;
                    strip.Controls.Add(k);
                    var v = new TextBox { Text = kv.Value, ReadOnly = true, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI Semibold", 13f),
                        Location = new Point(x, 30), Width = 290, BackColor = Ui.Surface, ForeColor = Ui.Text, TabStop = false };
                    strip.Controls.Add(v);
                    x += 310;
                }
                Controls.Add(strip);
                foreach (Control c in strip.Controls)          // nach dem Einfärben: wie Text, aber markier- und kopierbar
                {
                    var tb = c as TextBox;
                    if (tb != null) { tb.BorderStyle = BorderStyle.None; tb.BackColor = Ui.Surface; }
                }
                y += 64 + 18;
            }

            // Je Sprache ein Kasten: deine Angaben in dieser Sprache + der feste Hinweis „alles wird gelöscht“
            var bodies = new List<string>();
            foreach (GuestLang l in langs)
            {
                string slot = l.Code;
                var sb = new System.Text.StringBuilder();
                string co = info.Get("Checkout", slot), ct = info.Get("Kontakt", slot), en = info.Get("ExtraName", slot), ev = info.Get("Extra", slot), tx = info.Get("Text", slot);
                if (co.Length > 0) sb.AppendLine("Check-out:  " + co);
                if (ct.Length > 0) sb.AppendLine(l.Contact + ":  " + ct);
                if (ev.Length > 0) sb.AppendLine((en.Length > 0 ? en.TrimEnd(':') + ":  " : "") + ev);
                if (tx.Length > 0) sb.AppendLine(tx);
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(GuestTexts.Reset(l, info.IdleMin, info.RestartTime));
                bodies.Add(sb.ToString());
            }
            y = GuestTexts.AddBoxes(this, y, W, langs.Select(l => l.Name).ToList(), bodies, cols, langs.Select(l => l.Code).ToList(),
                one ? 11.5f : 0f) + 12;

            Button ok = Ui.MakeButton("OK", W - 220, y, 200, 44, true);
            ok.Click += (s, e) => Close();
            Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = ok;
            if (one && info.LangChoice) GuestSwitch.AddOtherLanguage(this, 22, y, langs[0], preview);
            ClientSize = new Size(W, y + 44 + 18);
            ActiveControl = ok;
        }

        protected override void OnLoad(EventArgs e) { base.OnLoad(e); GuestTexts.FitToScreen(this); }
    }

    // Kasten mit farbiger Kante links und (wahlweise) kleiner Flagge vor dem Titel
    class NoteBox : Control
    {
        readonly string title, body;
        public string FlagTag;

        public NoteBox(string title, string body)
        {
            this.title = title;
            this.body = body;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        float K { get { return DeviceDpi / 96f; } }

        public int NeededHeight(int width)
        {
            float k = K;
            using (var bold = new Font(Font, FontStyle.Bold))
            {
                int tw = width - (int)(36 * k);
                return (int)(14 * k) + Math.Max(TextRenderer.MeasureText(title, bold).Height, (int)(16 * k)) + (int)(6 * k) +
                       TextRenderer.MeasureText(body, Font, new Size(tw, 0), TextFormatFlags.WordBreak).Height + (int)(14 * k);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            float k = K;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            using (var br = new SolidBrush(Ui.Field)) g.FillRectangle(br, ClientRectangle);
            using (var br = new LinearGradientBrush(new Rectangle(0, 0, (int)(4 * k), Math.Max(1, Height)), Logo.Cyan, Logo.Pink, 90f))
                g.FillRectangle(br, 0, 0, (int)(4 * k), Height);
            int x = (int)(20 * k), y = (int)(14 * k), tw = Width - (int)(36 * k);
            using (var bold = new Font(Font, FontStyle.Bold))
            {
                int th = Math.Max(TextRenderer.MeasureText(title, bold).Height, (int)(16 * k));
                int tx = x;
                if (!string.IsNullOrEmpty(FlagTag))
                {
                    int fh = (int)(14 * k), fw = fh * 2;
                    Flags.Draw(g, new Rectangle(x, y + (th - fh) / 2, fw, fh), FlagTag);
                    tx += fw + (int)(8 * k);
                }
                TextRenderer.DrawText(g, title, bold, new Point(tx, y), Logo.Pink);
                y += th + (int)(6 * k);
            }
            TextRenderer.DrawText(g, body, Font, new Rectangle(x, y, tw, Height - y), Ui.Text, TextFormatFlags.WordBreak);
        }
    }


    // Liste, in die man Dateien aus dem Explorer ziehen kann – auch wenn FreshGuest als Administrator läuft.
    // (Normales Drag & Drop blockiert Windows zwischen Explorer und Admin-Fenstern; WM_DROPFILES lässt sich freischalten.)
    class DropListBox : ListBox
    {
        public event Action<string[]> FilesDropped;
        public string EmptyText = "";

        [DllImport("shell32.dll")] static extern void DragAcceptFiles(IntPtr hwnd, bool accept);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(IntPtr hDrop, uint i, System.Text.StringBuilder f, uint len);
        [DllImport("shell32.dll")] static extern void DragFinish(IntPtr hDrop);
        [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr info);

        const int WM_DROPFILES = 0x233, WM_COPYDATA = 0x4A, WM_COPYGLOBALDATA = 0x49, WM_PAINT = 0xF;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                foreach (uint m in new uint[] { WM_DROPFILES, WM_COPYDATA, WM_COPYGLOBALDATA })
                    ChangeWindowMessageFilterEx(Handle, m, 1 /*MSGFLT_ALLOW*/, IntPtr.Zero);
                DragAcceptFiles(Handle, true);
            }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DROPFILES)
            {
                var files = new List<string>();
                uint n = DragQueryFile(m.WParam, 0xFFFFFFFF, null, 0);
                for (uint i = 0; i < n; i++)
                {
                    var sb = new System.Text.StringBuilder(1024);
                    if (DragQueryFile(m.WParam, i, sb, (uint)sb.Capacity) > 0) files.Add(sb.ToString());
                }
                DragFinish(m.WParam);
                if (FilesDropped != null && files.Count > 0) FilesDropped(files.ToArray());
                return;
            }
            base.WndProc(ref m);
            if (m.Msg == WM_PAINT && Items.Count == 0 && EmptyText.Length > 0)
                using (Graphics g = CreateGraphics())
                    TextRenderer.DrawText(g, EmptyText, Font, ClientRectangle, Ui.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }
    }

    // Kleines Fenster: Internetseite als Symbol (.url) für den Gast-Desktop
    class UrlForm : Form
    {
        readonly TextBox txtName, txtUrl;
        readonly Label err;
        public string FileName, Url;

        public UrlForm()
        {
            Ui.Style(this, 440, 236);
            Text = L.T("Internetseite hinzufügen", "Aggiungi pagina internet", "Add web page");
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Controls.Add(Ui.MakeLabel(L.T("Name auf dem Desktop:", "Nome sul desktop:", "Name on the desktop:"), 16, 14, 400, 22));
            txtName = new TextBox { Location = new Point(18, 38), Width = 404, Text = L.T("Wetter Südtirol", "Meteo Alto Adige", "Weather") };
            Controls.Add(txtName);
            Controls.Add(Ui.MakeLabel(L.T("Adresse:", "Indirizzo:", "Address:"), 16, 74, 400, 22));
            txtUrl = new TextBox { Location = new Point(18, 98), Width = 404, Text = "https://" };
            Controls.Add(txtUrl);
            err = Ui.MakeLabel("", 16, 132, 404, 22);
            err.ForeColor = Ui.Red;
            Controls.Add(err);
            Button ok = Ui.MakeButton("OK", 222, 172, 96, 40, true);
            Button cancel = Ui.MakeButton(L.T("Abbrechen", "Annulla", "Cancel"), 326, 172, 96, 40, false);
            cancel.DialogResult = DialogResult.Cancel;
            ok.Click += (s, e) =>
            {
                string name = string.Concat(txtName.Text.Trim().Where(c => Path.GetInvalidFileNameChars().All(x => x != c)));
                Url = BrowserSetup.NormalizeUrl(txtUrl.Text);
                if (name.Length == 0) { err.Text = L.T("Bitte einen Namen eingeben.", "Inserire un nome.", "Please enter a name."); return; }
                if (Url == null) { err.Text = L.T("Bitte eine gültige Adresse eingeben.", "Inserire un indirizzo valido.", "Please enter a valid address."); return; }
                FileName = name + ".url";
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    // ======================================================================
    // Täglicher Neustart, Sperren und Drucker (vom Admin-Fenster aus)
    // ======================================================================
    static partial class GuestExtras
    {
        const string RestartTask = "FreshGuest-Neustart";

        public static string RestartTime(Store s) { return s.Get("NeustartZeit"); }

        public static string[] TimeChoices()
        {
            var l = new List<string>();
            for (int h = 0; h < 24; h++) { l.Add(h.ToString("00") + ":00"); l.Add(h.ToString("00") + ":30"); }
            return l.ToArray();
        }

        // Die Neustart-Zeit steht auch im für Gäste lesbaren Schlüssel – der Hinweis für Gäste nennt sie
        public static void SyncRestartTime(string time)
        {
            if (Program.Demo) return;
            try
            {
                if (time != null)
                    using (RegistryKey k = Registry.LocalMachine.CreateSubKey(ConfigKey)) k.SetValue("GastNeustartZeit", time, RegistryValueKind.String);
                else
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ConfigKey, true)) if (k != null) k.DeleteValue("GastNeustartZeit", false);
            }
            catch { }
        }

        public static void ApplyDailyRestart(Store store, string time, Action<string> log)
        {
            if (!Program.Demo)
            {
                if (time != null)
                {
                    // shutdown /t 300: angemeldete Gäste bekommen 5 Minuten vorher eine Windows-Meldung
                    string tr = "shutdown.exe /r /f /t 300 /d p:0:0 /c \\\"FreshGuest: Neustart in 5 Min. - Riavvio tra 5 min. - Restart in 5 min.\\\"";
                    int rc = Sys.RunQuiet("schtasks.exe", "/Create /F /TN \"" + RestartTask + "\" /SC DAILY /ST " + time +
                                                         " /RU SYSTEM /RL HIGHEST /TR \"" + tr + "\"");
                    if (rc != 0) throw new InvalidOperationException(L.T("Geplante Aufgabe konnte nicht angelegt werden",
                        "Impossibile creare l'attività pianificata", "Could not create the scheduled task") + " (schtasks " + rc + ").");
                    Sys.TaskRunOnBattery(RestartTask);
                }
                else Sys.RunQuiet("schtasks.exe", "/Delete /F /TN \"" + RestartTask + "\"");
                SyncRestartTime(time);
            }
            if (time != null) store.Set("NeustartZeit", time); else store.Remove("NeustartZeit");
            store.Save();
            log(time != null ? string.Format(L.T("✔ Der PC startet jeden Tag um {0} Uhr neu.", "✔ Il PC si riavvia ogni giorno alle {0}.",
                                                 "✔ The PC restarts every day at {0}."), time)
                             : L.T("Täglicher Neustart aus.", "Riavvio giornaliero disattivato.", "Daily restart off."));
        }

        // --- Sperren und Standarddrucker im Windows-Standardprofil (nur neue Konten = Gäste) ---
        public const int LockSettings = 1, LockTools = 2, LockDownloads = 16, LockInstall = 32;
        // 4 und 8 waren die Sperren aus Version 1.10 (4 = streng, 8 = nur .msi) – werden beim Lesen umgerechnet

        // Software-Einschränkungsrichtlinie (SRP) im Standardprofil, also nur für neue Gäste.
        // Zwei unabhängige Sperren:
        //   LockInstall   – Programme installieren: *.msi/*.msp überall, dazu AppData und Temp (dort entpacken
        //                   Installer und dort landen Programme „nur für diesen Benutzer“, z. B. Chrome, Zoom)
        //   LockDownloads – Programme aus Downloads und vom Desktop starten
        // C:\Programme bleibt immer erlaubt.
        // .lnk/.url sind absichtlich NICHT in der Liste, damit Verknüpfungen auf dem Desktop weiter gehen.
        const string SaferKey = @"Software\Policies\Microsoft\Windows\Safer\CodeIdentifiers";
        static readonly string[] SaferTypes = { "ADE", "ADP", "BAS", "BAT", "CHM", "CMD", "COM", "CPL", "CRT", "EXE", "HLP", "HTA", "INF", "INS",
                                                "ISP", "MDB", "MDE", "MSC", "MSI", "MSP", "MST", "OCX", "PCD", "PIF", "REG", "SCR", "SHS", "VB", "WSC", "PS1", "JAR" };
        static readonly string[][] SaferDownloads = {
            new[] { "{6f0f2a3e-5b1c-4c55-9a51-f7e5b1a10001}", @"%USERPROFILE%\Downloads" },
            new[] { "{6f0f2a3e-5b1c-4c55-9a51-f7e5b1a10002}", @"%USERPROFILE%\Desktop" } };
        static readonly string[][] SaferInstall = {
            new[] { "{6f0f2a3e-5b1c-4c55-9a51-f7e5b1a10003}", @"%LOCALAPPDATA%" },
            new[] { "{6f0f2a3e-5b1c-4c55-9a51-f7e5b1a10004}", @"%APPDATA%" },
            new[] { "{6f0f2a3e-5b1c-4c55-9a51-f7e5b1a10005}", @"%TEMP%" },
            new[] { "{6f0f2a3e-5b1c-4c55-9a51-f7e5b1a10011}", "*.msi" },
            new[] { "{6f0f2a3e-5b1c-4c55-9a51-f7e5b1a10012}", "*.msp" } };

        static void SetInstallLock(RegistryKey root, int locks)
        {
            bool install = (locks & LockInstall) != 0, downloads = (locks & LockDownloads) != 0, on = install || downloads;
            bool ours;
            using (RegistryKey k = root.OpenSubKey(SaferKey)) ours = k != null && ConfigInt(k, "FreshGuest") == 1;
            if (ours) try { root.DeleteSubKeyTree(SaferKey, false); } catch { }    // immer neu aufbauen
            if (!on) return;
            using (RegistryKey k = root.CreateSubKey(SaferKey))
            {
                k.SetValue("DefaultLevel", 0x40000, RegistryValueKind.DWord);       // Standard: erlaubt
                k.SetValue("TransparentEnabled", 1, RegistryValueKind.DWord);       // DLLs nicht prüfen
                k.SetValue("PolicyScope", 0, RegistryValueKind.DWord);
                k.SetValue("AuthenticodeEnabled", 0, RegistryValueKind.DWord);
                k.SetValue("ExecutableTypes", SaferTypes, RegistryValueKind.MultiString);
                k.SetValue("FreshGuest", 1, RegistryValueKind.DWord);               // Merker: von uns angelegt
            }
            var paths = new List<string[]>();
            if (install) paths.AddRange(SaferInstall);
            if (downloads) paths.AddRange(SaferDownloads);
            foreach (string[] p in paths)
                using (RegistryKey r = root.CreateSubKey(SaferKey + @"\0\Paths\" + p[0]))
                {
                    r.SetValue("ItemData", p[1], RegistryValueKind.ExpandString);
                    r.SetValue("SaferFlags", 0, RegistryValueKind.DWord);
                    r.SetValue("Description", "FreshGuest", RegistryValueKind.String);
                }
        }
        const string ExplorerPolicy = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
        const string PrinterKey = @"Software\Microsoft\Windows NT\CurrentVersion\Windows";

        public static int Locks(Store s)
        {
            int v;
            if (!int.TryParse(s.Get("Sperren"), out v)) return 0;
            if ((v & 4) != 0) v = (v & ~4) | LockDownloads | LockInstall;     // 1.10: streng
            if ((v & 8) != 0) v = (v & ~8) | LockInstall;                     // 1.10: nur .msi
            return v;
        }
        public static string GuestPrinter(Store s) { return s.Get("GastDrucker"); }

        public static string[] Printers()
        {
            try { return PrinterSettings.InstalledPrinters.Cast<string>().ToArray(); }
            catch { return new string[0]; }
        }

        public static void ApplyLocksAndPrinter(Store store, int locks, string printer, Action<string> log)
        {
            if (!Program.Demo)
            {
                WithDefaultHive(root =>
                {
                    using (RegistryKey k = root.CreateSubKey(ExplorerPolicy))
                        if ((locks & LockSettings) != 0) k.SetValue("NoControlPanel", 1, RegistryValueKind.DWord);
                        else k.DeleteValue("NoControlPanel", false);
                    using (RegistryKey k = root.CreateSubKey(PolicyPath))
                    {
                        if ((locks & LockTools) != 0)
                        {
                            k.SetValue("DisableRegistryTools", 1, RegistryValueKind.DWord);
                            k.SetValue("DisableCMD", 2, RegistryValueKind.DWord);   // 2 = Eingabeaufforderung aus, Skripte erlaubt
                        }
                        else { k.DeleteValue("DisableRegistryTools", false); k.DeleteValue("DisableCMD", false); }
                    }
                    SetInstallLock(root, locks);
                    using (RegistryKey k = root.CreateSubKey(PrinterKey))
                    {
                        if (printer != null)
                        {
                            k.SetValue("Device", printer + ",winspool,Ne00:", RegistryValueKind.String);
                            k.SetValue("LegacyDefaultPrinterMode", 1, RegistryValueKind.DWord);   // Windows soll den Drucker nicht selbst wechseln
                        }
                        else if (GuestPrinter(store) != null)
                        {
                            k.DeleteValue("Device", false);
                            k.DeleteValue("LegacyDefaultPrinterMode", false);
                        }
                    }
                });
            }
            else log("DEMO: " + L.T("Windows-Standardprofil würde geändert.", "il profilo predefinito di Windows verrebbe modificato.",
                                    "Windows default profile would be changed."));
            if (locks != 0) store.Set("Sperren", locks.ToString()); else store.Remove("Sperren");
            if (printer != null) store.Set("GastDrucker", printer); else store.Remove("GastDrucker");
            store.Save();
            if ((locks & LockSettings) != 0) log(L.T("✔ Gäste: Systemsteuerung und Einstellungen gesperrt.", "✔ Ospiti: Pannello di controllo e Impostazioni bloccati.",
                                                    "✔ Guests: Control Panel and Settings locked."));
            if ((locks & LockTools) != 0) log(L.T("✔ Gäste: Eingabeaufforderung und Registry-Editor gesperrt.", "✔ Ospiti: Prompt dei comandi ed Editor del Registro bloccati.",
                                                 "✔ Guests: Command Prompt and Registry Editor locked."));
            if ((locks & LockInstall) != 0) log(L.T("✔ Gäste: Programme installieren gesperrt.", "✔ Ospiti: installazione di programmi bloccata.",
                                                    "✔ Guests: installing programs blocked."));
            if ((locks & LockDownloads) != 0) log(L.T("✔ Gäste: Programme aus Downloads und vom Desktop gesperrt.",
                                                      "✔ Ospiti: programmi da Download e dal desktop bloccati.",
                                                      "✔ Guests: programs from Downloads and the desktop blocked."));
            if (locks == 0) log(L.T("Keine Sperren für Gäste.", "Nessun blocco per gli ospiti.", "No locks for guests."));
            log(printer != null ? L.T("✔ Standarddrucker für Gäste: ", "✔ Stampante predefinita per gli ospiti: ", "✔ Default printer for guests: ") + printer
                                : L.T("Standarddrucker für Gäste: Windows wählt selbst.", "Stampante predefinita per gli ospiti: sceglie Windows.",
                                      "Default printer for guests: Windows decides."));
        }

        // --- Anmeldebildschirm: nur „Gast“ oder „Gast“ + deine Konten ------------------------------
        // Die eigentliche Arbeit macht Bereinigung.ps1 -NurAnmeldung (läuft auch bei jedem Start).
        // Es blendet deine Konten nur aus, wenn „Anderer Benutzer“ sicher da ist – sonst kämst du nicht mehr rein.
        public static bool GuestOnlyTile()
        {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ConfigKey)) return ConfigInt(k, "NurGastKachel") == 1;
        }

        public static void ApplyGuestOnlyTile(bool on, Action<string> log)
        {
            if (Program.Demo)
            {
                log("DEMO: " + L.T("Anmeldebildschirm würde umgestellt.", "la schermata di accesso verrebbe modificata.", "sign-in screen would be changed."));
                return;
            }
            using (RegistryKey k = Registry.LocalMachine.CreateSubKey(ConfigKey))
                if (on) k.SetValue("NurGastKachel", 1, RegistryValueKind.DWord); else k.DeleteValue("NurGastKachel", false);

            string script = Path.Combine(Sys.DataDir, "Bereinigung.ps1");
            bool current = false;
            try { current = File.Exists(script) && File.ReadAllText(script).Contains("AnmeldungZuruecksetzen"); } catch { }
            if (!current)
            {
                log(L.T("Anmeldebildschirm: gilt, sobald der Schutz eingeschaltet (bzw. neu eingerichtet) ist.",
                        "Schermata di accesso: vale appena la protezione è attivata (o riconfigurata).",
                        "Sign-in screen: applies once protection is turned on (or set up again)."));
                return;
            }
            Sys.RunQuiet("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -NurAnmeldung");

            string[] hidden = new string[0];
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ConfigKey))
                if (k != null) hidden = (k.GetValue("VersteckteKonten") as string[]) ?? new string[0];
            if (!on)
                log(L.T("✔ Anmeldebildschirm: Kacheln „Gast“ und deine Konten.", "✔ Schermata di accesso: riquadri «Ospite» e i tuoi account.",
                        "✔ Sign-in screen: \"Guest\" tile and your accounts."));
            else if (hidden.Length == 0)
                throw new InvalidOperationException(L.T("Deine Konten konnten nicht sicher ausgeblendet werden – sie bleiben sichtbar. Details in Bereinigung.log.",
                    "Non è stato possibile nascondere i tuoi account in modo sicuro – restano visibili. Dettagli in Bereinigung.log.",
                    "Your accounts could not be hidden safely – they stay visible. Details in Bereinigung.log."));
            else
                log(L.T("✔ Anmeldebildschirm: nur „Gast“. Du meldest dich über „Anderer Benutzer“ an – Benutzername: ",
                        "✔ Schermata di accesso: solo «Ospite». Tu accedi con «Altro utente» – nome utente: ",
                        "✔ Sign-in screen: \"Guest\" only. You sign in via \"Other user\" – user name: ") + Environment.UserName);
        }

        // Für Deinstallieren: alles aus diesem Teil entfernen
        public static void RemoveAllExtras(Store store, Action<string> log)
        {
            Action<string, Action> step = (what, a) => { try { a(); } catch (Exception ex) { log(what + ": " + ex.Message); } };
            if (DesktopNames(store).Count > 0) step("Desktop", () => ApplyDesktop(store, new List<DeskItem>(), log));
            if (WelcomeOn(store) || IdleMinutes(store) > 0 || NoticeOn(store)) step("Willkommen", () => ApplyGuestSession(store, false, new WelcomeInfo(), 0, false, log));
            if (RestartTime(store) != null) step("Neustart", () => ApplyDailyRestart(store, null, log));
            if (Locks(store) != 0 || GuestPrinter(store) != null) step("Sperren", () => ApplyLocksAndPrinter(store, 0, null, log));
            if (PictureOn(store)) step("Profilbild", () => RemovePicture(store, log));
            if (ChoiceOn(store)) step("Sprachwahl", () => ApplyGuestChoice(store, false, log));
            if (GuestLanguage(store) != null || store.Get("GastSpr_Tag") != null) step("Sprache", () => ApplyGuestLanguage(store, null, log));
            if (GuestOnlyTile()) step("Anmeldung", () => ApplyGuestOnlyTile(false, log));
            step("Schnellstart", () => SyncFastStart(false));
        }

        // ======================================================================
        // Profilbild NUR für Gäste. Früher wurden die Windows-Standardbilder in
        // C:\ProgramData\Microsoft\User Account Pictures überschrieben – die gelten aber für JEDES
        // Konto ohne eigenes Bild, also auch für deinen Admin, und Gäste bekamen es trotzdem nicht.
        // Jetzt: geplante Aufgabe (SYSTEM) beim Anmelden. Ist das angemeldete Konto ein Gast
        // (nicht in der Schutzliste von Shared PC), bekommt nur dieses Konto das Bild – genauso, wie
        // Windows es bei „Eigenes Bild wählen“ speichert (HKLM\...\AccountPicture\Users\<SID>).
        // Geschützte Konten werden nie angefasst. Die Gast-Kachel auf dem Anmeldebildschirm
        // bleibt das Windows-Standardbild (das Gast-Konto entsteht erst nach dem Klick).
        // ======================================================================
        public const string LogoPic = "*FreshGuest*";
        const string PicTask = "FreshGuest-Profilbild";
        static readonly int[] PicSizes = { 32, 40, 48, 64, 96, 192, 208, 240, 424, 448, 1080 };
        // Alte Version: diese Windows-Standardbilder wurden überschrieben (Originale in PicBackup)
        static readonly string[] OldPicFiles = { "user.png", "user.bmp", "guest.png", "guest.bmp", "user-32.png", "user-40.png", "user-48.png", "user-192.png" };

        static string OldPicDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\User Account Pictures"); }
        }
        static string PicBackup { get { return Path.Combine(Sys.DataDir, "Profilbild-Original"); } }
        static string PicDir { get { return Path.Combine(Sys.DataDir, "Profilbild"); } }
        static string PicScript { get { return Path.Combine(Sys.DataDir, "Profilbild.ps1"); } }

        public static bool PictureOn(Store s) { return !string.IsNullOrEmpty(s.Get("Profilbild")); }

        // Pinguin-Logo (steckt in der exe)
        static Image LogoImage()
        {
            using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("FreshGuest-448.png"))
            {
                if (st == null) throw new FileNotFoundException("FreshGuest-448.png");
                using (Image tmp = Image.FromStream(st)) return new Bitmap(tmp);
            }
        }

        // Quadratisches Profilbild 448×448: Foto in der Mitte zugeschnitten, oder das Pinguin-Logo
        public static Bitmap LoadPicture(string source)
        {
            Image img;
            if (source == LogoPic) img = LogoImage();
            else using (Image tmp = Image.FromFile(source)) img = new Bitmap(tmp);   // Kopie: Datei bleibt nicht gesperrt
            using (img)
            {
                int side = Math.Min(img.Width, img.Height);
                var src = new Rectangle((img.Width - side) / 2, (img.Height - side) / 2, side, side);
                var bmp = new Bitmap(448, 448, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Logo.HeaderRight);                       // Ecken des Logos / transparente Bereiche
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(img, new Rectangle(0, 0, 448, 448), src, GraphicsUnit.Pixel);
                }
                return bmp;
            }
        }

        static Bitmap Scaled(Image src, int size)
        {
            var b = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, size, size);
            }
            return b;
        }

        static void SaveJpeg(Image img, string path)
        {
            var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            using (var p = new System.Drawing.Imaging.EncoderParameters(1))
            {
                p.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                img.Save(path, codec, p);
            }
        }

        // Läuft beim Anmelden jedes Benutzers als SYSTEM. Reines ASCII (PowerShell 5.1 liest ohne BOM als ANSI).
        const string PicScriptContent = @"# FreshGuest: Profilbild nur fuer Gaeste (laeuft beim Anmelden als SYSTEM)
$ErrorActionPreference = 'Continue'
$daten  = Join-Path $env:ProgramData 'GaestePC'
$quelle = Join-Path $daten 'Profilbild'
$log    = Join-Path $daten 'GaestePC.log'
function L([string]$t) {
    try { Add-Content -Path $log -Value ('{0}  [Profilbild] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $t) -Encoding UTF8 -ErrorAction Stop } catch { }
}
$exKey  = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedPC\Exemptions'
$apKey  = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users'
$pub    = Join-Path $env:SystemDrive 'Users\Public\AccountPictures'
$groessen = 32, 40, 48, 64, 96, 192, 208, 240, 424, 448, 1080
L 'Start (Anmeldung erkannt)'
try {
    if (-not (Test-Path (Join-Path $quelle 'Image448.jpg'))) { L 'keine Bilddateien - Ende'; return }
    $konten  = @(Get-LocalUser)
    $praefix = $konten[0].SID.AccountDomainSid.Value
    $lokal   = @($konten | ForEach-Object { $_.SID.Value })

    # Reste frueherer Gaeste (Konto gibt es nicht mehr) entfernen - nur Eintraege von FreshGuest
    if (Test-Path $apKey) {
        foreach ($k in @(Get-ChildItem $apKey)) {
            $sid = $k.PSChildName
            if ($sid -like ""$praefix-*"" -and $lokal -notcontains $sid -and (Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue).FreshGuest -eq 1) {
                Remove-Item $k.PSPath -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item (Join-Path $pub $sid) -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    # Bis zu 60 Sekunden warten, bis das Profil des Gastes geladen ist
    for ($i = 0; $i -lt 30; $i++) {
        $gefunden = $false
        $angemeldet = @(Get-CimInstance Win32_UserProfile | Where-Object { $_.Loaded -and -not $_.Special -and $_.SID -like ""$praefix-*"" } | ForEach-Object { $_.SID })
        if ($i -eq 0) { L (""angemeldete Profile: "" + ($angemeldet -join ', ')) }
        foreach ($sid in $angemeldet) {
            if (Test-Path (Join-Path $exKey $sid)) { continue }      # geschuetztes Konto (z. B. dein Admin) -> nie anfassen
            $gefunden = $true
            $k = Join-Path $apKey $sid
            if ((Get-ItemProperty $k -ErrorAction SilentlyContinue).FreshGuest -eq 1) { continue }
            $ziel = Join-Path $pub $sid
            New-Item -ItemType Directory -Path $ziel -Force | Out-Null
            New-Item -Path $k -Force | Out-Null
            foreach ($g in $groessen) {
                $datei = Join-Path $ziel ""FreshGuest-Image$g.jpg""
                Copy-Item -Path (Join-Path $quelle ""Image$g.jpg"") -Destination $datei -Force
                New-ItemProperty -Path $k -Name ""Image$g"" -Value $datei -PropertyType String -Force | Out-Null
            }
            New-ItemProperty -Path $k -Name 'FreshGuest' -Value 1 -PropertyType DWord -Force | Out-Null
            L ""gesetzt fuer Gast $sid""
        }
        if ($gefunden) { break }
        Start-Sleep -Seconds 2
    }
    if (-not $gefunden) { L 'kein Gast angemeldet (nur geschuetzte Konten) - nichts zu tun' }
} catch { L ('FEHLER: ' + $_.Exception.Message) }
";

        // Alte Version rückgängig: Windows-Standardbilder zurücklegen (betraf auch dein Konto)
        static void RestoreWindowsDefaults(Action<string> log)
        {
            if (!Directory.Exists(PicBackup)) return;
            foreach (string f in OldPicFiles)
            {
                string b = Path.Combine(PicBackup, f);
                if (File.Exists(b)) File.Copy(b, Path.Combine(OldPicDir, f), true);
            }
            Directory.Delete(PicBackup, true);
            if (log != null) log(L.T("Windows-Standardbilder wiederhergestellt (dein Konto hat wieder sein normales Bild).",
                "Immagini predefinite di Windows ripristinate (il tuo account ha di nuovo la sua immagine normale).",
                "Windows default pictures restored (your account has its normal picture again)."));
        }

        // Bilder, die FreshGuest einzelnen Gast-Konten gegeben hat, wieder entfernen
        static void RemoveGuestPictureEntries()
        {
            const string apKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users";
            string pub = Path.Combine(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)), @"Users\Public\AccountPictures");
            using (RegistryKey users = Registry.LocalMachine.OpenSubKey(apKey, true))
            {
                if (users == null) return;
                foreach (string sid in users.GetSubKeyNames())
                {
                    bool ours;
                    using (RegistryKey k = users.OpenSubKey(sid)) ours = k != null && ConfigInt(k, "FreshGuest") == 1;
                    if (!ours) continue;
                    try { users.DeleteSubKeyTree(sid, false); } catch { }
                    try { Directory.Delete(Path.Combine(pub, sid), true); } catch { }
                }
            }
        }

        public static void ApplyPicture(Store store, string source, Action<string> log)
        {
            string stored = source == LogoPic ? LogoPic : Path.Combine(Sys.DataDir, "Profilbild.png");
            using (Bitmap pic = LoadPicture(source))
            {
                if (Program.Demo)
                {
                    log("DEMO: " + L.T("Profilbild für Gäste würde gesetzt.", "l'immagine del profilo per gli ospiti verrebbe impostata.",
                        "guest profile picture would be set."));
                }
                else
                {
                    Sys.SecureDataDir();
                    RestoreWindowsDefaults(log);
                    if (Directory.Exists(PicDir)) Directory.Delete(PicDir, true);
                    Directory.CreateDirectory(PicDir);
                    foreach (int n in PicSizes)
                        using (Bitmap b = Scaled(pic, n)) SaveJpeg(b, Path.Combine(PicDir, "Image" + n + ".jpg"));
                    if (stored != LogoPic) pic.Save(stored, System.Drawing.Imaging.ImageFormat.Png);
                    File.WriteAllText(PicScript, PicScriptContent, new System.Text.UTF8Encoding(false));
                    string tr = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \\\"" + PicScript + "\\\"";
                    int rc = Sys.RunQuiet("schtasks.exe", "/Create /F /TN \"" + PicTask + "\" /SC ONLOGON /RU SYSTEM /RL HIGHEST /TR \"" + tr + "\"");
                    if (rc == 0) Sys.TaskRunOnBattery(PicTask);
                    if (rc != 0) throw new InvalidOperationException(L.T("Geplante Aufgabe konnte nicht angelegt werden",
                        "Impossibile creare l'attività pianificata", "Could not create the scheduled task") + " (schtasks " + rc + ").");
                    RemoveGuestPictureEntries();      // bei Bildwechsel: Gäste bekommen beim nächsten Anmelden das neue
                    using (RegistryKey k = Registry.LocalMachine.CreateSubKey(ConfigKey))
                        k.SetValue("GastProfilbild", "1", RegistryValueKind.String);
                    UpdateStartupLink();              // Gast-Sitzung frischt danach das Startmenü auf
                }
            }
            store.Set("Profilbild", stored);
            store.Save();
            log(L.T("✔ Profilbild nur für Gäste gesetzt (gilt ab dem nächsten Gast, dein Konto bleibt unverändert).",
                    "✔ Immagine del profilo impostata solo per gli ospiti (vale dal prossimo ospite, il tuo account resta invariato).",
                    "✔ Profile picture set for guests only (applies from the next guest, your account stays unchanged)."));
        }

        public static void RemovePicture(Store store, Action<string> log)
        {
            if (!Program.Demo)
            {
                Sys.RunQuiet("schtasks.exe", "/Delete /F /TN \"" + PicTask + "\"");
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ConfigKey, true))
                        if (k != null) k.DeleteValue("GastProfilbild", false);
                    UpdateStartupLink();
                }
                catch { }
                RestoreWindowsDefaults(null);
                try { RemoveGuestPictureEntries(); } catch { }
                try { if (File.Exists(PicScript)) File.Delete(PicScript); } catch { }
                try { if (Directory.Exists(PicDir)) Directory.Delete(PicDir, true); } catch { }
                try { File.Delete(Path.Combine(Sys.DataDir, "Profilbild.png")); } catch { }
            }
            store.Remove("Profilbild");
            store.Save();
            if (log != null) log(L.T("Profilbild für Gäste entfernt (Windows-Standard).", "Immagine del profilo per gli ospiti rimossa (predefinita di Windows).",
                                     "Guest profile picture removed (Windows default)."));
        }

        // Läuft in der Gast-Sitzung: Die SYSTEM-Aufgabe setzt das Bild erst einige Sekunden nach der
        // Anmeldung – dann hat das Startmenü schon das Standardbild geladen. Sobald unser Eintrag da ist,
        // das Startmenü neu starten (Windows startet es sofort wieder), damit es das Bild neu liest.
        static void RefreshGuestPicture(string sid)
        {
            try
            {
                string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\" + sid;
                bool ready = false;
                for (int i = 0; i < 60 && !ready; i++)
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(key)) ready = ConfigInt(k, "FreshGuest") == 1;
                    if (!ready) System.Threading.Thread.Sleep(2000);
                }
                if (!ready) return;
                System.Threading.Thread.Sleep(1500);
                int session = Process.GetCurrentProcess().SessionId;
                foreach (Process p in Process.GetProcessesByName("StartMenuExperienceHost"))
                    using (p)
                        if (p.SessionId == session)
                            try { p.Kill(); } catch { }
            }
            catch { }
        }

        // Beim Programmstart: Profilbild aus einer alten Version (Windows-Standardbilder überschrieben)
        // automatisch auf „nur Gäste“ umstellen.
        public static void MigratePicture(Store store)
        {
            if (Program.Demo || !PictureOn(store) || !Directory.Exists(PicBackup)) return;
            try { ApplyPicture(store, store.Get("Profilbild"), s => { }); } catch { }
        }
    }

    // Rundes Profilbild als Vorschau
    class CirclePicture : Control
    {
        Image image;
        public Image Image
        {
            get { return image; }
            set { if (image != null && image != value) image.Dispose(); image = value; Invalidate(); }
        }

        public CirclePicture()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var r = new Rectangle(2, 2, Width - 5, Height - 5);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(r);
                if (image != null && Enabled)
                {
                    g.SetClip(path);
                    g.DrawImage(image, r);
                    g.ResetClip();
                }
                else using (var br = new SolidBrush(Ui.Field)) g.FillPath(br, path);
                using (var pen = new Pen(Enabled ? Logo.Cyan : Ui.Border, 2f)) g.DrawPath(pen, path);
            }
        }

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void Dispose(bool disposing) { if (disposing) Image = null; base.Dispose(disposing); }
    }
}
