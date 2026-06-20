using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Savedrake
{
    // Reusable WinForms building blocks for the warm-dark "variant B" look: rounded cards, a branded header with the
    // gold serif wordmark, a themed checkbox, and small paint helpers. All colours come from the static Theme palette
    // (Theme.P), so these controls re-skin automatically when the light/dark theme toggles and Theme.Apply re-runs.
    internal static class UiPaint
    {
        // A rounded-rectangle path (used for cards and the checkbox glyph). Inset by 0.5px-friendly integer bounds.
        internal static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            if (radius <= 0) { p.AddRectangle(r); p.CloseFigure(); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // Cached serif font resolver for the wordmark. Tries Cinzel (if ever bundled/installed), then common system
        // serifs, then the generic serif family — never throws. Fonts are cached and never disposed (process-lifetime).
        static readonly Dictionary<string, Font> _serifCache = new Dictionary<string, Font>();
        static FontFamily _serifFamily;

        static FontFamily SerifFamily()
        {
            if (_serifFamily != null) return _serifFamily;
            foreach (var name in new[] { "Cinzel", "Trajan Pro", "Georgia", "Times New Roman" })
            {
                try { var f = new FontFamily(name); _serifFamily = f; return f; } catch { }
            }
            _serifFamily = FontFamily.GenericSerif;
            return _serifFamily;
        }

        internal static Font Serif(float size, FontStyle style)
        {
            string key = size.ToString("0.0") + "/" + (int)style;
            Font f;
            if (_serifCache.TryGetValue(key, out f)) return f;
            try { f = new Font(SerifFamily(), size, style, GraphicsUnit.Pixel); }
            catch { f = new Font(FontFamily.GenericSerif, size, style, GraphicsUnit.Pixel); }
            _serifCache[key] = f;
            return f;
        }

        // Draw a string with manual per-glyph letter-spacing (GDI+ has no tracking API).
        internal static void DrawTracked(Graphics g, string text, Font font, Color color, float x, float y, float spacing)
        {
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(color))
            {
                var fmt = StringFormat.GenericTypographic;
                foreach (char c in text)
                {
                    string s = c.ToString();
                    g.DrawString(s, font, b, x, y, fmt);
                    x += g.MeasureString(s, font, PointF.Empty, fmt).Width + spacing;
                }
            }
        }
    }

    // A rounded card: warm card fill, a 1px border, and an optional gold serif section title at the top-left. Children
    // are positioned by the layout code in the card's client area (below the title band).
    internal sealed class CardPanel : Panel
    {
        public Color FillColor = Color.Empty;     // Empty => Theme.P.Panel at paint time
        public Color BorderColor = Color.Empty;   // Empty => Theme.P.Border
        public Color ShellColor = Color.Empty;    // corner colour behind the rounded edge; Empty => Theme.P.Window
        public string Title = null;
        public int CornerRadius = 12;
        public int TitleInset = 18;

        public CardPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            float s = DeviceDpi / 96f;
            Color fill = FillColor.IsEmpty ? Theme.P.Panel : FillColor;
            Color border = BorderColor.IsEmpty ? Theme.P.Border : BorderColor;
            Color shell = ShellColor.IsEmpty ? Theme.P.Window : ShellColor;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var sb = new SolidBrush(shell)) g.FillRectangle(sb, ClientRectangle);

            Rectangle r = ClientRectangle; r.Width -= 1; r.Height -= 1;
            using (var path = UiPaint.RoundRect(r, (int)(CornerRadius * s)))
            using (var fb = new SolidBrush(fill))
            using (var bp = new Pen(border))
            {
                g.FillPath(fb, path);
                g.DrawPath(bp, path);
            }

            if (!string.IsNullOrEmpty(Title))
            {
                using (var tf = new Font("Segoe UI Semibold", 11f, FontStyle.Bold))
                {
                    TextRenderer.DrawText(g, Title, tf, new Point((int)(TitleInset * s), (int)(12 * s)), Theme.P.Accent,
                        TextFormatFlags.NoPadding);
                }
            }
        }
    }

    // The branded header strip: shell bg, a circular logo token, the gold serif "Savedrake" wordmark, the version, and
    // a 1px gold rule along the bottom. The File/Help MenuStrip is parented here by the layout code (right side).
    internal sealed class HeaderPanel : Panel
    {
        public Icon AppIcon;
        public string Version = "";

        public HeaderPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        // Make the header's own surface transparent to hit-testing so clicks on its empty areas fall through to the
        // parent Form (whose WM_NCHITTEST returns HTCAPTION/resize codes) — that's what makes the borderless window
        // draggable and top-resizable by the header. The header's child controls (menu, caption buttons) are separate
        // window handles that are hit first, so they still receive their own clicks.
        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            const int HTTRANSPARENT = -1;
            if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTTRANSPARENT; return; }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            float s = DeviceDpi / 96f;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Slightly distinct warm bar so the reparented File/Help menu (rendered in TitleBar) blends seamlessly.
            using (var bg = new SolidBrush(Theme.P.TitleBar)) g.FillRectangle(bg, ClientRectangle);

            // Logo token: a warm disc + faint gold ring + the app icon glyph.
            int d = (int)(44 * s), lx = (int)(30 * s), ly = (Height - d) / 2;
            var disc = new Rectangle(lx, ly, d, d);
            using (var db = new SolidBrush(Theme.P.Panel)) g.FillEllipse(db, disc);
            using (var rp = new Pen(Color.FromArgb(120, Theme.P.Accent), s)) g.DrawEllipse(rp, disc);
            if (AppIcon != null)
            {
                try
                {
                    using (var bmp = AppIcon.ToBitmap())
                    {
                        int gs = (int)(28 * s), gp = lx + (d - gs) / 2, gq = ly + (d - gs) / 2;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(bmp, new Rectangle(gp, gq, gs, gs));
                    }
                }
                catch { }
            }

            // Wordmark + version, vertically centred against the token.
            Font wf = UiPaint.Serif(26f * s, FontStyle.Regular);
            float track = 1.5f * s;
            float wx = lx + d + 16 * s;
            float wy = (Height - wf.Height) / 2f - 1;
            UiPaint.DrawTracked(g, "Savedrake", wf, Theme.P.Pinned, wx, wy, track);

            if (!string.IsNullOrEmpty(Version))
            {
                float ww = 0; var fmt = StringFormat.GenericTypographic;
                foreach (char c in "Savedrake") ww += g.MeasureString(c.ToString(), wf, PointF.Empty, fmt).Width + track;
                using (var vf = new Font("Segoe UI", 10.5f))
                    TextRenderer.DrawText(g, Version, vf, new Point((int)(wx + ww + 10 * s), (int)(wy + wf.Height - 22 * s)),
                        Theme.P.TextSecondary, TextFormatFlags.NoPadding);
            }

            // Bottom gold rule.
            using (var rule = new Pen(Color.FromArgb(150, Theme.P.Accent), s))
                g.DrawLine(rule, lx, Height - 1, Width - lx, Height - 1);
        }
    }

    // A minimal themed caption button (minimize / close) for the frameless window. Painted on the header's warm bar;
    // close turns red on hover. The window's WM_NCHITTEST returns HTCLIENT over these so they receive clicks.
    internal sealed class CaptionButton : Control
    {
        internal enum Kind { Minimize, Close }
        public Kind Type = Kind.Minimize;
        bool _hover;

        public CaptionButton()
        {
            // Fully paints its own background in OnPaint, so it does not need (and a raw Control does not support)
            // a transparent BackColor.
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            float s = DeviceDpi / 96f;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color bg = _hover ? (Type == Kind.Close ? Theme.P.Danger : Theme.P.Sel) : Theme.P.TitleBar;
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, ClientRectangle);

            Color fg = (_hover && Type == Kind.Close) ? Color.White : Theme.P.Text;
            int cx = Width / 2, cy = Height / 2, r = (int)(5 * s);
            using (var pen = new Pen(fg, Math.Max(1f, 1.3f * s)))
            {
                if (Type == Kind.Minimize) g.DrawLine(pen, cx - r, cy, cx + r, cy);
                else { g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r); g.DrawLine(pen, cx + r, cy - r, cx - r, cy + r); }
            }
        }
    }

    // Attaches owner-drawn rendering to a stock CheckBox so the check reads as a gold tick in a gold-tinted rounded box
    // on dark (bronze on parchment in light mode). The CheckBox keeps its native click/toggle behaviour (AutoCheck);
    // we only repaint. Re-reads Theme.P each paint, so it follows the theme toggle.
    internal static class ThemedCheck
    {
        internal static void Attach(CheckBox cb)
        {
            cb.FlatStyle = FlatStyle.Flat;
            cb.FlatAppearance.BorderSize = 0;
            cb.AutoSize = false;
            cb.BackColor = Color.Transparent;
            cb.Paint += Paint;
            cb.CheckedChanged += (s, e) => cb.Invalidate();
            cb.EnabledChanged += (s, e) => cb.Invalidate();
            cb.MouseEnter += (s, e) => cb.Invalidate();
            cb.MouseLeave += (s, e) => cb.Invalidate();
        }

        static void Paint(object sender, PaintEventArgs e)
        {
            var cb = (CheckBox)sender;
            float s = cb.DeviceDpi / 96f;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Erase the native glyph + text by repainting the card background underneath.
            using (var bg = new SolidBrush(Theme.P.Panel)) g.FillRectangle(bg, cb.ClientRectangle);

            int box = (int)(18 * s);
            int by = (cb.Height - box) / 2;
            var rect = new Rectangle(0, by, box, box);
            bool on = cb.Checked;
            bool dim = !cb.Enabled;

            using (var path = UiPaint.RoundRect(rect, (int)(4 * s)))
            {
                if (on)
                {
                    Color fill = dim ? Theme.P.AccentDim : Theme.P.Accent;
                    using (var fb = new SolidBrush(fill)) g.FillPath(fb, path);
                    // tick
                    using (var pen = new Pen(Theme.P.AccentText, 2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLines(pen, new[]
                        {
                            new PointF(rect.X + 4 * s, rect.Y + 9 * s),
                            new PointF(rect.X + 8 * s, rect.Y + 13 * s),
                            new PointF(rect.X + 14 * s, rect.Y + 5 * s)
                        });
                    }
                }
                else
                {
                    using (var fb = new SolidBrush(Theme.P.Input)) g.FillPath(fb, path);
                    using (var bp = new Pen(dim ? Theme.P.RowSep : Theme.P.Border, s)) g.DrawPath(bp, path);
                }
            }

            Color textColor = dim ? Theme.P.TextHint : Theme.P.Text;
            var textRect = new Rectangle(box + (int)(8 * s), 0, cb.Width - box - (int)(8 * s), cb.Height);
            TextRenderer.DrawText(g, cb.Text, cb.Font, textRect, textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }
}
