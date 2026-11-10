// FreshGuest – der Gast wählt die Sprache von Windows selbst
// © 2026 PowerStore Computers
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Windows wechselt seine Anzeigesprache erst bei der nächsten Anmeldung – und beim Abmelden löscht Shared PC das
// Gast-Konto. Darum:
//   1. Der Gast klickt im Willkommens-/Hinweisfenster auf eine Sprache. FreshGuest (in der Gast-Sitzung, ohne
//      Admin-Rechte) schreibt den Wunsch nach HKCU\Software\FreshGuest\Sprachwunsch.
//   2. Die Wache (SYSTEM) sieht den Wunsch, trägt die Sprache ins Windows-Standardprofil ein und bestätigt mit
//      HKLM\SOFTWARE\FreshGuest\SprachwunschOk. Sie merkt sich: SprachwechselAnmelden = 1.
//   3. Der Gast wird abgemeldet. Am Anmeldebildschirm wählt der Helfer „Guest“ und klickt selbst auf „Anmelden“.
//      Das neue Gast-Konto entsteht in der gewählten Sprache.
//   4. Sobald dieser Gast angemeldet ist (20 s) – oder wenn 5 Minuten niemand kommt – stellt die Wache die
//      Standardsprache wieder ein. Der nächste Gast bekommt also wieder deine Vorgabe.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GaesteSchutz
{
    // ====================================================================== Einstellung (Admin)
    static partial class GuestExtras
    {
        public static bool ChoiceOn(Store s)
        {
            if (Program.Demo) return s.Get("Sprachwahl") == "1";
            try { using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ConfigKey)) return ConfigInt(k, "GastSprachwahl") == 1; }
            catch { return false; }
        }

        // Sprache im Standardprofil setzen, ohne die gespeicherte Vorgabe (GastSprache) zu ändern. tag = null: Windows-Vorgabe
        internal static void SetHiveLanguage(Store store, string tag, string klid)
        {
            WithDefaultHive(root =>
            {
                RestoreLanguage(root, store.Get);
                foreach (string n in LangBackupKeys) store.Remove(n);
                if (tag != null) WriteLanguage(root, tag, klid, store.Set);
            });
            store.Save();
        }

        public static void ApplyGuestChoice(Store store, bool on, Action<string> log)
        {
            if (!Program.Demo)
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(ConfigKey))
                {
                    if (on) k.SetValue("GastSprachwahl", "1", RegistryValueKind.String); else k.DeleteValue("GastSprachwahl", false);
                    if (!on) foreach (string n in new[] { "SprachwunschOk", "SprachwechselAnmelden" }) k.DeleteValue(n, false);
                }
                string klid = AdminKeyboard();
                if (klid != null) store.Set("GastSprTastatur", klid);
                UpdateStartupLink();
            }
            if (on) store.Set("Sprachwahl", "1"); else store.Remove("Sprachwahl");
            store.Save();
            if (!Program.Demo) SignIn.Sync(store, log);
            int count = InstalledUiLanguages().Count;
            log(on ? L.T("✔ Gäste können die Windows-Sprache selbst wählen (", "✔ Gli ospiti possono scegliere la lingua di Windows (",
                         "✔ Guests can choose the Windows language themselves (") + count + L.T(" Sprachen).", " lingue).", " languages).")
                   : L.T("Sprachwahl für Gäste aus.", "Scelta della lingua per gli ospiti disattivata.", "Language choice for guests off."));
            if (on && count < 2)
                log(L.T("   Hinweis: Auf diesem PC ist nur eine Sprache installiert – es gibt noch nichts zu wählen.",
                        "   Nota: su questo PC è installata una sola lingua – non c'è ancora nulla da scegliere.",
                        "   Note: only one language is installed on this PC – there is nothing to choose yet."));
        }
    }

    // ====================================================================== Wache + Helfer (SYSTEM)
    static partial class SignIn
    {
        static string sidUser, sidValue, tempUserSeen;
        static DateTime? tempSince, emptySince;

        static string CfgStr(string name)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ConfigKey))
                {
                    string v = k == null ? "" : Convert.ToString(k.GetValue(name, ""));
                    return v.Length == 0 ? null : v;
                }
            }
            catch { return null; }
        }

        static string SidOf(string user)
        {
            if (user == sidUser) return sidValue;
            try { sidValue = ((SecurityIdentifier)new NTAccount(Environment.MachineName, user).Translate(typeof(SecurityIdentifier))).Value; }
            catch { sidValue = null; }
            sidUser = user;
            return sidValue;
        }

        public static bool ChoiceNeedsWatch() { return CfgInt("GastSprachwahl", 0) == 1 || CfgStr("SprachTemp") != null; }

        // Läuft in jeder Runde der Wache
        static void LanguageTick(string user, bool nobody, bool adminOn)
        {
            bool choice = CfgInt("GastSprachwahl", 0) == 1;
            string temp = CfgStr("SprachTemp");
            if (!choice && temp == null) return;

            // 1) Wunsch eines angemeldeten Gastes übernehmen
            if (choice && !nobody && !adminOn)
            {
                string sid = SidOf(user), wish = null;
                if (sid != null)
                    try
                    {
                        using (RegistryKey k = Registry.Users.OpenSubKey(sid + @"\Software\FreshGuest", true))
                            if (k != null)
                            {
                                wish = Convert.ToString(k.GetValue("Sprachwunsch", ""));
                                if (wish.Length > 0) k.DeleteValue("Sprachwunsch", false);
                            }
                    }
                    catch { }
                if (!string.IsNullOrEmpty(wish))
                {
                    if (!GuestExtras.InstalledUiLanguages().Contains(wish)) Log("Sprachwunsch abgelehnt (nicht installiert): " + wish);
                    else
                        try
                        {
                            Store st = Store.Open();
                            GuestExtras.SetHiveLanguage(st, wish, st.Get("GastSprTastatur"));
                            CfgSet("SprachTemp", wish);
                            CfgSet("SprachTempVon", user);
                            CfgSet("SprachwechselAnmelden", 1);
                            CfgSet("SprachwunschOk", wish);
                            tempSince = null; emptySince = null;
                            Log("Sprachwunsch von '" + user + "': " + wish + " -> Standardprofil umgestellt, nach dem Abmelden automatisch anmelden.");
                            return;
                        }
                        catch (Exception ex) { Log("Sprachwunsch FEHLER: " + ex.Message); }
                }
            }

            // 2) Danach wieder die Standardsprache für den nächsten Gast
            if (temp == null) { tempSince = emptySince = null; return; }
            string by = CfgStr("SprachTempVon");
            bool restore = false;
            if (adminOn) restore = true;
            else if (!nobody && user != by)
            {
                emptySince = null;
                if (tempSince == null || tempUserSeen != user) { tempSince = DateTime.UtcNow; tempUserSeen = user; }
                else if ((DateTime.UtcNow - tempSince.Value).TotalSeconds >= 20) restore = true;     // Konto ist angelegt
            }
            else if (nobody)
            {
                tempSince = null;
                if (emptySince == null) emptySince = DateTime.UtcNow;
                else if ((DateTime.UtcNow - emptySince.Value).TotalMinutes >= 5) restore = true;     // niemand hat sich angemeldet
            }
            if (!restore) return;
            try
            {
                Store st = Store.Open();
                string std = GuestExtras.GuestLanguage(st);
                GuestExtras.SetHiveLanguage(st, std, st.Get("GastSprTastatur"));
                foreach (string n in new[] { "SprachTemp", "SprachTempVon", "SprachwunschOk" }) CfgSet(n, null);
                CfgSet("SprachwechselAnmelden", 0);
                tempSince = emptySince = null;
                Log("Sprache " + temp + " war fuer '" + (user.Length > 0 ? user : "-") + "' -> Standard wieder eingestellt: " + (std ?? "Windows-Vorgabe"));
            }
            catch (Exception ex) { Log("Standardsprache zuruecksetzen FEHLER: " + ex.Message); }
        }

        // Helfer: nach dem Sprachwechsel „Guest“ auswählen und selbst auf „Anmelden“ klicken
        static void AutoSubmit(Func<bool> stop, bool locked)
        {
            DateTime start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start).TotalSeconds < 40 && !stop())
            {
                string what;
                if (!locked) TrySelectGuest(out what);
                int count = 0;
                AutomationElement btn = FindByIdPrefix(TreeWalker.RawViewWalker, AutomationElement.RootElement, "StandaloneSubmitButtonField", 0, ref count);
                object p;
                if (btn != null && btn.TryGetCurrentPattern(InvokePattern.Pattern, out p))
                {
                    Thread.Sleep(400);                        // Auswahl fertig aufbauen lassen
                    try
                    {
                        ((InvokePattern)p).Invoke();
                        CfgSet("SprachwechselAnmelden", 0);
                        Log("Helfer: nach Sprachwechsel automatisch angemeldet (" + Safe(() => btn.Current.Name) + ").");
                        return;
                    }
                    catch (Exception ex) { Log("Helfer: Anmelden-Knopf FEHLER: " + ex.Message); }
                }
                Thread.Sleep(300);
            }
            CfgSet("SprachwechselAnmelden", 0);
            Log("Helfer: Anmelden-Knopf nach dem Sprachwechsel nicht gefunden – der Gast klickt selbst.");
        }

        static AutomationElement FindByIdPrefix(TreeWalker w, AutomationElement e, string prefix, int depth, ref int count)
        {
            if (e == null || depth > 12 || count > 500) return null;
            count++;
            if (Safe(() => e.Current.AutomationId).StartsWith(prefix, StringComparison.Ordinal)) return e;
            AutomationElement c = null;
            try { c = w.GetFirstChild(e); } catch { }
            while (c != null)
            {
                AutomationElement hit = FindByIdPrefix(w, c, prefix, depth + 1, ref count);
                if (hit != null) return hit;
                try { c = w.GetNextSibling(c); } catch { c = null; }
            }
            return null;
        }
    }

    // ====================================================================== Gast-Seite: Knöpfe + Umschalten
    static class GuestSwitch
    {
        // Rückfrage und Fehlermeldung jeweils in der Zielsprache
        static readonly Dictionary<string, string[]> Texts = new Dictionary<string, string[]> {
            { "de", new[] { "Windows wird auf Deutsch umgestellt. Dazu meldet sich der PC kurz ab und gleich wieder an (etwa eine Minute). Alles, was du bisher gemacht hast, wird dabei gelöscht.\n\nJetzt umstellen?",
                            "Die Sprache konnte gerade nicht umgestellt werden. Bitte später noch einmal versuchen." } },
            { "it", new[] { "Windows viene impostato in italiano. Il PC si disconnette per un attimo e si riconnette subito (circa un minuto). Tutto ciò che hai fatto finora viene cancellato.\n\nCambiare ora?",
                            "Al momento non è stato possibile cambiare la lingua. Riprova più tardi." } },
            { "en", new[] { "Windows will be switched to English. The PC signs out briefly and signs straight back in (about a minute). Everything you have done so far will be deleted.\n\nSwitch now?",
                            "The language could not be switched right now. Please try again later." } },
            { "fr", new[] { "Windows va passer en français. Le PC se déconnecte un instant puis se reconnecte aussitôt (environ une minute). Tout ce que vous avez fait jusqu'ici sera supprimé.\n\nChanger maintenant ?",
                            "Impossible de changer la langue pour le moment. Veuillez réessayer plus tard." } },
            { "es", new[] { "Windows se cambiará a español. El PC cerrará la sesión un momento y volverá a iniciarla enseguida (aprox. un minuto). Todo lo que hayas hecho hasta ahora se borrará.\n\n¿Cambiar ahora?",
                            "No se ha podido cambiar el idioma en este momento. Inténtalo más tarde." } },
            { "nl", new[] { "Windows wordt op Nederlands gezet. De pc meldt zich kort af en meteen weer aan (ongeveer een minuut). Alles wat je tot nu toe hebt gedaan, wordt gewist.\n\nNu omschakelen?",
                            "De taal kon nu niet worden gewijzigd. Probeer het later opnieuw." } },
            { "pl", new[] { "Windows zostanie przełączony na język polski. Komputer na chwilę się wyloguje i od razu zaloguje ponownie (około minuty). Wszystko, co do tej pory zrobiłeś, zostanie usunięte.\n\nPrzełączyć teraz?",
                            "Nie udało się teraz zmienić języka. Spróbuj ponownie później." } },
            { "cs", new[] { "Windows se přepne do češtiny. Počítač se na chvíli odhlásí a hned znovu přihlásí (asi minutu). Vše, co jste dosud udělali, bude smazáno.\n\nPřepnout nyní?",
                            "Jazyk teď nebylo možné přepnout. Zkuste to prosím později." } },
        };

        // Kurzer Name auf dem Knopf: „Deutsch“, „Italiano“ … (bei zwei Varianten derselben Sprache mit Land)
        static string ButtonName(string tag, List<string> all)
        {
            try
            {
                var c = new CultureInfo(tag);
                bool twin = all.Count(t => { try { return new CultureInfo(t).TwoLetterISOLanguageName == c.TwoLetterISOLanguageName; } catch { return false; } }) > 1;
                string n = twin ? c.NativeName : c.Parent.NativeName;
                if (string.IsNullOrEmpty(n)) n = c.NativeName;
                return char.ToUpper(n[0]) + n.Substring(1);
            }
            catch { return tag; }
        }

        public const int SideWidth = 236;

        // Gibt es etwas zu wählen? (mindestens zwei Sprachpakete)
        public static bool Available() { return GuestExtras.InstalledUiLanguages().Count >= 2; }

        public static void AddSide(Form f, int x, int w, bool preview)
        {
            Panel col = MakeColumn(f, x, 78, w, preview);
            int h = f.ClientSize.Height - 78;
            if (col.Height > h) { f.ClientSize = new Size(f.ClientSize.Width, col.Height + 78); h = col.Height; }
            col.Height = h;
            f.Controls.Add(col);
        }

        // Spalte rechts: „Windows“ + je Sprache ein Knopf mit Flagge und Namen (untereinander)
        public static Panel MakeColumn(Form f, int x, int y, int w, bool preview)
        {
            List<string> langs = GuestExtras.InstalledUiLanguages();
            string current = CultureInfo.CurrentUICulture.Name;
            var p = new Panel { Location = new Point(x, y), Width = w, BackColor = Ui.Surface };
            var t = Ui.MakeLabel("Windows", 18, 16, w - 36, 30);
            t.Font = new Font("Segoe UI Semibold", 14f);
            p.Controls.Add(t);
            var s = Ui.MakeLabel("Sprache · Lingua · Language\r\nIdioma · Langue", 18, 48, w - 36, 40);
            s.ForeColor = Ui.Muted;
            p.Controls.Add(s);
            int by = 98;
            foreach (string tag in langs)
            {
                var b = new FlagButton(tag, ButtonName(tag, langs), string.Equals(tag, current, StringComparison.OrdinalIgnoreCase))
                    { Location = new Point(16, by), Size = new Size(w - 32, 50) };
                string tg = tag;
                if (!b.Current) b.Click += (o, e) => Request(f, tg, preview);
                p.Controls.Add(b);
                by += 58;
            }
            p.Height = by + 8;
            return p;
        }

        static void Request(Form owner, string tag, bool preview)
        {
            string two = tag.Split('-')[0].ToLowerInvariant();
            string[] tx;
            if (!Texts.TryGetValue(two, out tx)) tx = Texts["en"];
            if (preview || Program.Demo)
            {
                MessageBox.Show(owner, L.T("Vorschau – so fragt FreshGuest den Gast:", "Anteprima – così FreshGuest chiede all'ospite:", "Preview – this is what FreshGuest asks the guest:") +
                    "\n\n" + tx[0], L.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(owner, tx[0], L.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) != DialogResult.Yes) return;

            owner.UseWaitCursor = true;
            Application.DoEvents();
            bool ok = false;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\FreshGuest")) k.SetValue("Sprachwunsch", tag, RegistryValueKind.String);
                for (int i = 0; i < 100 && !ok; i++)          // bis zu 25 s auf die Wache warten
                {
                    Thread.Sleep(250);
                    Application.DoEvents();
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\FreshGuest"))
                        ok = k != null && string.Equals(Convert.ToString(k.GetValue("SprachwunschOk", "")), tag, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            owner.UseWaitCursor = false;
            if (ok) { Idle.SignOut(); return; }
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\FreshGuest", true)) if (k != null) k.DeleteValue("Sprachwunsch", false); } catch { }
            MessageBox.Show(owner, tx[1], L.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // Knopf mit gezeichneter Flagge und dem Namen der Sprache
    class FlagButton : Control
    {
        public readonly string LangTag;
        public readonly bool Current;
        bool hot;

        public FlagButton(string tag, string name, bool current)
        {
            LangTag = tag;
            Text = name;
            Current = current;
            Font = new Font("Segoe UI Semibold", 11f);
            Cursor = current ? Cursors.Default : Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

        static GraphicsPath Round(Rectangle r, int rad)
        {
            var gp = new GraphicsPath();
            int d = rad * 2;
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            float k = DeviceDpi / 96f;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath gp = Round(r, (int)(8 * k)))
            {
                using (var br = new SolidBrush(Current ? Ui.Blue : hot ? Ui.FieldHover : Ui.Field)) g.FillPath(br, gp);
                using (var pen = new Pen(Current ? Logo.Cyan : Ui.Border, Current ? 2f : 1f)) g.DrawPath(pen, gp);
            }
            int fh = (int)(26 * k), fw = (int)(39 * k);
            var fr = new Rectangle((int)(14 * k), (Height - fh) / 2, fw, fh);
            g.SmoothingMode = SmoothingMode.None;
            Flags.Draw(g, fr, LangTag);
            int tx = fr.Right + (int)(14 * k);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(tx, 0, Width - tx - (int)(28 * k), Height), Color.White,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            if (Current)
                TextRenderer.DrawText(g, "✔", Font, new Rectangle(Width - (int)(30 * k), 0, (int)(22 * k), Height), Logo.Cyan,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        }
    }

    // Einfache Flaggen, selbst gezeichnet (Windows zeigt Flaggen-Emojis nur als Buchstaben)
    static class Flags
    {
        static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        static void Stripes(Graphics g, Rectangle r, bool vertical, params Color[] cols)
        {
            int n = cols.Length;
            for (int i = 0; i < n; i++)
            {
                Rectangle s = vertical
                    ? Rectangle.FromLTRB(r.X + r.Width * i / n, r.Y, r.X + r.Width * (i + 1) / n, r.Bottom)
                    : Rectangle.FromLTRB(r.X, r.Y + r.Height * i / n, r.Right, r.Y + r.Height * (i + 1) / n);
                using (var b = new SolidBrush(cols[i])) g.FillRectangle(b, s);
            }
        }

        public static void Draw(Graphics g, Rectangle r, string tag)
        {
            string lang = (tag ?? "").Split('-')[0].ToLowerInvariant();
            string region = tag != null && tag.Contains("-") ? tag.Substring(tag.LastIndexOf('-') + 1).ToUpperInvariant() : "";
            switch (lang)
            {
                case "de":
                    if (region == "AT") Stripes(g, r, false, C(200, 16, 46), Color.White, C(200, 16, 46));
                    else Stripes(g, r, false, Color.Black, C(221, 0, 0), C(255, 206, 0));
                    break;
                case "it": Stripes(g, r, true, C(0, 146, 70), Color.White, C(206, 43, 55)); break;
                case "fr": Stripes(g, r, true, C(0, 85, 164), Color.White, C(239, 65, 53)); break;
                case "nl": Stripes(g, r, false, C(174, 28, 40), Color.White, C(33, 70, 139)); break;
                case "pl": Stripes(g, r, false, Color.White, C(220, 20, 60)); break;
                case "es":
                    Stripes(g, r, false, C(170, 21, 27), C(241, 191, 0), C(241, 191, 0), C(170, 21, 27));
                    break;
                case "cs":
                    Stripes(g, r, false, Color.White, C(215, 20, 26));
                    using (var b = new SolidBrush(C(17, 69, 126)))
                        g.FillPolygon(b, new[] { new Point(r.X, r.Y), new Point(r.X + r.Width / 2, r.Y + r.Height / 2), new Point(r.X, r.Bottom) });
                    break;
                case "pt":
                    using (var b = new SolidBrush(C(0, 102, 0))) g.FillRectangle(b, r.X, r.Y, r.Width * 2 / 5, r.Height);
                    using (var b = new SolidBrush(C(255, 0, 0))) g.FillRectangle(b, r.X + r.Width * 2 / 5, r.Y, r.Width - r.Width * 2 / 5, r.Height);
                    break;
                case "en":
                    if (region == "US") UnitedStates(g, r); else UnionJack(g, r);
                    break;
                default:
                    using (var b = new SolidBrush(Ui.Grey)) g.FillRectangle(b, r);
                    TextRenderer.DrawText(g, lang.ToUpperInvariant(), new Font("Segoe UI Semibold", 8f), r, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    break;
            }
            using (var pen = new Pen(Color.FromArgb(90, 0, 0, 0))) g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        static void UnionJack(Graphics g, Rectangle r)
        {
            GraphicsState st = g.Save();
            g.SetClip(r);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(C(1, 33, 105))) g.FillRectangle(b, r);
            float h = r.Height;
            using (var w = new Pen(Color.White, h / 5f)) { g.DrawLine(w, r.Left, r.Top, r.Right, r.Bottom); g.DrawLine(w, r.Left, r.Bottom, r.Right, r.Top); }
            using (var red = new Pen(C(200, 16, 46), h / 12f)) { g.DrawLine(red, r.Left, r.Top, r.Right, r.Bottom); g.DrawLine(red, r.Left, r.Bottom, r.Right, r.Top); }
            float cx = r.Left + r.Width / 2f, cy = r.Top + h / 2f;
            using (var w = new SolidBrush(Color.White))
            {
                g.FillRectangle(w, cx - h / 6f, r.Top, h / 3f, h);
                g.FillRectangle(w, r.Left, cy - h / 6f, r.Width, h / 3f);
            }
            using (var red = new SolidBrush(C(200, 16, 46)))
            {
                g.FillRectangle(red, cx - h / 10f, r.Top, h / 5f, h);
                g.FillRectangle(red, r.Left, cy - h / 10f, r.Width, h / 5f);
            }
            g.Restore(st);
        }

        static void UnitedStates(Graphics g, Rectangle r)
        {
            for (int i = 0; i < 13; i++)
                using (var b = new SolidBrush(i % 2 == 0 ? C(178, 34, 52) : Color.White))
                    g.FillRectangle(b, Rectangle.FromLTRB(r.X, r.Y + r.Height * i / 13, r.Right, r.Y + r.Height * (i + 1) / 13));
            using (var b = new SolidBrush(C(60, 59, 110))) g.FillRectangle(b, r.X, r.Y, r.Width * 2 / 5, r.Height * 7 / 13);
        }
    }

    // Kleines Fenster nur für die Sprachwahl (wenn weder Willkommens- noch Hinweisfenster eingeschaltet ist)
    class LanguageForm : Form
    {
        const int W = 360;

        public LanguageForm(bool preview)
        {
            Ui.Style(this, W, 300);
            MinimizeBox = false;
            if (preview) { StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; }
            else TopMost = true;
            Text = L.AppName;
            int lockupRight;
            Logo.MakeHeader(this, 78, out lockupRight);
            Panel col = GuestSwitch.MakeColumn(this, 0, 78, W, preview);
            Controls.Add(col);
            int y = col.Bottom + 6;
            Button ok = Ui.MakeButton("OK", W - 220, y, 200, 44, true);
            ok.Click += (s, e) => Close();
            Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = ok;
            ClientSize = new Size(W, y + 44 + 18);
            ActiveControl = ok;
        }
    }
}
