using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DNotes.Core
{
    /// <summary>
    /// The Win32 surface DNotes needs. Kept deliberately small: window styles,
    /// focus, hotkeys, monitors and file dialogs. Everything here is used for
    /// behaviour that WinForms does not expose - never for decoration.
    /// </summary>
    internal static class Native
    {
        // ---- window styles --------------------------------------------------
        public const int GWL_EXSTYLE = -20;
        public const int GWL_STYLE = -16;

        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TRANSPARENT = 0x00000020;   // clicks pass through
        public const int WS_EX_TOOLWINDOW = 0x00000080;    // no alt-tab entry
        public const int WS_EX_APPWINDOW = 0x00040000;    // own taskbar button
        public const int WS_EX_NOACTIVATE = 0x08000000;   // never steal focus
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_ACCEPTFILES = 0x00000010;
        public const int WS_POPUP = unchecked((int)0x80000000);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y,
                                               int cx, int cy, uint flags);

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_FRAMECHANGED = 0x0020;

        // ---- focus ----------------------------------------------------------
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int cmd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint a, uint b, bool attach);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        /// <summary>
        /// Brings a window to the front and gives it keyboard focus. The
        /// AttachThreadInput dance is needed because Windows refuses to let a
        /// background process steal focus otherwise - which would leave the note
        /// window visible but deaf to the keyboard. This is exactly the class of
        /// bug that makes a note look open but impossible to type into.
        /// </summary>
        public static void ForceForeground(Form f)
        {
            if (f == null || !f.IsHandleCreated) return;
            IntPtr h = f.Handle;
            if (GetForegroundWindow() == h) { SetFocus(GetFocus()); return; }

            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            uint myThread = GetCurrentThreadId();
            bool attached = false;
            if (fgThread != myThread && fgThread != 0)
                attached = AttachThreadInput(myThread, fgThread, true);
            try
            {
                ShowWindow(h, 5 /* SW_SHOW */);
                BringWindowToTop(h);
                SetForegroundWindow(h);
            }
            finally
            {
                if (attached) AttachThreadInput(myThread, fgThread, false);
            }
        }

        // ---- hit testing ----------------------------------------------------
        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT p);

        [DllImport("user32.dll")]
        public static extern IntPtr ChildWindowFromPointEx(IntPtr parent, POINT p, uint flags);

        [DllImport("user32.dll")]
        public static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        public const uint GA_ROOT = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        /// <summary>
        /// Returns the deepest child window under a screen point, or IntPtr.Zero
        /// if the topmost window there is not owned by <paramref name="root"/>.
        /// The self-test uses this to prove nothing is parked on top of the
        /// note editor swallowing clicks.
        /// </summary>
        public static IntPtr DeepestChildAt(IntPtr root, Point screenPoint)
        {
            IntPtr top = WindowFromPoint(new POINT(screenPoint.X, screenPoint.Y));
            if (top == IntPtr.Zero) return IntPtr.Zero;
            if (GetAncestor(top, GA_ROOT) != root) return IntPtr.Zero;
            IntPtr cur = top;
            while (true)
            {
                POINT p = new POINT(screenPoint.X, screenPoint.Y);
                ScreenToClient(cur, ref p);
                IntPtr next = ChildWindowFromPointEx(cur, p, 1 /* CWP_SKIPINVISIBLE */);
                if (next == IntPtr.Zero || next == cur) break;
                cur = next;
            }
            return cur;
        }

        // ---- monitors -------------------------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [DllImport("user32.dll")]
        public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip,
                                                      MonitorEnumProc callback, IntPtr data);

        public delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT rect, IntPtr data);

        // ---- hotkeys --------------------------------------------------------
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_NOREPEAT = 0x4000;
        public const uint MOD_WIN = 0x0008;

        public const int WM_HOTKEY = 0x0312;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // ---- text measurement / caret ---------------------------------------
        [DllImport("gdi32.dll")]
        public static extern uint GetCharacterWidth(IntPtr hdc, char ch);

        [DllImport("user32.dll")]
        public static extern bool HideCaret(IntPtr hwnd);

        // ---- explorer / shell ------------------------------------------------
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SHGetFolderPath(IntPtr hwnd, int csidl, IntPtr token, uint flags,
                                                  StringBuilder path);

        public static string AppData()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        // ---- DPI ------------------------------------------------------------
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        public const int SM_CXVSCROLL = 17;
        public const int SM_CYSCROLL = 18;

        [DllImport("gdi32.dll")]
        public static extern int GetDeviceCaps(IntPtr hdc, int index);

        public const int LOGPIXELSX = 88;

        // ---- misc ------------------------------------------------------------
        [DllImport("user32.dll")]
        public static extern short VkKeyScan(char ch);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int ToUnicode(uint vk, uint scan, IntPtr keys, StringBuilder buf,
                                           int bufSize, uint flags);

        public const uint MAPVK_VK_TO_VSC = 0;

        // ---- DWM (soft dark drop shadow behind frameless windows) ------------
        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS
        {
            public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight;
        }

        [DllImport("dwmapi.dll")]
        public static extern int DwmExtendFrameIntoWindowArea(IntPtr hwnd, ref MARGINS m);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmIsCompositionEnabled(out bool enabled);

        /// <summary>Per-pixel alpha for a window; used by the edge deck only.</summary>
        public const byte LWA_ALPHA = 0x02;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint color, byte alpha,
                                                              uint flags);
    }
}
