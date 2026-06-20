using System;
using System.Drawing;
using System.Windows.Forms;

namespace Savedrake
{
    // UI theme (neutral charcoal + gold dark, parchment + bronze light). Palette locked via a Claude-design mockup pass.
    // Gold is a dark-mode colour (glows on dark, muddy on light), so light mode uses its dark sibling BRONZE for accents.
    internal static class Theme
    {
        internal enum Mode { Dark, Light }
        internal static Mode Current = Mode.Dark;

        internal struct Palette
        {
            public Color Window, TitleBar, Panel, PanelAlt, Input, Border, RowSep, Sel,
                         Text, TextSecondary, TextHint, Accent, AccentText, AccentDim,
                         Success, Danger, Pinned;
        }

        static Color H(string hex) { return ColorTranslator.FromHtml(hex); }

        static readonly Palette Dark = new Palette
        {
            Window = H("#1B1B1D"), TitleBar = H("#202022"), Panel = H("#26262A"), PanelAlt = H("#222225"),
            Input = H("#1E1E20"), Border = H("#34343A"), RowSep = H("#2E2E33"), Sel = H("#3A3A40"),
            Text = H("#E7E5E0"), TextSecondary = H("#9B9A94"), TextHint = H("#76756F"),
            Accent = H("#C8A24C"), AccentText = H("#211A0E"), AccentDim = H("#D3BE86"),
            Success = H("#8FB36A"), Danger = H("#C77B6F"), Pinned = H("#D8B968")
        };

        static readonly Palette Light = new Palette
        {
            Window = H("#E9E0CB"), TitleBar = H("#E3D8BD"), Panel = H("#F5EFDF"), PanelAlt = H("#EFE7D2"),
            Input = H("#FBF8EE"), Border = H("#D9CBA8"), RowSep = H("#E3D9BF"), Sel = H("#DBCBA0"),
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
                    btn.FlatAppearance.BorderColor = P.Border;
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
            if (lbl != null) { lbl.BackColor = Color.Transparent; lbl.ForeColor = P.Text; return; }

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
