using System;
using System.Drawing;
using System.Windows.Forms;

namespace Savedrake
{
    // "Variant B" warm-dark card layout. Built once at the end of Main_Load: it adds a branded header + three rounded
    // cards + a status bar, REPARENTS the existing Designer controls into those cards (so all the event wiring and
    // storage keys keep working), surfaces the autobackup settings that used to live only in File > Settings, and
    // restyles the action buttons + backup list to match the locked mockup.
    //
    // All coordinates below are 96-dpi logical units; every Location/Size is scaled to device pixels via Sx/SP/SZ so the
    // layout is correct on high-DPI displays (the form is AutoScaleMode.Dpi and its design ClientSize is now 850x760).
    // The canonical state for the surfaced settings stays on the existing menu items, so Save/Load need no new keys.
    public partial class Main
    {
        private HeaderPanel _header;
        private CardPanel _cardFolders, _cardAuto, _cardBackups;
        private CheckBox _chkBackupOnSave, _chkCleanup, _chkRecycle;
        private NumericUpDown _numKeep;
        private Label _countSave, _countBackup, _hint, _lblKeepPre, _lblKeepPost;
        private Button _btnDelete;
        private Panel _autoSep;
        private ToolStripMenuItem _undoDeleteMenuItem;
        private CaptionButton _btnMin, _btnClose;
        private bool _syncingSettings;
        private bool _variantBBuilt;
        private bool _frameless = true; // borderless custom chrome (the header is the top of the window)
        private float _scale = 1f;

        // ---- Frameless custom chrome ----
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Re-add the drop shadow the OS frame normally provides (CS_DROPSHADOW). Applied at handle creation because
        // FormBorderStyle is None from the Designer, so _frameless is already true here.
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                if (_frameless) cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        // Hit-test the borderless window: a resize grip near every edge/corner, and a drag band over the header's
        // background (but not over the menu or caption buttons, which return HTCLIENT so they still get clicks). Returning
        // HTCAPTION lets Windows handle move + Aero Snap natively.
        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            if (_frameless && _header != null && m.Msg == WM_NCHITTEST && this.WindowState == FormWindowState.Normal)
            {
                long lp = m.LParam.ToInt64();
                int sx = (short)(lp & 0xFFFF), sy = (short)((lp >> 16) & 0xFFFF);
                Point pt = PointToClient(new Point(sx, sy));
                int grip = Sx(7);
                bool l = pt.X <= grip, r = pt.X >= ClientSize.Width - grip;
                bool t = pt.Y <= grip, b = pt.Y >= ClientSize.Height - grip;
                int code = 0;
                if (t && l) code = 13; else if (t && r) code = 14; else if (b && l) code = 16; else if (b && r) code = 17;
                else if (l) code = 10; else if (r) code = 11; else if (t) code = 12; else if (b) code = 15;
                if (code != 0) { m.Result = (IntPtr)code; return; }
                if (pt.Y < _header.Height)
                {
                    Point hp = _header.PointToClient(new Point(sx, sy));
                    if (_header.GetChildAtPoint(hp) == null) { m.Result = (IntPtr)2; return; } // HTCAPTION
                }
            }
            base.WndProc(ref m);
        }

        private void ApplyFramelessCorners()
        {
            // Win11 rounded corners for the borderless window (DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2).
            try { int round = 2; DwmSetWindowAttribute(this.Handle, 33, ref round, 4); } catch { }
        }

        // Darkens OS-drawn bits a WinForms theme can't reach (the ListView's scrollbar). "DarkMode_Explorer" gives a
        // dark scrollbar on Win10 1809+/Win11; "Explorer" restores the normal light one for the parchment theme.
        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        private void ApplyListScrollTheme()
        {
            try
            {
                if (listView != null && listView.IsHandleCreated)
                    SetWindowTheme(listView.Handle, Theme.Current == Theme.Mode.Dark ? "DarkMode_Explorer" : "Explorer", null);
            }
            catch { }
        }

        private int Sx(int v) { return (int)Math.Round(v * _scale); }
        private Point SP(int x, int y) { return new Point(Sx(x), Sx(y)); }
        private Size SZ(int w, int h) { return new Size(Sx(w), Sx(h)); }

