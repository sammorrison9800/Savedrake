using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Savedrake
{
    // UI theme (warm dark + gold dark, parchment + bronze light). Palette locked via a Claude-design mockup pass
    // ("B - my refinement: warm dark, readable gold, restrained colour"). The dark shell is a warm espresso brown,
    // NOT a neutral/cool charcoal -- the structural greys carry a brown undertone (red >= green > blue) so the gold
    // reads as part of the same family. Gold is a dark-mode colour (glows on dark, muddy on light), so light mode
    // uses its dark sibling BRONZE for accents.
    internal static class Theme
    {
        internal enum Mode { Dark, Light }
        internal static Mode Current = Mode.Dark;

        internal struct Palette
        {
            public Color Window, TitleBar, Panel, PanelAlt, Input, Border, Outline, RowSep, Sel,
                         Text, TextSecondary, TextHint, Accent, AccentText, AccentDim,
                         Success, Danger, Pinned;
        }

        static Color H(string hex) { return ColorTranslator.FromHtml(hex); }

        static readonly Palette Dark = new Palette
        {
            // Warm espresso shell (brown undertone, R >= G > B) so the bar/panels/inputs sit in the same family as gold.
            Window = H("#1F1811"), TitleBar = H("#241C14"), Panel = H("#2A2118"), PanelAlt = H("#251D15"),
            Input = H("#1A140C"), Border = H("#3A3025"), Outline = H("#6B5A33"), RowSep = H("#2F261A"), Sel = H("#3E3223"),
            Text = H("#E9E4D8"), TextSecondary = H("#A39B88"), TextHint = H("#837B69"),
            Accent = H("#C8A24C"), AccentText = H("#211A0E"), AccentDim = H("#D3BE86"),
            Success = H("#8FB36A"), Danger = H("#C77B6F"), Pinned = H("#D8B968")
        };

        static readonly Palette Light = new Palette
        {
            Window = H("#E9E0CB"), TitleBar = H("#E3D8BD"), Panel = H("#F5EFDF"), PanelAlt = H("#EFE7D2"),
            Input = H("#FBF8EE"), Border = H("#D9CBA8"), Outline = H("#B89B5B"), RowSep = H("#E3D9BF"), Sel = H("#DBCBA0"),
            Text = H("#2E2A1F"), TextSecondary = H("#5C543E"), TextHint = H("#837A5E"),
            Accent = H("#7E5E22"), AccentText = H("#F5EFDF"), AccentDim = H("#6E5320"),
            Success = H("#3E6B33"), Danger = H("#9A3B2E"), Pinned = H("#7E5E22")
        };

        internal static Palette P { get { return Current == Mode.Dark ? Dark : Light; } }

        // Apply the current palette across a form. ListView is owner-drawn separately by Main (it needs per-row status
        // colours); here we just set its base colours + OwnerDraw flag.
        internal static void Apply(Form form)
        {
            form.BackColor = P.Window;
            form.ForeColor = P.Text;
            ApplyControls(form.Controls);
            ApplyTitleBar(form);
        }

        // ---- Native title-bar theming via DWM (Win11 = caption+text+border colour; Win10 1809+ = dark mode only) ----
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("ntdll.dll")] static extern void RtlGetNtVersionNumbers(out uint major, out uint minor, out uint build);

        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;     // Win10 2004+/Win11
        const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Win10 1809-1909
        const int DWMWA_BORDER_COLOR = 34;                // Win11 22000+
        const int DWMWA_CAPTION_COLOR = 35;               // Win11 22000+
        const int DWMWA_TEXT_COLOR = 36;                  // Win11 22000+

        // COLORREF is 0x00BBGGRR — NOT Color.ToArgb() (which is 0x00RRGGBB and would swap red/blue).
        static int ColorRef(Color c) { return c.R | (c.G << 8) | (c.B << 16); }

        static uint OsBuild()
        {
            try { uint mj, mn, b; RtlGetNtVersionNumbers(out mj, out mn, out b); return b & 0xFFFF; }
            catch { return 0; }
        }

        // Make the OS title bar follow the theme. Best-effort: on a build/OS without DWM support it just no-ops to the
        // native bar (never throws). The caption glyphs (min/max/close) follow immersive dark mode: white on the dark
        // bar, black on the parchment bar — both readable.
        internal static void ApplyTitleBar(Form form)
        {
            if (form == null || !form.IsHandleCreated) return;
            IntPtr h = form.Handle;
            uint build = OsBuild();
            try
            {
                int dark = Current == Mode.Dark ? 1 : 0;
                if (build >= 19041)
                {
                    if (DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4) != 0)
                        DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, 4);
                }
                else if (build >= 17763)
                {
                    DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, 4);
                }

                if (build >= 22000) // caption/text/border colours: Windows 11 only
                {
                    int caption = ColorRef(P.TitleBar);
                    int text = ColorRef(P.Text);
                    int border = ColorRef(P.Border);
                    DwmSetWindowAttribute(h, DWMWA_CAPTION_COLOR, ref caption, 4);
                    DwmSetWindowAttribute(h, DWMWA_TEXT_COLOR, ref text, 4);
                    DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref border, 4);
                }
            }
            catch { /* DWM unavailable -> keep the native title bar, never crash */ }
        }

        static void ApplyControls(Control.ControlCollection controls)
        {
            foreach (Control c in controls)
            {
                Style(c);
                // Don't recurse into a ComboBox: its dropdown list is OS-managed, not a WinForms child control.
                if (c.HasChildren && !(c is ComboBox)) ApplyControls(c.Controls);
            }
        }

        static void Style(Control c)
        {
            var menu = c as MenuStrip;
            if (menu != null) { menu.BackColor = P.TitleBar; menu.ForeColor = P.Text; menu.Renderer = new DarkMenuRenderer(); return; }

            var status = c as StatusStrip;
            if (status != null) { status.BackColor = P.TitleBar; status.ForeColor = P.TextSecondary; status.Renderer = new DarkMenuRenderer(); return; }

            var btn = c as Button;
            if (btn != null)
            {
                // Restore / Backup are the primary actions -> gold; everything else -> panel. Set ALL FlatAppearance
                // colours so none go stale across a theme toggle, and reflect Enabled so a disabled button reads dimmer.
                bool primary = btn.Name == "button_res" || btn.Name == "button_backup";
                btn.FlatStyle = FlatStyle.Flat;
                btn.UseVisualStyleBackColor = false;
                if (primary)
                {
                    btn.BackColor = P.Accent; btn.ForeColor = P.AccentText;
                    btn.FlatAppearance.BorderColor = P.Accent;
                    btn.FlatAppearance.MouseOverBackColor = P.Pinned;
                    btn.FlatAppearance.MouseDownBackColor = P.AccentDim;
                }
                else
                {
                    btn.BackColor = btn.Enabled ? P.Panel : P.PanelAlt;
                    btn.ForeColor = P.AccentDim;
                    btn.FlatAppearance.BorderColor = P.Outline; // gold-dim outline = the mockup's secondary buttons
                    btn.FlatAppearance.MouseOverBackColor = P.PanelAlt;
                    btn.FlatAppearance.MouseDownBackColor = P.Sel;
                }
                return;
            }

            var tb = c as TextBox;
            if (tb != null) { tb.BorderStyle = BorderStyle.FixedSingle; tb.BackColor = P.Input; tb.ForeColor = P.Text; return; }

            var cmb = c as ComboBox;
            if (cmb != null) { cmb.FlatStyle = FlatStyle.Flat; cmb.BackColor = P.Input; cmb.ForeColor = P.Text; return; }

            var chk = c as CheckBox;
            if (chk != null) { chk.BackColor = Color.Transparent; chk.ForeColor = P.Text; return; }

            var lbl = c as Label;
            // Header-style labels (those ending in ":", e.g. "Savegame Location:") get the gold/bronze accent so the
            // theme's personality shows even without the card layout; other labels stay in the normal text colour.
            if (lbl != null)
            {
                lbl.BackColor = Color.Transparent;
                lbl.ForeColor = (!string.IsNullOrEmpty(lbl.Text) && lbl.Text.TrimEnd().EndsWith(":")) ? P.Accent : P.Text;
                return;
            }

            var lv = c as ListView;
            if (lv != null)
            {
                lv.BackColor = P.Panel; lv.ForeColor = P.Text;
                lv.BorderStyle = BorderStyle.FixedSingle;
                lv.OwnerDraw = true;
                return;
            }

            var pnl = c as Panel;
            if (pnl != null) { pnl.BackColor = P.Window; return; }

            // Default: containers and anything else inherit the window colours.
            c.BackColor = P.Window;
            c.ForeColor = P.Text;
        }

        // A flat dark/parchment renderer for the menu bar + status bar (the default professional renderer is light-only).
        internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
        {
            internal DarkMenuRenderer() : base(new DarkColors()) { }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Selected ? P.AccentText : P.Text;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = P.TextSecondary;
                base.OnRenderArrow(e);
            }
        }

        sealed class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return P.Panel; } }
            public override Color ImageMarginGradientBegin { get { return P.Panel; } }
            public override Color ImageMarginGradientMiddle { get { return P.Panel; } }
            public override Color ImageMarginGradientEnd { get { return P.Panel; } }
            public override Color MenuBorder { get { return P.Border; } }
            public override Color MenuItemBorder { get { return P.Accent; } }
            public override Color MenuItemSelected { get { return P.Accent; } }
            public override Color MenuItemSelectedGradientBegin { get { return P.Accent; } }
            public override Color MenuItemSelectedGradientEnd { get { return P.Accent; } }
            public override Color MenuItemPressedGradientBegin { get { return P.PanelAlt; } }
            public override Color MenuItemPressedGradientEnd { get { return P.PanelAlt; } }
            public override Color MenuStripGradientBegin { get { return P.TitleBar; } }
            public override Color MenuStripGradientEnd { get { return P.TitleBar; } }
            public override Color StatusStripGradientBegin { get { return P.TitleBar; } }
            public override Color StatusStripGradientEnd { get { return P.TitleBar; } }
            public override Color SeparatorDark { get { return P.Border; } }
            public override Color SeparatorLight { get { return P.Border; } }
            public override Color ToolStripBorder { get { return P.TitleBar; } }
        }
    }
}
