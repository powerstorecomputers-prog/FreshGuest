// FreshGuest – Gast-Profilbild auf der Kachel „Guest“ des Anmeldebildschirms
// © 2026 PowerStore Computers
//
// Das Bild der Kachel (Figur mit Fragezeichen) ist fest in Windows.SharedPC.CredentialProvider.dll eingebaut.
// Einziger Weg ohne Eingriff in Windows-Dateien: Der Helfer, der ohnehin auf dem Anmelde-Desktop läuft, legt
// zwei kleine runde Fenster mit dem Gast-Profilbild genau über die beiden Stellen (groß in der Mitte, klein in
// der Kachel-Liste unten links). Die Fenster nehmen keine Klicks an und keinen Fokus.
// Am Gäste-PC getestet (10.11.2026): Das Bild erscheint erst mit der Kachel, nicht über dem Sperrbildschirm (Uhr).
//
// Aktiv, sobald ein Profilbild für Gäste eingestellt ist (HKLM\SOFTWARE\FreshGuest\GastProfilbild = 1).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace GaesteSchutz
{
    static partial class SignIn
    {
        class TileState
        {
            public bool HasBig, HasSmall;
            public Rectangle Big, Small;
            public string Diag = "";
        }

        [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        // KachelBildTest: Schalter aus der Testphase – bleibt gültig, damit das Bild nach dem Update nicht verschwindet
        static bool TilePic() { return CfgInt("GastProfilbild", 0) == 1 || CfgInt("KachelBildTest", 0) == 1; }

        static int IdleSeconds()
        {
            var i = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            return GetLastInputInfo(ref i) ? (int)(unchecked((uint)Environment.TickCount - i.dwTime) / 1000) : -1;
        }

        // Profilbild der Gäste (größte Datei aus den Einstellungen), sonst das Pinguin-Logo
        static Image LoadTilePicture()
        {
            try
            {
                string dir = Path.Combine(Sys.DataDir, "Profilbild");
                if (Directory.Exists(dir))
                {
                    string best = Directory.GetFiles(dir, "Image*.jpg").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
                    if (best != null)
                        using (var ms = new MemoryStream(File.ReadAllBytes(best)))
                        using (Image tmp = Image.FromStream(ms)) return new Bitmap(tmp);
                }
            }
            catch { }
            using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("FreshGuest-448.png"))
            using (Image tmp = Image.FromStream(st)) return new Bitmap(tmp);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void StartOverlay(Func<bool> stop)
        {
            var ui = new Thread(() =>
            {
                try { Application.Run(new TileOverlay(stop)); }
                catch (Exception ex) { Log("Kachelbild FEHLER: " + ex.Message); }
            });
            ui.SetApartmentState(ApartmentState.STA);
            ui.IsBackground = true;
            ui.Start();
        }

        // Fenster-Schleife (eigener Thread) + Beobachter (zweiter Thread: fragt den Anmeldebildschirm ab)
        class TileOverlay : ApplicationContext
        {
            readonly TileForm big, small;
            readonly Func<bool> stop;
            string lastDiag;

            public TileOverlay(Func<bool> stop)
            {
                this.stop = stop;
                Image pic = LoadTilePicture();
                big = new TileForm(pic);
                small = new TileForm(pic);
                IntPtr h1 = big.Handle, h2 = small.Handle;       // Fenster-Handles anlegen (für BeginInvoke)
                Log("Kachelbild gestartet (Bild " + pic.Width + "x" + pic.Height + ", Fenster " + h1 + "/" + h2 + ")");
                new Thread(Work) { IsBackground = true }.Start();
            }

            void Work()
            {
                while (true)
                {
                    if (stop())
                    {
                        try { big.BeginInvoke((Action)(() => ExitThread())); } catch { }
                        return;
                    }
                    TileState st;
                    try { st = LocateTiles(); }
                    catch (Exception ex) { st = new TileState { Diag = "Fehler: " + ex.Message }; }
                    if (st.Diag != lastDiag)
                    {
                        lastDiag = st.Diag;
                        Log("Kachelbild: " + st.Diag + "  (letzte Eingabe vor " + IdleSeconds() + " s)");
                    }
                    TileState cur = st;
                    try { big.BeginInvoke((Action)(() => { big.Place(cur.HasBig, cur.Big); small.Place(cur.HasSmall, cur.Small); })); } catch { }
                    Thread.Sleep(250);
                }
            }
        }

        // Rundes Bild-Fenster: ohne Rand, immer oben, nimmt weder Klicks noch Fokus an
        class TileForm : Form
        {
            readonly Image pic;

            public TileForm(Image pic)
            {
                this.pic = pic;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
                Opacity = 0.99;                 // „Layered“-Fenster: nur so lassen sich Klicks durchreichen
                BackColor = Color.Black;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= 0x08000000 /* NOACTIVATE */ | 0x80 /* TOOLWINDOW */ | 0x20 /* TRANSPARENT: Klicks gehen durch */ | 0x8 /* TOPMOST */;
                    return cp;
                }
            }

            public void Place(bool show, Rectangle r)
            {
                if (!show || r.Width < 8 || r.Height < 8) { if (Visible) Hide(); return; }
                if (Bounds != r)
                {
                    Bounds = r;
                    using (var gp = new GraphicsPath())
                    {
                        gp.AddEllipse(0, 0, r.Width, r.Height);
                        Region = new Region(gp);
                    }
                    Invalidate();
                }
                if (!Visible) Show();
                SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);     // oben halten, ohne Fokus
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                e.Graphics.DrawImage(pic, ClientRectangle);
            }
        }

        static bool ToRect(AutomationElement e, out Rectangle r)
        {
            r = Rectangle.Empty;
            try
            {
                System.Windows.Rect b = e.Current.BoundingRectangle;
                if (b.IsEmpty || double.IsInfinity(b.Width) || b.Width < 1) return false;
                r = new Rectangle((int)Math.Round(b.X), (int)Math.Round(b.Y), (int)Math.Round(b.Width), (int)Math.Round(b.Height));
                return true;
            }
            catch { return false; }
        }

        static string Probe(AutomationElement e)
        {
            Rectangle r;
            return "Klasse=" + Safe(() => e.Current.ClassName) + ",Id=" + Safe(() => e.Current.AutomationId) + ",Name=" + Safe(() => e.Current.Name) +
                   ",Ort=" + (ToRect(e, out r) ? r.X + "," + r.Y + "," + r.Width + "x" + r.Height : "-") +
                   ",verdeckt=" + Safe(() => e.Current.IsOffscreen.ToString()) + ",aktiv=" + Safe(() => e.Current.IsEnabled.ToString());
        }

        // Wo sind die beiden Bilder der Kachel „Guest“ gerade? (+ Zustandszeile fürs Protokoll)
        [MethodImpl(MethodImplOptions.NoInlining)]
        static TileState LocateTiles()
        {
            var st = new TileState();
            TreeWalker w = TreeWalker.RawViewWalker;
            int count = 0;
            AutomationElement page = FindById(w, AutomationElement.RootElement, "UserControlPageRoot", 0, ref count);
            if (page == null) { st.Diag = "kein Anmeldebildschirm (" + count + " Elemente)"; return st; }

            AutomationElement list = null, bigImg = null;
            var extra = new StringBuilder();
            int idx = 0;
            AutomationElement c = null;
            try { c = w.GetFirstChild(page); } catch { }
            while (c != null)
            {
                AutomationElement cur = c;
                string type = Safe(() => cur.Current.ControlType.ProgrammaticName), id = Safe(() => cur.Current.AutomationId);
                Rectangle r;
                if (id == "UsersList") list = cur;
                else if (type == "ControlType.Image" && id != "LockLogonImage" && bigImg == null && ToRect(cur, out r) && r.Width >= 100) bigImg = cur;
                if (type.Length == 0) extra.Append("  unlesbar#" + idx + "[" + Probe(cur) + "]");
                idx++;
                try { c = w.GetNextSibling(c); } catch { c = null; }
            }

            // Kachel „Guest“ in der Liste unten links
            AutomationElement guest = null;
            bool guestSelected = list == null;            // „Nur Guest“: keine Liste, Guest ist die einzige Kachel
            if (list != null)
            {
                var items = new List<AutomationElement>();
                for (AutomationElement i = w.GetFirstChild(list); i != null; i = w.GetNextSibling(i)) items.Add(i);
                foreach (AutomationElement it in items)
                    if (Texts(w, it, 0).Any(t => GuestName.IsMatch(t.Trim()))) { guest = it; break; }
                if (guest == null && items.Count == 2 && !HasImage(w, items[0]) && HasImage(w, items[1])) guest = items[1];
                if (guest != null)
                {
                    object p;
                    if (guest.TryGetCurrentPattern(SelectionItemPattern.Pattern, out p)) guestSelected = ((SelectionItemPattern)p).Current.IsSelected;
                    for (AutomationElement i = w.GetFirstChild(guest); i != null; i = w.GetNextSibling(i))
                    {
                        AutomationElement cur = i;
                        if (Safe(() => cur.Current.ControlType.ProgrammaticName) == "ControlType.Image" && ToRect(cur, out st.Small)) { st.HasSmall = true; break; }
                    }
                }
            }
            if (bigImg != null && guestSelected) st.HasBig = ToRect(bigImg, out st.Big);

            AutomationElement big = bigImg;
            st.Diag = "Liste=" + (list != null) + " GuestGewaehlt=" + guestSelected +
                      " gross=" + (st.HasBig ? st.Big.X + "," + st.Big.Y + "," + st.Big.Width + "x" + st.Big.Height : "-") +
                      (big != null ? "(verdeckt=" + Safe(() => big.Current.IsOffscreen.ToString()) + ")" : "") +
                      " klein=" + (st.HasSmall ? st.Small.X + "," + st.Small.Y + "," + st.Small.Width + "x" + st.Small.Height : "-") +
                      extra;
            return st;
        }
    }
}
