using System.Runtime.InteropServices;
using System.Text;

namespace UbaTrayTool;

/// <summary>
/// Keeps the popup above other windows. WS_EX_TOPMOST alone isn't enough: Show desktop
/// (Win+D, the taskbar corner) and clicks on the desktop or taskbar raise those windows into the
/// topmost band too, and a pinned popup ends up hidden behind them until it is reopened. While a
/// popup is visible it checks whenever the foreground window changes, and every half second, and
/// goes back on top only when the desktop, the taskbar or a window that isn't topmost is covering
/// it. Menus, tooltips, the Start menu and other topmost windows are left above it, so a tray
/// menu opened over a pinned popup stays visible. Never activates the popup.
/// </summary>
internal static class PopupZOrder
{
    private const int ReassertMs = 500;

    private static readonly List<(Form Form, Func<bool>? IsClosing)> Forms = [];
    private static WinEventProc? _hookProc;     // held so the delegate isn't collected
    private static IntPtr _hook;

    /// <summary>Call once from the popup's constructor.</summary>
    /// <param name="isClosing">True while the popup slides away: it drops behind the taskbar as it
    /// goes and isn't pulled back in front.</param>
    public static void KeepOnTop(Form form, Func<bool>? isClosing = null)
    {
        var timer = new System.Windows.Forms.Timer { Interval = ReassertMs };
        timer.Tick += (_, _) => RaiseIfCovered(form, isClosing);
        form.VisibleChanged += (_, _) =>
        {
            if (form.Visible)
            {
                BringToTop(form);
                timer.Start();
            }
            else
            {
                timer.Stop();
            }
        };
        form.Disposed += (_, _) =>
        {
            timer.Dispose();
            Forms.RemoveAll(f => f.Form == form);
        };
        Forms.Add((form, isClosing));
        InstallHook();
    }

    private static bool CanRaise(Form form) => !form.IsDisposed && form.IsHandleCreated && form.Visible;

    private static void BringToTop(Form form)
    {
        if (!CanRaise(form))
            return;
        SetWindowPos(form.Handle, HwndTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    private static void RaiseIfCovered(Form form, Func<bool>? isClosing)
    {
        if (CanRaise(form) && isClosing?.Invoke() != true && IsCoveredByShell(form.Handle))
            BringToTop(form);
    }

    /// <summary>
    /// True when a window above the popup that overlaps it is the desktop or taskbar, or isn't
    /// topmost at all (only the shell gets there). Anything else above it, such as a tray menu, a
    /// tooltip or another app's always-on-top window, was put there on purpose and stays.
    /// </summary>
    private static bool IsCoveredByShell(IntPtr self)
    {
        if (!GetWindowRect(self, out NativeRect me))
            return false;
        var name = new StringBuilder(64);
        IntPtr h = self;
        for (int guard = 0; guard < 1000; guard++)
        {
            h = GetWindow(h, GwHwndPrev);
            if (h == IntPtr.Zero)
                return false;
            if (!IsWindowVisible(h) || !GetWindowRect(h, out NativeRect r))
                continue;
            if (r.Right <= me.Left || r.Left >= me.Right || r.Bottom <= me.Top || r.Top >= me.Bottom)
                continue;   // doesn't overlap
            name.Clear();
            GetClassName(h, name, name.Capacity);
            string cls = name.ToString();
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                return true;
            if ((GetWindowLongPtr(h, GwlExStyle).ToInt64() & WsExTopmost) == 0)
                return true;
        }
        return false;
    }

    private static void InstallHook()
    {
        if (_hook != IntPtr.Zero)
            return;
        _hookProc = (_, _, _, _, _, _, _) =>
        {
            // Out-of-context hook: runs on this (UI) thread through its message loop.
            foreach ((Form form, Func<bool>? isClosing) in Forms.ToArray())
                RaiseIfCovered(form, isClosing);
        };
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _hookProc,
            0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint GwHwndPrev = 3;
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008;
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint eventThread, uint eventTime);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
        uint processId, uint threadId, uint flags);
}
