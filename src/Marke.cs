// FreshGuest – persönliche Fassung („Marke“), z. B. für eine Ferienwohnung
// © 2026 PowerStore Computers – GPL-3.0-or-later
//
// build.cmd <name> baut FreshGuest-<name>.exe mit den Dateien aus marke\<name>\ als Ressourcen:
//   marke.txt      Zeile=<Text oben in den Gast-Fenstern>   Profilbild=<Name des Bildes in den Einstellungen>
//   logo.png       kleines Logo rechts oben in den Gast-Fenstern (runder Ausschnitt mit transparenten Ecken)
//   profilbild.png Bild, das in den Einstellungen als Profilbild für Gäste zur Wahl steht (quadratisch)
// Der Ordner marke\ gehört nicht ins öffentliche Repo (.gitignore) – dort stehen persönliche Angaben.
// Ohne diese Ressourcen (normales FreshGuest.exe) ändert sich nichts.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace GaesteSchutz
{
    static class Brand
    {
        static Dictionary<string, string> vals;

        static Dictionary<string, string> Vals
        {
            get
            {
                if (vals != null) return vals;
                vals = new Dictionary<string, string>();
                try
                {
                    using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("Marke.txt"))
                        if (st != null)
                            using (var r = new StreamReader(st, Encoding.UTF8))
                            {
                                string line;
                                while ((line = r.ReadLine()) != null)
                                {
                                    if (line.TrimStart().StartsWith("#")) continue;
                                    int i = line.IndexOf('=');
                                    if (i > 0) vals[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                                }
                            }
                }
                catch { }
                return vals;
            }
        }

        static string Val(string key) { string v; return Vals.TryGetValue(key, out v) && v.Length > 0 ? v : null; }

        // Zeile oben in den Gast-Fenstern, z. B. „© by … · …“ – null = keine persönliche Fassung
        public static string Line { get { return Val("Zeile"); } }
        public static string PictureName { get { return Val("Profilbild") ?? L.T("Eigenes Logo", "Logo proprio", "Own logo"); } }

        // Überschrift im Willkommensfenster (Titel_de, Titel_it …) – null = Standard („Willkommen!“)
        public static string Title(string code) { return Val("Titel_" + code); }

        // Vorgaben fürs erste Einrichten: Vorgabe.W_WLAN, Vorgabe.W_Checkout_it … (Schlüssel wie im Speicher)
        public static bool HasDefaults { get { foreach (string k in Vals.Keys) if (k.StartsWith("Vorgabe.")) return true; return false; } }

        // Leere Felder mit den Vorgaben füllen (bereits Eingetragenes bleibt)
        public static void ApplyDefaults(WelcomeInfo w)
        {
            foreach (var kv in Vals)
            {
                if (!kv.Key.StartsWith("Vorgabe.W_")) continue;
                string key = kv.Key.Substring("Vorgabe.".Length);
                if (key == "W_WLAN") { if (w.Wlan.Trim().Length == 0) w.Wlan = kv.Value; continue; }
                foreach (string f in WelcomeInfo.LangFields)
                    foreach (string slot in w.AllSlots)
                        if (WelcomeInfo.Key(f, slot) == key && w.Raw(f, slot).Length == 0) w.Set(f, slot, kv.Value);
            }
        }

        static Image Res(string name)
        {
            try
            {
                using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                {
                    if (st == null) return null;
                    using (Image tmp = Image.FromStream(st)) return new Bitmap(tmp);
                }
            }
            catch { return null; }
        }

        public static bool HasPicture
        {
            get { using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("Marke-Profilbild.png")) return st != null; }
        }
        public static Image Picture() { return Res("Marke-Profilbild.png"); }

        // Rechts in der Kopfzeile der Gast-Fenster: Zeile + kleines Logo
        public static void AddToHeader(Panel header, int lockupRight)
        {
            string line = Line;
            if (line == null || header == null) return;
            int h = header.Height, right = header.Width - 16;
            Image logo = Res("Marke-Logo.png");
            if (logo != null)
            {
                int s = h - 18;
                header.Controls.Add(new PictureBox { Image = logo, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent,
                    Location = new Point(right - s, (h - s) / 2 - 1), Size = new Size(s, s) });
                right -= s + 12;
            }
            var lbl = new Label { Text = line, AutoSize = false, BackColor = Color.Transparent, ForeColor = Color.FromArgb(238, 236, 250),
                Font = new Font("Segoe UI Semibold", 11f), TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true };
            int left = lockupRight + 16;
            lbl.SetBounds(left, 0, Math.Max(40, right - left), h - 3);
            header.Controls.Add(lbl);
        }
    }
}
