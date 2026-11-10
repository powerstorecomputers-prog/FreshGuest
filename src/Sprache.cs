// FreshGuest – Windows-Sprache für Gäste
// © 2026 PowerStore Computers
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Jeder Gast ist für Windows ein neues Konto. Es bekommt die Sprache aus dem Windows-Standardprofil
// (C:\Users\Default\NTUSER.DAT) – meist die Sprache, mit der Windows installiert wurde, nicht die deines Kontos.
// Hier wird die Anzeigesprache für neue Konten im Standardprofil eingetragen (wie Windows' „Einstellungen kopieren“):
//   Control Panel\Desktop                         PreferredUILanguages = <Sprache>
//   Control Panel\International\User Profile      Languages            = <Sprache>
//   Control Panel\International\User Profile\<Sprache>   <LANGID>:<Tastatur> = 1
//   Keyboard Layout\Preload                       1                    = <Tastatur>
// Die Tastatur bleibt die deines Kontos (passt zur echten Tastatur am PC) – nur die Sprache von Windows ändert sich.
// Vorher werden die alten Werte gesichert (FreshGuest-Speicher, GastSpr_*) und beim Ausschalten wiederhergestellt.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace GaesteSchutz
{
    static partial class GuestExtras
    {
        const string LangDesktop = @"Control Panel\Desktop";
        const string LangProfile = @"Control Panel\International\User Profile";
        const string LangPreload = @"Keyboard Layout\Preload";
        const char RecSep = '\u001e', UnitSep = '\u001f', MultiSep = '\u001d';

        public static string GuestLanguage(Store s) { string v = s.Get("GastSprache"); return string.IsNullOrEmpty(v) ? null : v; }

        // Installierte Anzeigesprachen (Sprachpakete) dieses PCs, z. B. de-DE, en-US, it-IT
        public static List<string> InstalledUiLanguages()
        {
            var l = new List<string>();
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MUI\UILanguages"))
                    if (k != null)
                        foreach (string n in k.GetSubKeyNames())
                            try { new CultureInfo(n); l.Add(n); } catch { }
            }
            catch { }
            if (l.Count == 0) l.Add(CultureInfo.InstalledUICulture.Name);
            return l.OrderBy(t => LanguageName(t), StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static string LanguageName(string tag)
        {
            try
            {
                string n = new CultureInfo(tag).NativeName;
                return char.ToUpper(n[0]) + n.Substring(1) + "  –  " + tag;
            }
            catch { return tag; }
        }

        // Sprache, mit der Windows installiert wurde (Vorgabe für neue Konten, solange nichts kopiert wurde)
        public static string InstallLanguage()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Nls\Language"))
                {
                    int lcid = Convert.ToInt32(Convert.ToString(k.GetValue("InstallLanguage", "")), 16);
                    return new CultureInfo(lcid).Name;
                }
            }
            catch { return CultureInfo.InstalledUICulture.Name; }
        }

        // Tastatur deines Kontos (erste Tastatur), z. B. 00000410 = Italienisch
        public static string AdminKeyboard()
        {
            try
            {
                string id = null;
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(LangPreload)) if (k != null) id = Convert.ToString(k.GetValue("1", ""));
                if (string.IsNullOrEmpty(id)) return null;
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Substitutes"))
                    if (k != null) { string sub = Convert.ToString(k.GetValue(id, "")); if (sub.Length == 8) id = sub; }
                return id.Length == 8 ? id.ToUpperInvariant() : null;
            }
            catch { return null; }
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int SHLoadIndirectString(string source, StringBuilder outBuf, int cch, IntPtr reserved);

        public static string KeyboardName(string klid)
        {
            if (string.IsNullOrEmpty(klid)) return "-";
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts\" + klid))
                    if (k != null)
                    {
                        string disp = Convert.ToString(k.GetValue("Layout Display Name", ""));
                        var sb = new StringBuilder(260);
                        if (disp.StartsWith("@") && SHLoadIndirectString(disp, sb, sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0) return sb.ToString();
                        string t = Convert.ToString(k.GetValue("Layout Text", ""));
                        if (t.Length > 0) return t;
                    }
            }
            catch { }
            return klid;
        }

        // --- Werte sichern / wiederherstellen (Name -> Typ + Inhalt als Text) ------------------------------
        static string SaveValue(RegistryKey root, string path, string name)
        {
            using (RegistryKey k = root.OpenSubKey(path))
            {
                if (k == null || !k.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return "-";
                RegistryValueKind kind = k.GetValueKind(name);
                object v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                string data = v is string[] ? string.Join(MultiSep.ToString(), (string[])v) : Convert.ToString(v, CultureInfo.InvariantCulture);
                return ((int)kind).ToString() + UnitSep + data;
            }
        }

        static void RestoreValue(RegistryKey root, string path, string name, string saved)
        {
            if (saved == null) return;
            if (saved == "-")
            {
                using (RegistryKey k = root.OpenSubKey(path, true)) if (k != null) k.DeleteValue(name, false);
                return;
            }
            int i = saved.IndexOf(UnitSep);
            var kind = (RegistryValueKind)int.Parse(saved.Substring(0, i));
            string data = saved.Substring(i + 1);
            using (RegistryKey k = root.CreateSubKey(path))
            {
                if (kind == RegistryValueKind.MultiString) k.SetValue(name, data.Split(MultiSep), kind);
                else if (kind == RegistryValueKind.DWord) k.SetValue(name, unchecked((int)uint.Parse(data, CultureInfo.InvariantCulture)), kind);
                else k.SetValue(name, data, kind == RegistryValueKind.ExpandString ? kind : RegistryValueKind.String);
            }
        }

        // Ganzer Unterschlüssel (nur Werte): "-" = gab es nicht
        static string SaveKey(RegistryKey root, string path)
        {
            using (RegistryKey k = root.OpenSubKey(path))
            {
                if (k == null) return "-";
                var parts = k.GetValueNames().Select(n => n + RecSep + SaveValue(root, path, n));
                return "+" + string.Join(RecSep.ToString() + RecSep, parts.ToArray());
            }
        }

        static void RestoreKey(RegistryKey root, string path, string saved)
        {
            if (saved == null) return;
            root.DeleteSubKeyTree(path, false);
            if (saved == "-") return;
            root.CreateSubKey(path).Dispose();
            string body = saved.Substring(1);
            if (body.Length == 0) return;
            foreach (string part in body.Split(new[] { RecSep.ToString() + RecSep }, StringSplitOptions.None))
            {
                int i = part.IndexOf(RecSep);
                RestoreValue(root, path, part.Substring(0, i), part.Substring(i + 1));
            }
        }

        // Kern (testbar an jedem Registry-Schlüssel): gesicherte Werte zurückschreiben
        internal static void RestoreLanguage(RegistryKey root, Func<string, string> get)
        {
            string tag = get("GastSpr_Tag");
            if (tag == null) return;
            RestoreValue(root, LangDesktop, "PreferredUILanguages", get("GastSpr_PUL"));
            RestoreValue(root, LangProfile, "Languages", get("GastSpr_Langs"));
            RestoreValue(root, LangPreload, "1", get("GastSpr_Preload"));
            RestoreKey(root, LangProfile + @"\" + tag, get("GastSpr_Sub"));
        }

        // Kern: alte Werte sichern (über set) und Sprache + Tastatur eintragen
        internal static void WriteLanguage(RegistryKey root, string tag, string klid, Action<string, string> set)
        {
            string langId = (new CultureInfo(tag).LCID & 0xFFFF).ToString("X4");
            if (string.IsNullOrEmpty(klid)) klid = "0000" + langId;
            set("GastSpr_Tag", tag);
            set("GastSpr_PUL", SaveValue(root, LangDesktop, "PreferredUILanguages"));
            set("GastSpr_Langs", SaveValue(root, LangProfile, "Languages"));
            set("GastSpr_Preload", SaveValue(root, LangPreload, "1"));
            set("GastSpr_Sub", SaveKey(root, LangProfile + @"\" + tag));

            using (RegistryKey k = root.CreateSubKey(LangDesktop)) k.SetValue("PreferredUILanguages", new[] { tag }, RegistryValueKind.MultiString);
            using (RegistryKey k = root.CreateSubKey(LangProfile)) k.SetValue("Languages", new[] { tag }, RegistryValueKind.MultiString);
            root.DeleteSubKeyTree(LangProfile + @"\" + tag, false);
            using (RegistryKey k = root.CreateSubKey(LangProfile + @"\" + tag))
                k.SetValue(langId + ":" + klid, 1, RegistryValueKind.DWord);
            using (RegistryKey k = root.CreateSubKey(LangPreload)) k.SetValue("1", klid, RegistryValueKind.String);
        }

        static readonly string[] LangBackupKeys = { "GastSpr_Tag", "GastSpr_PUL", "GastSpr_Langs", "GastSpr_Preload", "GastSpr_Sub" };

        // tag = null: wieder Windows-Vorgabe (gesicherte Werte zurück)
        public static void ApplyGuestLanguage(Store store, string tag, Action<string> log)
        {
            string klid = AdminKeyboard();
            if (!Program.Demo)
            {
                WithDefaultHive(root =>
                {
                    RestoreLanguage(root, store.Get);            // erst die vorige Auswahl rückgängig machen
                    foreach (string n in LangBackupKeys) store.Remove(n);
                    if (tag != null) WriteLanguage(root, tag, klid, store.Set);
                });
            }
            else log("DEMO: " + L.T("Windows-Standardprofil würde geändert.", "il profilo predefinito di Windows verrebbe modificato.",
                                    "Windows default profile would be changed."));
            if (tag != null) store.Set("GastSprache", tag); else store.Remove("GastSprache");
            store.Save();
            log(tag != null
                ? L.T("✔ Windows-Sprache für Gäste: ", "✔ Lingua di Windows per gli ospiti: ", "✔ Windows language for guests: ") + LanguageName(tag) +
                  L.T(" – Tastatur: ", " – tastiera: ", " – keyboard: ") + KeyboardName(klid) +
                  L.T(" (ab dem nächsten Gast)", " (dal prossimo ospite)", " (from the next guest)")
                : L.T("Windows-Sprache für Gäste: Windows-Vorgabe.", "Lingua di Windows per gli ospiti: predefinita di Windows.",
                      "Windows language for guests: Windows default."));
        }
    }
}
