using System.Diagnostics;
using UbaTrayTool.Trace;

namespace UbaTrayTool;

/// <summary>Owns the tray icon, the popup and the trace monitor.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private const int HoverPollMs = 100;
    private const int HoverGraceMs = 300;
    /// <summary>When a build ends the popup slides away after this long (unless pinned or under the mouse).</summary>
    private const int AutoHideAfterBuildMs = 3000;

    private readonly NotifyIcon _tray;
    private readonly BuildPopup _popup = new();
    private readonly TraceMonitor _monitor = new();
    private readonly SynchronizationContext _ui;
    private readonly ToolStripMenuItem _pinItem;
    private readonly ToolStripMenuItem _autoShowItem;
    private readonly ToolStripMenuItem _notifyItem;
    private readonly ToolStripMenuItem _suppressItem;
    private readonly ToolStripMenuItem _visualizerItem;
    private readonly ToolStripMenuItem _saveItem;
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = HoverPollMs };
    private readonly System.Windows.Forms.Timer _autoHideTimer = new() { Interval = AutoHideAfterBuildMs };

    private Icon? _icon;
    private (BuildState State, int Step) _iconKey = (BuildState.Idle, -1);
    private Point _lastHoverPos;
    private long _lastHoverTick;
    private bool _autoShown;

    public TrayContext(string? openFile)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _pinItem = new ToolStripMenuItem("Keep open", null, (_, _) => SetPinned(!_popup.Pinned));
        _visualizerItem = new ToolStripMenuItem("Open in UbaVisualizer", null, (_, _) => OpenInVisualizer());
        _saveItem = new ToolStripMenuItem("Save trace as…", null, (_, _) => SaveTrace());
        _autoShowItem = new ToolStripMenuItem("Show when a build starts", null, (_, _) =>
        {
            Settings.AutoShow = !Settings.AutoShow;
        });
        _notifyItem = new ToolStripMenuItem("Notify when a build finishes", null, (_, _) =>
        {
            Settings.NotifyOnFinish = !Settings.NotifyOnFinish;
        });
        _suppressItem = new ToolStripMenuItem("Stop UBA launching UbaVisualizer", null, (_, _) =>
        {
            Settings.SuppressVisualizer = !Settings.SuppressVisualizer;
            _monitor.ActAsListener = Settings.SuppressVisualizer;
        });
        var startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_pinItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_visualizerItem);
        menu.Items.Add("Open trace file…", null, (_, _) => OpenTraceFile());
        menu.Items.Add(_saveItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autoShowItem);
        menu.Items.Add(_notifyItem);
        menu.Items.Add(_suppressItem);
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        menu.Opening += (_, _) =>
        {
            BuildSnapshot s = _monitor.Current;
            _pinItem.Checked = _popup.Pinned;
            _autoShowItem.Checked = Settings.AutoShow;
            _notifyItem.Checked = Settings.NotifyOnFinish;
            _suppressItem.Checked = Settings.SuppressVisualizer;
            startupItem.Checked = StartupRegistration.IsEnabled;
            _visualizerItem.Enabled = s.HasBuild;
            _saveItem.Enabled = s.HasBuild;
        };

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "UBA Tray Tool" };
        UpdateIcon(BuildSnapshot.Empty);
        // Show on the taskbar itself rather than behind the ^ overflow button.
        TrayIconPromotion.PromoteOnce();
        _tray.MouseMove += OnTrayMouseMove;
        _tray.MouseClick += OnTrayMouseClick;
        _tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenInVisualizer();
        };
        _tray.BalloonTipClicked += (_, _) => SetPinned(true);

        _popup.MouseClick += (_, _) => SetPinned(false);
        _hoverTimer.Tick += OnHoverTick;
        _autoHideTimer.Tick += (_, _) =>
        {
            if (_popup.Bounds.Contains(Cursor.Position))
                return;     // being read: try again on the next tick
            _autoHideTimer.Stop();
            _autoShown = false;
            if (!_popup.Pinned)
                _popup.HidePopup();
        };

        _monitor.ActAsListener = Settings.SuppressVisualizer;
        _monitor.Updated += s => _ui.Post(_ => OnSnapshot(s), null);
        _monitor.BuildStarted += s => _ui.Post(_ => OnBuildStarted(), null);
        _monitor.BuildEnded += s => _ui.Post(_ => OnBuildEnded(s), null);

        if (openFile is not null)
            _ui.Post(_ => OpenFile(openFile, show: true), null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _monitor.Dispose();
            _hoverTimer.Dispose();
            _autoHideTimer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _popup.Dispose();
            _icon?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- Monitor events ----------------------------------------------------------------------

    private void OnSnapshot(BuildSnapshot s)
    {
        if (s.Sequence < _monitor.Current.Sequence - 1)
            return;     // a newer one is queued
        _popup.SetSnapshot(s);
        UpdateIcon(s);
        _tray.Text = TooltipText(s);
    }

    private void OnBuildStarted()
    {
        _autoHideTimer.Stop();
        if (Settings.AutoShow && !_popup.Visible)
        {
            _autoShown = true;
            _popup.ShowPopup();
        }
    }

    private void OnBuildEnded(BuildSnapshot s)
    {
        if (Settings.NotifyOnFinish && !_popup.Visible)
        {
            string title = s.State switch
            {
                BuildState.Succeeded => "Build succeeded",
                BuildState.Failed => "Build failed",
                BuildState.Stopped => "Build stopped",
                _ => "Build ended",
            };
            string text = $"{Format.Words(s.Elapsed)}  ·  {Format.Count(s.Total > 0 ? s.Done : (uint)s.Finished)} actions";
            if (s.Errors > 0)
                text += $"  ·  {s.Errors} error{(s.Errors == 1 ? "" : "s")}";
            if (s.Failures.Count > 0)
                text += $"\n{s.Failures[0].Description}";
            _tray.ShowBalloonTip(5000, title, text, s.State switch { BuildState.Failed => ToolTipIcon.Error, BuildState.Stopped => ToolTipIcon.Warning, _ => ToolTipIcon.Info });
        }
        if (_popup.Visible && !_popup.Pinned)
        {
            _autoShown = true;      // keep the hover poll from closing it early
            _autoHideTimer.Stop();
            _autoHideTimer.Start();
        }
    }

    private static string TooltipText(BuildSnapshot s)
    {
        string text = s.State switch
        {
            BuildState.Running => s.Fraction is { } f ? $"UBA: building {f * 100:0}% ({s.Done}/{s.Total})" : $"UBA: building, {s.Running} running",
            BuildState.Succeeded => $"UBA: build succeeded in {Format.Words(s.Elapsed)}",
            BuildState.Failed => $"UBA: build failed, {s.Errors} errors",
            BuildState.Stopped => $"UBA: build stopped after {Format.Words(s.Elapsed)}",
            BuildState.Unreadable => "UBA: can't read trace",
            _ => "UBA Tray Tool: waiting for a build",
        };
        return text.Length > 127 ? text[..127] : text;
    }

    private void UpdateIcon(BuildSnapshot s)
    {
        int step = s.State == BuildState.Running ? (int)Math.Round((s.Fraction ?? 0) * 20) : 0;
        if (_iconKey == (s.State, step) && _icon is not null)
            return;
        _iconKey = (s.State, step);
        Icon? old = _icon;
        _icon = IconFactory.CreateIcon(SystemInformation.SmallIconSize.Width, s.State, s.Fraction);
        _tray.Icon = _icon;
        old?.Dispose();
    }

    // ---- Hover / pin -------------------------------------------------------------------------

    private void OnTrayMouseMove(object? sender, MouseEventArgs e)
    {
        _lastHoverPos = Cursor.Position;
        _lastHoverTick = Environment.TickCount64;
        if (!_popup.Visible || _popup.IsSlidingOut)
            _popup.ShowPopup();
        _hoverTimer.Start();
    }

    private void OnTrayMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            SetPinned(!_popup.Pinned);
    }

    // NotifyIcon has no MouseLeave, so poll the cursor.
    private void OnHoverTick(object? sender, EventArgs e)
    {
        if (!_popup.Visible)
        {
            _hoverTimer.Stop();
            return;
        }
        if (_popup.Pinned || _autoShown)
            return;
        Point cursor = Cursor.Position;
        if (cursor == _lastHoverPos || _popup.Bounds.Contains(cursor))
            return;
        if (Environment.TickCount64 - _lastHoverTick < HoverGraceMs)
            return;
        _popup.HidePopup();
        _hoverTimer.Stop();
    }

    private void SetPinned(bool pinned)
    {
        _popup.Pinned = pinned;
        _autoShown = false;
        _autoHideTimer.Stop();
        if (pinned)
            _popup.ShowPopup();
        else
            _popup.HidePopup();
    }

    // ---- Commands ----------------------------------------------------------------------------

    private void OpenTraceFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open UBA trace",
            Filter = "UBA traces (*.uba)|*.uba|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() == DialogResult.OK)
            OpenFile(dialog.FileName, show: true);
    }

    private void OpenFile(string path, bool show)
    {
        try
        {
            _monitor.OpenFile(path);
            if (show)
                SetPinned(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not open {path}.\n\n{ex.Message}", "UBA Tray Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SaveTrace()
    {
        BuildSnapshot s = _monitor.Current;
        using var dialog = new SaveFileDialog
        {
            Title = "Save UBA trace",
            Filter = "UBA traces (*.uba)|*.uba",
            FileName = $"{SafeName(s.TraceName)}.uba",
        };
        if (dialog.ShowDialog() != DialogResult.OK)
            return;
        try
        {
            _monitor.SaveTrace(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not save the trace.\n\n{ex.Message}", "UBA Tray Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenInVisualizer()
    {
        BuildSnapshot s = _monitor.Current;
        string? exe = s.VisualizerPath ?? Settings.VisualizerPath;
        if (exe is null || !File.Exists(exe))
            exe = VisualizerLocator.FromInstalledEngines();
        if (exe is null)
        {
            using var dialog = new OpenFileDialog { Title = "Find UbaVisualizer.exe", Filter = "UbaVisualizer.exe|UbaVisualizer.exe" };
            if (dialog.ShowDialog() != DialogResult.OK)
                return;
            exe = dialog.FileName;
        }
        Settings.VisualizerPath = exe;

        string args;
        if (s.State == BuildState.Running && s.IsLive)
            args = $"-named={s.TraceName}";
        else if (s.FilePath is not null)
            args = $"-file=\"{s.FilePath}\"";
        else if (s.HasBuild)
        {
            string dir = Path.Combine(Path.GetTempPath(), "UbaTrayTool");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"{SafeName(s.TraceName)}.uba");
            if (!_monitor.SaveTrace(file))
                return;
            args = $"-file=\"{file}\"";
        }
        else
            args = $"-listen={TraceMonitor.DefaultChannel}";

        try
        {
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            MessageBox.Show($"Could not start UbaVisualizer.\n\n{ex.Message}", "UBA Tray Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "trace";
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private static void ToggleStartup()
    {
        try
        {
            StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            MessageBox.Show($"Could not change the startup setting.\n\n{ex.Message}", "UBA Tray Tool",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
