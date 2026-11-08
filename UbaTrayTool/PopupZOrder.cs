using System.Runtime.InteropServices;

namespace UbaTrayTool;

/// <summary>
/// Keeps the popup above other windows. WS_EX_TOPMOST alone isn't enough: Show desktop
/// (Win+D, the taskbar corner) and clicks on the desktop or taskbar raise those windows into the
/// topmost band too, and a pinned popup ends up hidden behind them until it is reopened. While a
/// popup is visible it re-asserts HWND_TOPMOST whenever the foreground window changes, and every
/// half second in case nothing changed focus. Never activates the popup.
/// </summary>
internal static class PopupZOrder
{
    private const int ReassertMs = 500;

    private static readonly List<Form> Forms = [];
    private static WinEventProc? _hookProc;     // held so the delegate isn't collected
    private static IntPtr _hook;

    /// <summary>Call once from the popup's constructor.</summary>
    public static void KeepOnTop(Form form)
    {
        var timer = new System.Windows.Forms.Timer { Interval = ReassertMs };
        timer.Tick += (_, _) => BringToTop(form);
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
            Forms.Remove(form);
        };
        Forms.Add(form);
        InstallHook();
    }

    private static void BringToTop(Form form)
    {
        if (form.IsDisposed || !form.IsHandleCreated || !form.Visible)
            return;
        SetWindowPos(form.Handle, HwndTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    private static void InstallHook()
    {
        if (_hook != IntPtr.Zero)
            return;
        _hookProc = (_, _, _, _, _, _, _) =>
        {
            // Out-of-context hook: runs on this (UI) thread through its message loop.
            foreach (Form form in Forms.ToArray())
                BringToTop(form);
        };
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _hookProc,
            0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint eventThread, uint eventTime);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
        uint processId, uint threadId, uint flags);
}
