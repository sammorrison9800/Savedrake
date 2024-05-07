﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Savedrake_v1._2._3
{
    public class MessageWindow : NativeWindow
    {


        private const int WM_HOTKEY = 0x0312;
        public event EventHandler HotkeyPressed;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        public MessageWindow()
        {
            // Store the reference to Form1


            // Create the handle for the message-only window
            this.CreateHandle(new CreateParams());

            // Modify the window's extended style to hide it from Alt+Tab
            SetWindowStyle();
        }

        private void SetWindowStyle()
        {
            IntPtr hwnd = this.Handle;
            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TOOLWINDOW);

        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_HOTKEY)
            {
                // Raise the HotkeyPressed event
                HotkeyPressed?.Invoke(this, EventArgs.Empty);


            }
        }
    }
}