        internal void BuildVariantBLayout()
        {
            if (_variantBBuilt) return;
            _variantBBuilt = true;

            _scale = this.DeviceDpi / 96f;
            SuspendLayout();
            this.Text = "Savedrake";
            this.ClientSize = SZ(850, 760);

            // ---- Header (branded strip; File/Help menu moves into it) ----
            _header = new HeaderPanel { Dock = DockStyle.Top, Height = Sx(70), AppIcon = this.Icon, Version = "1.4.0" };
            this.Controls.Add(_header);

            menuStrip1.Dock = DockStyle.None;
            menuStrip1.AutoSize = false;
            menuStrip1.Size = SZ(150, 30);
            _header.Controls.Add(menuStrip1);

            // Caption buttons (minimize / close) at the very top-right corner; File/Help sits to their left.
            int cbw = Sx(46), cbh = Sx(34);
            _btnClose = new CaptionButton { Type = CaptionButton.Kind.Close, Size = new Size(cbw, cbh), Location = new Point(_header.Width - cbw, 0), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _btnMin = new CaptionButton { Type = CaptionButton.Kind.Minimize, Size = new Size(cbw, cbh), Location = new Point(_header.Width - cbw * 2, 0), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _btnClose.Click += (s, e) => this.Close();
            _btnMin.Click += (s, e) => this.WindowState = FormWindowState.Minimized;
            _header.Controls.Add(_btnClose); _header.Controls.Add(_btnMin);

            menuStrip1.Location = new Point(_header.Width - menuStrip1.Width - cbw * 2 - Sx(8), Sx(22));
            menuStrip1.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            // ---- Cards ----
            _cardFolders = new CardPanel { Title = "Folders", Location = SP(30, 84), Size = SZ(790, 132), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            _cardAuto = new CardPanel { Title = "Autobackup", Location = SP(30, 228), Size = SZ(790, 286), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            _cardBackups = new CardPanel { Title = "Backups", Location = SP(30, 526), Size = SZ(790, 196), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            this.Controls.Add(_cardFolders);
            this.Controls.Add(_cardAuto);
            this.Controls.Add(_cardBackups);

            // ---- Card 1: Folders ----
            Reparent(label1, _cardFolders, SP(22, 56)); label1.Text = "Save game"; label1.AutoSize = true;
            Reparent(textbox1, _cardFolders, SP(120, 52)); textbox1.Size = SZ(490, 26); textbox1.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _countSave = MakeCountBox(); Reparent(_countSave, _cardFolders, SP(622, 52)); _countSave.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Reparent(Button_br_1, _cardFolders, SP(668, 51)); Button_br_1.Size = SZ(100, 28); Button_br_1.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            Reparent(label2, _cardFolders, SP(22, 96)); label2.Text = "Backups"; label2.AutoSize = true;
            Reparent(textbox2, _cardFolders, SP(120, 92)); textbox2.Size = SZ(490, 26); textbox2.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _countBackup = MakeCountBox(); Reparent(_countBackup, _cardFolders, SP(622, 92)); _countBackup.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Reparent(Button_br_2, _cardFolders, SP(668, 91)); Button_br_2.Size = SZ(100, 28); Button_br_2.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            // ---- Card 2: Autobackup ----
            Reparent(checkbox_auto, _cardAuto, SP(22, 44)); checkbox_auto.Text = "Autobackup while game runs"; checkbox_auto.Size = SZ(320, 24);
            Reparent(combobox_auto, _cardAuto, SP(628, 42)); combobox_auto.Size = SZ(140, 24); combobox_auto.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            // DropDownList (not editable) so the closed display + list are fully owner-drawn dark; the system EDIT field
            // of an editable combobox can't be themed. Presets still select normally.
            combobox_auto.DropDownStyle = ComboBoxStyle.DropDownList;
            combobox_auto.DrawMode = DrawMode.OwnerDrawFixed; combobox_auto.DrawItem += ComboAuto_DrawItem;

            _chkBackupOnSave = MakeCheck("Back up the moment the game saves", SP(58, 78), SZ(360, 24)); _cardAuto.Controls.Add(_chkBackupOnSave);
            _chkCleanup = MakeCheck("Automatically clean up old backups", SP(58, 110), SZ(360, 24)); _cardAuto.Controls.Add(_chkCleanup);
            _chkRecycle = MakeCheck("Send removed to the Recycle Bin", SP(80, 142), SZ(340, 24)); _cardAuto.Controls.Add(_chkRecycle);

            _lblKeepPre = new Label { Text = "Keep at most", AutoSize = true, Location = SP(58, 178) };
            _numKeep = new NumericUpDown { Location = SP(162, 174), Size = SZ(72, 24), Minimum = 1, Maximum = 100000, Value = 800 };
            _lblKeepPost = new Label { Text = "backups", AutoSize = true, Location = SP(244, 178) };
            _cardAuto.Controls.Add(_lblKeepPre); _cardAuto.Controls.Add(_numKeep); _cardAuto.Controls.Add(_lblKeepPost);

            _autoSep = new Panel { Location = SP(22, 212), Size = SZ(746, 1), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            _cardAuto.Controls.Add(_autoSep);

            Reparent(checkbox_hot, _cardAuto, SP(22, 226)); checkbox_hot.Text = "Global hotkey"; checkbox_hot.Size = SZ(150, 24);
            Reparent(textbox3, _cardAuto, SP(180, 228));
            Reparent(checkbox_tray, _cardAuto, SP(300, 226)); checkbox_tray.Text = "Minimize to tray"; checkbox_tray.Size = SZ(220, 24);

            _hint = new Label { Text = "Unchanged saves are skipped automatically.", AutoSize = true, Location = SP(22, 256) };
            _cardAuto.Controls.Add(_hint);

            ThemedCheck.Attach(checkbox_auto); ThemedCheck.Attach(checkbox_hot); ThemedCheck.Attach(checkbox_tray);
            ThemedCheck.Attach(_chkBackupOnSave); ThemedCheck.Attach(_chkCleanup); ThemedCheck.Attach(_chkRecycle);

            // ---- Card 3: Backups ----
            Reparent(button_backup, _cardBackups, SP(430, 12)); button_backup.Text = "Back up now"; button_backup.Size = SZ(118, 30); button_backup.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Reparent(button_res, _cardBackups, SP(556, 12)); button_res.Text = "Restore"; button_res.Size = SZ(104, 30); button_res.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnDelete = new Button { Text = "Delete", Location = SP(668, 12), Size = SZ(100, 30), Anchor = AnchorStyles.Top | AnchorStyles.Right, FlatStyle = FlatStyle.Flat };
            _btnDelete.Click += (s, e) => DeleteMenuItem_Click(s, e);
            _cardBackups.Controls.Add(_btnDelete);

            Reparent(listView, _cardBackups, SP(22, 54)); listView.Size = SZ(746, 130); listView.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            listView.HeaderStyle = ColumnHeaderStyle.None;
            ImageList rowHeight = new ImageList { ImageSize = new Size(1, Sx(34)) }; // forces a taller, mockup-like row
            listView.SmallImageList = rowHeight;

            // ---- Off-form controls now reachable from the File menu (mockup's action row is Back up now / Restore / Delete) ----
            Button_op_1.Visible = false; Button_op_2.Visible = false; button_ref.Visible = false; button_undo.Visible = false;

            ToolStripMenuItem openSave = new ToolStripMenuItem("Open save folder"); openSave.Click += Button_op_1_Click;
            ToolStripMenuItem openBackup = new ToolStripMenuItem("Open backup folder"); openBackup.Click += Button_op_2_Click;
            ToolStripMenuItem refreshItem = new ToolStripMenuItem("Refresh list"); refreshItem.Click += button_ref_Click;
            _undoDeleteMenuItem = new ToolStripMenuItem("Undo delete") { Enabled = false }; _undoDeleteMenuItem.Click += button_undo_Click;
            fileToolStripMenuItem.DropDownItems.Insert(0, openSave);
            fileToolStripMenuItem.DropDownItems.Insert(1, openBackup);
            fileToolStripMenuItem.DropDownItems.Insert(2, refreshItem);
            fileToolStripMenuItem.DropDownItems.Insert(3, _undoDeleteMenuItem);

            statusStrip1.Dock = DockStyle.Bottom;

            ResumeLayout(false);
            PerformLayout();

            // Theme everything (the recursion now reaches the new cards/checks), then apply the look details Theme.Style
            // can't infer from a control's name (outlined Restore, red Delete, card-bg labels, separator colour, etc.).
            Theme.Apply(this);
            ApplyVariantBOverrides();

            WireSurfacedSettings();
            SyncSettingControlsFromState();

            // Polish: clear the top-level menu items' stale system-white background, theme the spinner's inner controls,
            // and drop the combobox's text selection so it doesn't open with a harsh blue highlight.
            foreach (ToolStripItem it in menuStrip1.Items) it.BackColor = Color.Transparent;
            foreach (Control inner in _numKeep.Controls) { inner.BackColor = Theme.P.Input; inner.ForeColor = Theme.P.Text; }

            ApplyListScrollTheme();
            ApplyFramelessCorners();
            listViewColumnResize();
        }

        // Re-skin pass for things the generic Theme can't decide from a control name.
        private void ApplyVariantBOverrides()
        {
            Color card = Theme.P.Panel;
            if (label1 != null) { label1.BackColor = card; label1.ForeColor = Theme.P.TextSecondary; }
            if (label2 != null) { label2.BackColor = card; label2.ForeColor = Theme.P.TextSecondary; }
            if (_lblKeepPre != null) { _lblKeepPre.BackColor = card; _lblKeepPre.ForeColor = Theme.P.Text; }
            if (_lblKeepPost != null) { _lblKeepPost.BackColor = card; _lblKeepPost.ForeColor = Theme.P.Text; }
            if (textbox3 != null) { textbox3.BackColor = card; textbox3.ForeColor = Theme.P.AccentDim; }
            if (_hint != null) { _hint.BackColor = card; _hint.ForeColor = Theme.P.TextHint; }
            if (_countSave != null) { _countSave.BackColor = Theme.P.Input; _countSave.ForeColor = Theme.P.TextSecondary; }
            if (_countBackup != null) { _countBackup.BackColor = Theme.P.Input; _countBackup.ForeColor = Theme.P.TextSecondary; }
            if (_autoSep != null) _autoSep.BackColor = Theme.P.RowSep;
            if (_numKeep != null) { _numKeep.BackColor = Theme.P.Input; _numKeep.ForeColor = Theme.P.Text; _numKeep.BorderStyle = BorderStyle.FixedSingle; }
            if (combobox_auto != null) { combobox_auto.FlatStyle = FlatStyle.Flat; combobox_auto.BackColor = Theme.P.Input; combobox_auto.ForeColor = Theme.P.Text; }

            // button_backup stays gold (Theme makes it primary by name). Restore = outlined, Delete = red.
            StyleOutlineButton(button_res);
            StyleDangerButton(_btnDelete);
        }

        private void StyleOutlineButton(Button b)
        {
            if (b == null) return;
            b.FlatStyle = FlatStyle.Flat; b.UseVisualStyleBackColor = false;
            b.BackColor = Theme.P.Panel; b.ForeColor = Theme.P.AccentDim;
            b.FlatAppearance.BorderColor = Theme.P.Outline;
            b.FlatAppearance.MouseOverBackColor = Theme.P.PanelAlt;
            b.FlatAppearance.MouseDownBackColor = Theme.P.Sel;
        }

        private void StyleDangerButton(Button b)
        {
            if (b == null) return;
            b.FlatStyle = FlatStyle.Flat; b.UseVisualStyleBackColor = false;
            b.BackColor = Theme.P.Panel; b.ForeColor = Theme.P.Danger;
            b.FlatAppearance.BorderColor = Theme.P.Danger;
            b.FlatAppearance.MouseOverBackColor = Theme.P.PanelAlt;
            b.FlatAppearance.MouseDownBackColor = Theme.P.Sel;
        }

        // Owner-draw the interval combobox so its closed display + dropdown list follow the dark theme (the stock
        // combobox renders light). The edit field stays editable so custom intervals can still be typed.
        private void ComboAuto_DrawItem(object sender, DrawItemEventArgs e)
        {
            Color bg = (e.State & DrawItemState.Selected) != 0 ? Theme.P.Sel : Theme.P.Input;
            using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
            string txt = e.Index >= 0 ? combobox_auto.Items[e.Index].ToString() : combobox_auto.Text;
            Rectangle r = e.Bounds; r.X += (int)(4 * _scale);
            TextRenderer.DrawText(e.Graphics, txt, combobox_auto.Font, r, Theme.P.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private Label MakeCountBox()
        {
            return new Label
            {
                AutoSize = false,
                Size = SZ(40, 26),
                TextAlign = ContentAlignment.MiddleCenter,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9f)
            };
        }

        private CheckBox MakeCheck(string text, Point loc, Size size)
        {
            return new CheckBox { Text = text, Location = loc, Size = size, FlatStyle = FlatStyle.Flat, AutoSize = false };
        }

        // Add a control to a new parent and reset its layout state (Designer anchors were relative to the form).
        private void Reparent(Control c, Control parent, Point loc)
        {
            if (c == null) return;
            if (c.Parent != null) c.Parent.Controls.Remove(c);
            parent.Controls.Add(c);
            c.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            c.Location = loc;
            c.Visible = true;
        }

        // ---- Surfaced settings: two-way sync between the on-form controls and the canonical menu-item state ----
        private void WireSurfacedSettings()
        {
            _chkBackupOnSave.CheckedChanged += (s, e) =>
            {
                if (_syncingSettings) return;
                _syncingSettings = true;
                _backupOnSaveMenuItem.Checked = _chkBackupOnSave.Checked; // fires its watcher start/stop lambda
                _syncingSettings = false;
                try { SaveSettings(); } catch { }
            };

            _chkCleanup.CheckedChanged += (s, e) =>
            {
                if (_syncingSettings) return;
                bool want = _chkCleanup.Checked;
                if (want)
                {
                    DialogResult r = MessageBox.Show(
                        "Savedrake will keep all your recent autobackups and a spread of older ones, then remove the " +
                        "extra older autobackups so they don't pile up. Your manual backups and pinned backups are never removed.\n\n" +
                        "Turn this on?",
                        "Automatically clean up old autobackups", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (r != DialogResult.Yes) { _syncingSettings = true; _chkCleanup.Checked = false; _syncingSettings = false; return; }
                }
                _syncingSettings = true;
                _cleanupMenuItem.Checked = want;
                _recycleMenuItem.Enabled = want;
                if (!want) _recycleMenuItem.Checked = false;
                _syncingSettings = false;
                SyncSettingControlsFromState();
                try { SaveSettings(); } catch { }
            };

            _chkRecycle.CheckedChanged += (s, e) =>
            {
                if (_syncingSettings) return;
                _syncingSettings = true;
                _recycleMenuItem.Checked = _chkRecycle.Checked;
                _syncingSettings = false;
                try { SaveSettings(); } catch { }
            };

            _numKeep.ValueChanged += (s, e) =>
            {
                if (_syncingSettings) return;
                toolStripTextBox2.Text = ((int)_numKeep.Value).ToString();
                try { SaveSettings(); } catch { }
            };

            // Reflect menu-driven changes back onto the form controls.
            _cleanupMenuItem.CheckedChanged += (s, e) => { if (!_syncingSettings) SyncSettingControlsFromState(); };
            _recycleMenuItem.CheckedChanged += (s, e) => { if (!_syncingSettings) SyncSettingControlsFromState(); };
            _backupOnSaveMenuItem.CheckedChanged += (s, e) => { if (!_syncingSettings) SyncSettingControlsFromState(); };
        }

        private void SyncSettingControlsFromState()
        {
            if (_chkCleanup == null) return;
            _syncingSettings = true;
            try
            {
                _chkBackupOnSave.Checked = _backupOnSaveMenuItem.Checked;
                _chkCleanup.Checked = _cleanupMenuItem.Checked;
                _chkRecycle.Checked = _recycleMenuItem.Checked;
                _chkRecycle.Enabled = _cleanupMenuItem.Checked;
                int n;
                if (int.TryParse(toolStripTextBox2.Text, out n) && n >= 1 && n <= 100000) _numKeep.Value = n;
            }
            finally { _syncingSettings = false; }
        }
    }
}
