using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using UbaTrayTool.Trace;

namespace UbaTrayTool;

/// <summary>
/// Borderless, non-activating popup above the tray. One card per running task (up to the
/// processor count); cards keep their slot like lanes in UbaVisualizer, and the window grows
/// and shrinks smoothly as the number of running tasks changes.
/// </summary>
internal sealed class BuildPopup : Form
{
    // Layout at 96 dpi.
    private const int Pad = 16;
    private const int MinWidth = 460;
    private const int RingSize = 58;
    private const int HeaderHeight = 62;
    private const int BarHeight = 6;
    private const int ChartHeight = 46;
    private const int SectionLabelHeight = 22;
    private const int CardHeight = 40;
    private const int CardGap = 6;
    private const int WideCardWidth = 270;
    private const int MinCardWidth = 210;
    /// <summary>Share of the screen height the task grid may use when every processor is busy.</summary>
    private const double GridScreenShare = 0.6;
    /// <summary>How long the grid must have been too big before it shrinks (stops the window jumping about).</summary>
    private const int ShrinkDelayMs = 4000;
    private const int FailureCardHeight = 46;
    private const int ChipHeight = 26;
    private const int ScreenMargin = 10;
    private const int ResizeMs = 220;
    private const int SlideMs = 200;

    private Theme _theme = Theme.Current();
    private BuildSnapshot _snapshot = BuildSnapshot.Empty;
    private bool _pinned;

    // Stable lanes: process id -> slot. Slots fill row by row, so only the height changes.
    private readonly Dictionary<uint, int> _slotOf = [];
    private RunningAction?[] _slots = [];
    private int _shownRows;                 // grid rows on screen (debounced when shrinking)
    private long _shrinkSince = -1;         // when the grid first became bigger than needed
    private readonly System.Windows.Forms.Timer _shrinkTimer = new() { Interval = 250 };

    // Size / slide animation.
    private readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 15 };
    // Repaints the ring's spinner while the total isn't known yet.
    private readonly System.Windows.Forms.Timer _spinTimer = new() { Interval = 40 };
    private Rectangle _fromBounds, _toBounds;
    private long _animStart;
    private int _animMs;
    private bool _sliding;
    private Action? _animDone;

    private Fonts? _fonts;

    public BuildPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        Text = "UbaTrayTool";
        BackColor = _theme.Background;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _animTimer.Tick += (_, _) => StepAnimation();
        _spinTimer.Tick += (_, _) => Invalidate(new Rectangle(0, 0, S(Pad + RingSize + 4), S(Pad + RingSize + 4)));
        VisibleChanged += (_, _) => UpdateSpinner();
        _shrinkTimer.Tick += (_, _) => Regrid();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Size = TargetSize();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _animTimer.Dispose();
            _spinTimer.Dispose();
            _shrinkTimer.Dispose();
            _fonts?.Dispose();
        }
        base.Dispose(disposing);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Pinned
    {
        get => _pinned;
        set
        {
            _pinned = value;
            Invalidate();
        }
    }

    public bool IsSlidingOut => _sliding;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x00000008;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int CS_DROPSHADOW = 0x00020000;
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _theme.ApplyWindowChrome(Handle);
    }

    /// <summary>
    /// Overall size of the popup relative to the 96-dpi layout constants. 0.75 keeps it compact on
    /// machines with lots of processors (one card each); everything — fonts, cards, padding — scales together.
    /// </summary>
    private const float UiScale = 0.75f;

    private float Dpi => DeviceDpi / 96f * UiScale;
    private int S(float v) => (int)Math.Round(v * Dpi);

    // ---- Theme ------------------------------------------------------------------------------

    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.Color
            or Microsoft.Win32.UserPreferenceCategory.VisualStyle)
        {
            if (InvokeRequired)
                BeginInvoke(SyncTheme);
            else
                SyncTheme();
        }
    }

    /// <summary>For screenshots: use the given theme instead of the system one.</summary>
    public void ForceTheme(bool light)
    {
        _forcedTheme = true;
        _theme = Theme.Create(light, Theme.AccentColor());
        BackColor = _theme.Background;
    }

    private bool _forcedTheme;

    public void SyncTheme()
    {
        if (_forcedTheme)
            return;
        Theme theme = Theme.Current();
        if (theme.Light == _theme.Light && theme.Accent == _theme.Accent)
            return;
        _theme = theme;
        BackColor = theme.Background;
        if (IsHandleCreated)
            theme.ApplyWindowChrome(Handle);
        Invalidate();
    }

    // ---- Data -------------------------------------------------------------------------------

    public void SetSnapshot(BuildSnapshot snapshot)
    {
        _snapshot = snapshot;
        UpdateSpinner();
        Regrid();
        Invalidate();
    }

    /// <summary>
    /// Works out how many grid rows to show. Growing happens at once; shrinking waits until the
    /// grid has been too big for <see cref="ShrinkDelayMs"/>, so the window doesn't bob up and down
    /// as jobs finish and new ones start.
    /// </summary>
    private void Regrid()
    {
        int cols = Columns;
        int needed = (int)Math.Ceiling(_snapshot.RunningActions.Count / (double)cols);
        long now = Environment.TickCount64;
        int rows = _shownRows;
        if (needed >= rows || !Visible)
        {
            // Grow at once; while hidden there is nothing to jump about, so shrink at once too.
            rows = needed;
            _shrinkSince = -1;
        }
        else if (_shrinkSince < 0)
            _shrinkSince = now;
        else if (now - _shrinkSince >= ShrinkDelayMs)
        {
            rows = needed;
            _shrinkSince = -1;
        }
        _shrinkTimer.Enabled = _shrinkSince >= 0;

        _shownRows = rows;
        AssignSlots(_snapshot.RunningActions, rows * cols);

        // Other sections (errors, machines, chart) can change the height too, so always re-target;
        // AnimateTo does nothing when the size is already right.
        if (Visible && !_sliding)
            AnimateTo(TargetBounds());
        else if (!Visible)
            Size = TargetSize();
        Invalidate();
    }

    private void UpdateSpinner() =>
        _spinTimer.Enabled = Visible && _snapshot.State == BuildState.Running && _snapshot.Fraction is null;

    /// <summary>
    /// Keeps each running task in the same card while it runs. When the grid shrinks, tasks in
    /// slots past the end move into the holes left by finished ones.
    /// </summary>
    private void AssignSlots(IReadOnlyList<RunningAction> running, int capacity)
    {
        int count = capacity;
        var present = new HashSet<uint>(running.Select(r => r.Id));
        foreach (uint id in _slotOf.Keys.ToList())
            if (!present.Contains(id))
                _slotOf.Remove(id);
        foreach (var pair in _slotOf.Where(p => p.Value >= count).ToList())
            _slotOf.Remove(pair.Key);

        var used = new bool[count];
        foreach (int slot in _slotOf.Values)
            used[slot] = true;
        int next = 0;
        foreach (RunningAction r in running)
        {
            if (_slotOf.ContainsKey(r.Id))
                continue;
            while (next < count && used[next])
                next++;
            if (next >= count)
                break;
            _slotOf[r.Id] = next;
            used[next] = true;
        }

        _slots = new RunningAction?[count];
        foreach (RunningAction r in running)
            if (_slotOf.TryGetValue(r.Id, out int slot))
                _slots[slot] = r;
    }

    // ---- Layout -----------------------------------------------------------------------------

    private sealed record PopupLayout(
        Size Size, int Cols, int Rows, int CardWidth,
        int GridTop, int FailuresTop, int MachinesTop, int ChipRows, int OverflowTop,
        int FailureCount, bool ShowChart, bool ShowRunning);

    private Rectangle WorkArea()
    {
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        Screen screen = taskbar != IntPtr.Zero && GetWindowRect(taskbar, out NativeRect r)
            ? Screen.FromRectangle(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom))
            : Screen.PrimaryScreen ?? Screen.AllScreens[0];
        return screen.WorkingArea;
    }

    private int _columns;
    private Rectangle _columnsArea;
    private int _columnsDpi;

    /// <summary>
    /// Fixed for the session (screen size and DPI permitting): enough columns that one card per
    /// logical processor fits in <see cref="GridScreenShare"/> of the screen height.
    /// </summary>
    private int Columns
    {
        get
        {
            Rectangle area = WorkArea();
            if (_columns > 0 && area == _columnsArea && DeviceDpi == _columnsDpi)
                return _columns;
            // Worked out at full size, so UiScale shrinks the whole popup (same columns, smaller
            // cards) instead of trading width for an even taller grid.
            float fullScale = DeviceDpi / 96f;
            int fitRows = Math.Max(4, (int)((area.Height * GridScreenShare + CardGap * fullScale) / ((CardHeight + CardGap) * fullScale)));
            _columns = Math.Max(1, (int)Math.Ceiling(TraceMonitor.MaxRunningRows / (double)fitRows));
            _columnsArea = area;
            _columnsDpi = DeviceDpi;
            return _columns;
        }
    }

    /// <summary>Card width for the fixed column count; the popup width never changes.</summary>
    private (int Width, int CardWidth) FixedWidth()
    {
        Rectangle area = WorkArea();
        int pad = S(Pad);
        int cols = Columns;
        int cardWidth = cols == 1 ? S(MinWidth) - 2 * pad : S(WideCardWidth);
        int maxWidth = area.Width - 2 * S(ScreenMargin);
        while (cols > 1 && 2 * pad + cols * cardWidth + (cols - 1) * S(CardGap) > maxWidth && cardWidth > S(MinCardWidth))
            cardWidth -= S(10);
        int width = Math.Max(S(MinWidth), 2 * pad + cols * cardWidth + (cols - 1) * S(CardGap));
        if (cols == 1)
            cardWidth = width - 2 * pad;
        else
            cardWidth = (width - 2 * pad - (cols - 1) * S(CardGap)) / cols;
        return (width, cardWidth);
    }

    private PopupLayout ComputeLayout()
    {
        Rectangle area = WorkArea();
        BuildSnapshot s = _snapshot;
        int pad = S(Pad);
        int y = pad + S(HeaderHeight) + S(10) + S(BarHeight);

        bool showChart = s.HasBuild && s.Concurrency.Count > 0 && s.MaxActive > 0;
        if (showChart)
            y += S(14) + S(SectionLabelHeight) + S(ChartHeight);

        int n = _slots.Length;
        bool showRunning = n > 0;
        int failureCount = Math.Min(s.Failures.Count, s.State == BuildState.Running ? 2 : 4);
        int machines = s.Sessions.Count;

        int cols = Columns;
        int rows = _shownRows;
        (int width, int cardWidth) = FixedWidth();
        int gridTop = y;
        if (showRunning)
        {
            gridTop = y + S(14) + S(SectionLabelHeight);
            y = gridTop + rows * (S(CardHeight) + S(CardGap)) - S(CardGap);
        }

        int overflowTop = y;
        int hidden = Math.Max(0, s.Running - s.RunningActions.Count);
        if (showRunning && hidden > 0)
            y += S(20);

        int failuresTop = y;
        if (failureCount > 0)
            y += S(14) + S(SectionLabelHeight) + failureCount * (S(FailureCardHeight) + S(CardGap)) - S(CardGap);

        int machinesTop = y;
        int chipRows = 0;
        if (machines > 0)
        {
            chipRows = MeasureChipRows(width - 2 * pad);
            y += S(14) + S(SectionLabelHeight) + chipRows * (S(ChipHeight) + S(CardGap)) - S(CardGap);
        }

        y += pad;
        int height = Math.Min(y, area.Height - 2 * S(ScreenMargin));
        return new PopupLayout(new Size(width, height), cols, rows, cardWidth, gridTop, failuresTop, machinesTop,
            chipRows, overflowTop, failureCount, showChart, showRunning);
    }

    private Size TargetSize() => ComputeLayout().Size;

    private Rectangle TargetBounds()
    {
        Size size = TargetSize();
        Rectangle area = WorkArea();
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        bool taskbarAtTop = taskbar != IntPtr.Zero && GetWindowRect(taskbar, out NativeRect r) && r.Top <= area.Top && r.Bottom <= area.Top + 4;
        int x = area.Right - S(ScreenMargin) - size.Width;
        int y = taskbarAtTop ? area.Top + S(ScreenMargin) : area.Bottom - S(ScreenMargin) - size.Height;
        return new Rectangle(new Point(Math.Max(area.Left, x), Math.Max(area.Top, y)), size);
    }

    // ---- Show / hide / animation -----------------------------------------------------------

    public void ShowPopup()
    {
        SyncTheme();
        if (_sliding)
        {
            _sliding = false;
            _animDone = null;
            AnimateTo(TargetBounds());
            return;
        }
        if (Visible)
            return;
        Bounds = TargetBounds();
        Show();
    }

    /// <summary>Slides down off the screen like a card, then hides.</summary>
    public void HidePopup()
    {
        if (!Visible || _sliding)
            return;
        _sliding = true;
        Rectangle screen = Screen.FromControl(this).Bounds;
        StartAnimation(new Rectangle(Left, screen.Bottom + 4, Width, Height), SlideMs, () =>
        {
            _sliding = false;
            Hide();
        });
    }

    private void AnimateTo(Rectangle target)
    {
        if (Bounds == target || (_animTimer.Enabled && _toBounds == target))
            return;
        StartAnimation(target, ResizeMs, null);
    }

    private void StartAnimation(Rectangle target, int ms, Action? done)
    {
        _fromBounds = Bounds;
        _toBounds = target;
        _animStart = Environment.TickCount64;
        _animMs = ms;
        _animDone = done;
        _animTimer.Start();
    }

    private void StepAnimation()
    {
        double t = Math.Clamp((Environment.TickCount64 - _animStart) / (double)_animMs, 0, 1);
        double e = _sliding ? t * t : 1 - Math.Pow(1 - t, 3);
        Bounds = new Rectangle(
            (int)Math.Round(_fromBounds.X + (_toBounds.X - _fromBounds.X) * e),
            (int)Math.Round(_fromBounds.Y + (_toBounds.Y - _fromBounds.Y) * e),
            (int)Math.Round(_fromBounds.Width + (_toBounds.Width - _fromBounds.Width) * e),
            (int)Math.Round(_fromBounds.Height + (_toBounds.Height - _fromBounds.Height) * e));
        if (t >= 1)
        {
            _animTimer.Stop();
            Action? done = _animDone;
            _animDone = null;
            done?.Invoke();
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _fonts?.Dispose();
        _fonts = null;
        if (Visible)
            Bounds = TargetBounds();
        Invalidate();
    }

    public Bitmap RenderToBitmap()
    {
        Size = TargetSize();
        var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using Graphics g = Graphics.FromImage(bmp);
        Draw(g);
        return bmp;
    }

    // ---- Drawing ----------------------------------------------------------------------------

    private sealed class Fonts : IDisposable
    {
        public readonly Font Title, Big, Body, BodyBold, Small, SmallBold, Label, Percent;

        public Fonts(float dpi)
        {
            Title = new Font("Segoe UI Semibold", 16f * dpi, FontStyle.Regular, GraphicsUnit.Pixel);
            Big = new Font("Segoe UI Semibold", 19f * dpi, FontStyle.Regular, GraphicsUnit.Pixel);
            Percent = new Font("Segoe UI Semibold", 14f * dpi, FontStyle.Regular, GraphicsUnit.Pixel);
            Body = new Font("Segoe UI", 12.5f * dpi, FontStyle.Regular, GraphicsUnit.Pixel);
            BodyBold = new Font("Segoe UI Semibold", 12.5f * dpi, FontStyle.Regular, GraphicsUnit.Pixel);
            Small = new Font("Segoe UI", 11f * dpi, FontStyle.Regular, GraphicsUnit.Pixel);
            SmallBold = new Font("Segoe UI Semibold", 11f * dpi, FontStyle.Regular, GraphicsUnit.Pixel);
            Label = new Font("Segoe UI Semibold", 10.5f * dpi, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        public void Dispose()
        {
            foreach (Font f in new[] { Title, Big, Body, BodyBold, Small, SmallBold, Label, Percent })
                f.Dispose();
        }
    }

    private const TextFormatFlags LeftFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
        TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
    private const TextFormatFlags RightFlags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
    private const TextFormatFlags CenterFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    protected override void OnPaint(PaintEventArgs e) => Draw(e.Graphics);

    private Color StateColor => _snapshot.State switch
    {
        BuildState.Running => _theme.Accent,
        BuildState.Succeeded => _theme.Success,
        BuildState.Failed => _theme.Danger,
        BuildState.Stopped => _theme.Warning,
        BuildState.Unreadable => _theme.Warning,
        _ => _theme.FaintText,
    };

    private void Draw(Graphics g)
    {
        _fonts ??= new Fonts(Dpi);
        Fonts f = _fonts;
        Theme t = _theme;
        BuildSnapshot s = _snapshot;
        PopupLayout layout = ComputeLayout();

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(t.Background);

        int pad = S(Pad);
        int width = layout.Size.Width - 2 * pad;

        // Pinned: accent hairline across the top.
        if (_pinned)
        {
            using var pin = new SolidBrush(t.Accent);
            g.FillRectangle(pin, 0, 0, layout.Size.Width, S(3));
        }

        DrawHeader(g, f, t, s, pad, width);

        // Progress bar.
        int barTop = pad + S(HeaderHeight) + S(10);
        var bar = new RectangleF(pad, barTop, width, S(BarHeight));
        double fraction = s.Fraction ?? (s.State is BuildState.Succeeded or BuildState.Failed ? 1 : 0);
        DrawBar(g, bar, fraction, StateColor, t.Track);

        if (layout.ShowChart)
        {
            int labelTop = barTop + S(BarHeight) + S(14);
            DrawSectionLabel(g, f, t, "PARALLEL TASKS", $"peak {s.MaxActive}  ·  {Environment.ProcessorCount} logical processors", pad, labelTop, width);
            DrawChart(g, f, t, s, new Rectangle(pad, labelTop + S(SectionLabelHeight), width, S(ChartHeight)));
        }

        if (layout.ShowRunning)
            DrawRunning(g, f, t, s, layout, pad, width);

        if (layout.FailureCount > 0)
            DrawFailures(g, f, t, s, layout, pad, width);

        if (s.Sessions.Count > 0)
            DrawMachines(g, f, t, s, layout, pad, width);
    }

    private void DrawHeader(Graphics g, Fonts f, Theme t, BuildSnapshot s, int pad, int width)
    {
        // Progress ring.
        var ring = new RectangleF(pad + S(3), pad + S(2), S(RingSize - 6), S(RingSize - 6));
        float stroke = S(6);
        using (var trackPen = new Pen(t.Track, stroke))
            g.DrawEllipse(trackPen, ring);

        Color colour = StateColor;
        string centre;
        if (s.State == BuildState.Running && s.Fraction is null)
        {
            // Unknown total: spinning arc.
            float phase = Environment.TickCount64 % 1200 / 1200f * 360f;
            using var pen = new Pen(colour, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, ring, phase - 90, 90);
            centre = s.Running > 0 ? Format.Count(s.Running) : "…";
        }
        else
        {
            double fr = s.Fraction ?? (s.State is BuildState.Succeeded or BuildState.Failed ? 1 : 0);
            if (fr > 0)
            {
                using var pen = new Pen(colour, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, ring, -90, (float)(fr * 360));
            }
            centre = s.State switch
            {
                BuildState.Succeeded => "✓",
                BuildState.Failed => "!",
                BuildState.Stopped => "!",
                BuildState.Idle => "–",
                BuildState.Unreadable => "?",
                _ => $"{fr * 100:0}%",
            };
        }
        Rectangle ringRect = Rectangle.Round(ring);
        Font centreFont = centre.Length <= 1 ? f.Big : f.Percent;
        TextRenderer.DrawText(g, centre, centreFont, ringRect, s.State is BuildState.Running or BuildState.Idle ? t.Text : colour,
            t.Background, CenterFlags);

        // Title and details.
        int textLeft = pad + S(RingSize) + S(14);
        int rightWidth = S(110);
        int textWidth = width - (textLeft - pad) - rightWidth - S(8);

        string title = s.State switch
        {
            BuildState.Running => "Building",
            BuildState.Succeeded => "Build succeeded",
            BuildState.Failed => "Build failed",
            BuildState.Stopped => "Build stopped",
            BuildState.Unreadable => "Can't read this trace",
            _ => "Waiting for a build",
        };
        TextRenderer.DrawText(g, title, f.Title, new Rectangle(textLeft, pad, textWidth, S(22)), t.Text, t.Background, LeftFlags);

        string line2;
        if (s.State == BuildState.Idle)
            line2 = "Listening for Unreal Build Accelerator";
        else if (s.State == BuildState.Unreadable)
            line2 = s.Error ?? "Unknown trace format";
        else if (s.Total > 0)
            line2 = $"{Format.Count(s.Done)} of {Format.Count(s.Total)} actions";
        else
            line2 = $"{Format.Count(s.Finished)} actions done";
        var line2Rect = new Rectangle(textLeft, pad + S(22), textWidth, S(18));
        TextRenderer.DrawText(g, line2, f.Body, line2Rect, t.DimText, t.Background, LeftFlags);
        if (s.Errors > 0 && s.State != BuildState.Unreadable)
        {
            int used = TextRenderer.MeasureText(g, line2, f.Body, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            string errors = $"  ·  {Format.Count(s.Errors)} error{(s.Errors == 1 ? "" : "s")}";
            TextRenderer.DrawText(g, errors, f.BodyBold, new Rectangle(line2Rect.Left + used, line2Rect.Top, Math.Max(0, line2Rect.Width - used), line2Rect.Height),
                t.Danger, t.Background, LeftFlags);
        }

        var parts = new List<string>();
        if (s.State == BuildState.Idle)
            parts.Add("Start a build in Visual Studio or UBT");
        else
        {
            if (s.State == BuildState.Running)
                parts.Add(s.RunningRemote > 0 ? $"{s.Running} running ({s.RunningRemote} remote)" : $"{s.Running} running");
            if (s.MaxActive > 0)
                parts.Add($"peak {s.MaxActive}");
            if (s.CacheHits > 0)
                parts.Add($"{Format.Count(s.CacheHits)} cache hits");
            if (!s.IsLive && s.FilePath is not null)
                parts.Add(Path.GetFileName(s.FilePath));
        }
        TextRenderer.DrawText(g, string.Join("  ·  ", parts), f.Small, new Rectangle(textLeft, pad + S(41), textWidth, S(16)),
            t.FaintText, t.Background, LeftFlags);

        // Right: elapsed and ETA / finish time.
        int rightLeft = pad + width - rightWidth;
        if (s.HasBuild && s.State != BuildState.Unreadable)
        {
            TextRenderer.DrawText(g, Format.Duration(s.Elapsed), f.Big, new Rectangle(rightLeft, pad - S(1), rightWidth, S(26)),
                t.Text, t.Background, RightFlags);
            string sub = "";
            if (s.State == BuildState.Running)
            {
                if (s.Total > 0 && s.Done > 0 && s.Done < s.Total && s.Elapsed.TotalSeconds > 5)
                {
                    double remaining = s.Elapsed.TotalSeconds / s.Done * (s.Total - s.Done);
                    sub = $"~{Format.Duration(TimeSpan.FromSeconds(remaining))} left";
                }
                else
                    sub = "elapsed";
            }
            else if (s.StartUtc is { } start)
                sub = $"finished {(start + s.Elapsed).ToLocalTime():HH:mm}";
            TextRenderer.DrawText(g, sub, f.Small, new Rectangle(rightLeft, pad + S(26), rightWidth, S(16)),
                t.FaintText, t.Background, RightFlags);
        }
        if (_pinned)
            TextRenderer.DrawText(g, "pinned", f.Label, new Rectangle(rightLeft, pad + S(42), rightWidth, S(14)),
                t.Accent, t.Background, RightFlags);
    }

    private static void DrawBar(Graphics g, RectangleF track, double fraction, Color fill, Color trackColour)
    {
        float radius = track.Height / 2f;
        using (GraphicsPath trackPath = IconFactory.RoundedRect(track, radius))
        using (var brush = new SolidBrush(trackColour))
            g.FillPath(brush, trackPath);
        float w = (float)(track.Width * Math.Clamp(fraction, 0, 1));
        if (w < 1)
            return;
        var fillRect = new RectangleF(track.X, track.Y, Math.Max(w, track.Height), track.Height);
        using GraphicsPath fillPath = IconFactory.RoundedRect(fillRect, radius);
        using var fillBrush = new LinearGradientBrush(RectangleF.Inflate(fillRect, 1, 1),
            Theme.Lighten(fill, 0.25), fill, LinearGradientMode.Horizontal);
        g.FillPath(fillBrush, fillPath);
    }

    private void DrawChart(Graphics g, Fonts f, Theme t, BuildSnapshot s, Rectangle r)
    {
        using (GraphicsPath bg = IconFactory.RoundedRect(r, S(6)))
        using (var brush = new SolidBrush(t.Surface))
            g.FillPath(brush, bg);

        IReadOnlyList<float> values = s.Concurrency;
        float max = Math.Max(1, s.MaxActive);
        var inner = Rectangle.Inflate(r, -S(6), -S(5));
        var points = new List<PointF>(values.Count + 2);
        for (int i = 0; i < values.Count; i++)
        {
            float x = inner.Left + inner.Width * (values.Count == 1 ? 0 : i / (float)(values.Count - 1));
            float y = inner.Bottom - inner.Height * Math.Clamp(values[i] / max, 0, 1);
            points.Add(new PointF(x, y));
        }
        if (points.Count >= 2)
        {
            Color c = StateColor;
            using var area = new GraphicsPath();
            area.AddLine(inner.Left, inner.Bottom, points[0].X, points[0].Y);
            area.AddLines(points.ToArray());
            area.AddLine(points[^1].X, points[^1].Y, inner.Right, inner.Bottom);
            area.CloseFigure();
            using (var fill = new LinearGradientBrush(inner, Theme.WithAlpha(c, 110), Theme.WithAlpha(c, 12), LinearGradientMode.Vertical))
                g.FillPath(fill, area);
            using var line = new Pen(c, Math.Max(1.5f, S(1.6f))) { LineJoin = LineJoin.Round };
            g.DrawLines(line, points.ToArray());
        }

        // Dotted line at the processor count, when the build gets near it.
        if (Environment.ProcessorCount <= max)
        {
            using var cap = new Pen(t.FaintText, 1f) { DashStyle = DashStyle.Dot };
            float cy = inner.Bottom - inner.Height * Math.Clamp(Environment.ProcessorCount / max, 0, 1);
            g.DrawLine(cap, inner.Left, cy, inner.Right, cy);
        }
    }

    private void DrawSectionLabel(Graphics g, Fonts f, Theme t, string label, string? right, int left, int top, int width, Color? labelColour = null)
    {
        var rect = new Rectangle(left, top, width, S(SectionLabelHeight) - S(6));
        TextRenderer.DrawText(g, label, f.Label, rect, labelColour ?? t.DimText, t.Background, LeftFlags);
        if (right is not null)
            TextRenderer.DrawText(g, right, f.Small, rect, t.FaintText, t.Background, RightFlags);
    }

    private void DrawRunning(Graphics g, Fonts f, Theme t, BuildSnapshot s, PopupLayout layout, int pad, int width)
    {
        int remote = _slots.Count(x => x is { Remote: true });
        DrawSectionLabel(g, f, t, $"RUNNING  {_slots.Count(x => x is not null)}",
            remote > 0 ? $"{remote} on helpers" : $"{Environment.ProcessorCount} lanes", pad, layout.GridTop - S(SectionLabelHeight), width);

        double longest = Math.Max(1, _slots.Max(x => x?.Seconds ?? 0));
        for (int i = 0; i < _slots.Length; i++)
        {
            int col = i % layout.Cols;
            int row = i / layout.Cols;
            var card = new Rectangle(
                pad + col * (layout.CardWidth + S(CardGap)),
                layout.GridTop + row * (S(CardHeight) + S(CardGap)),
                layout.CardWidth, S(CardHeight));
            DrawTaskCard(g, f, t, card, _slots[i], longest);
        }

        int hidden = Math.Max(0, s.Running - s.RunningActions.Count);
        if (hidden > 0)
            TextRenderer.DrawText(g, $"+ {hidden} more running", f.Small,
                new Rectangle(pad, layout.OverflowTop + S(4), width, S(16)), t.FaintText, t.Background, LeftFlags);
    }

    private void DrawTaskCard(Graphics g, Fonts f, Theme t, Rectangle card, RunningAction? action, double longest)
    {
        using GraphicsPath path = IconFactory.RoundedRect(card, S(7));
        using (var brush = new SolidBrush(action is null ? Theme.Lerp(t.Surface, t.Background, 0.5) : t.Surface))
            g.FillPath(brush, path);
        if (action is null)
            return;

        Color lane = t.LaneColor(action.Description);

        // Lane stripe.
        GraphicsState state = g.Save();
        g.SetClip(path);
        using (var stripe = new SolidBrush(lane))
            g.FillRectangle(stripe, card.Left, card.Top, S(4), card.Height);

        // Duration bar along the bottom, relative to the longest running task.
        float frac = (float)Math.Clamp(action.Seconds / longest, 0.02, 1);
        using (var bar = new SolidBrush(Theme.WithAlpha(lane, t.Light ? 70 : 90)))
            g.FillRectangle(bar, card.Left, card.Bottom - S(3), card.Width * frac, S(3));
        g.Restore(state);

        int left = card.Left + S(12);
        int timeWidth = S(56);
        (string name, string detail) = SplitDescription(action.Description);
        TextRenderer.DrawText(g, name, f.BodyBold, new Rectangle(left, card.Top + S(4), card.Width - S(12) - timeWidth - S(10), S(17)),
            t.Text, t.Surface, LeftFlags);

        string where = action.Remote ? $"⇄ {action.Where}" : "local";
        if (action.Racing)
            where += " · racing";
        if (detail.Length > 0)
            where = $"{detail} · {where}";
        TextRenderer.DrawText(g, where, f.Small, new Rectangle(left, card.Top + S(21), card.Width - S(12) - timeWidth - S(10), S(14)),
            t.FaintText, t.Surface, LeftFlags);

        Color timeColour = action.Seconds >= 60 ? t.Warning : t.DimText;
        TextRenderer.DrawText(g, Format.Seconds(action.Seconds), f.SmallBold,
            new Rectangle(card.Right - timeWidth - S(10), card.Top + S(4), timeWidth, S(17)), timeColour, t.Surface, RightFlags);
    }

    /// <summary>"Module.Engine.12.cpp" → name; anything in brackets or after " - " goes to the detail line.</summary>
    private static (string Name, string Detail) SplitDescription(string description)
    {
        string d = description.Trim();
        int dash = d.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
            return (d[..dash], d[(dash + 3)..]);
        int bracket = d.IndexOf(" (", StringComparison.Ordinal);
        if (bracket > 0 && d.EndsWith(')'))
            return (d[..bracket], d[(bracket + 2)..^1]);
        return (d, "");
    }

    private void DrawFailures(Graphics g, Fonts f, Theme t, BuildSnapshot s, PopupLayout layout, int pad, int width)
    {
        int top = layout.FailuresTop + S(14);
        string? more = s.FailureCount > layout.FailureCount ? $"showing {layout.FailureCount} of {s.FailureCount}" : null;
        DrawSectionLabel(g, f, t, $"ERRORS  {s.FailureCount}", more, pad, top, width, t.Danger);
        top += S(SectionLabelHeight);
        for (int i = 0; i < layout.FailureCount; i++)
        {
            FailedAction fail = s.Failures[i];
            var card = new Rectangle(pad, top + i * (S(FailureCardHeight) + S(CardGap)), width, S(FailureCardHeight));
            using (GraphicsPath path = IconFactory.RoundedRect(card, S(7)))
            using (var brush = new SolidBrush(t.DangerSurface))
                g.FillPath(brush, path);
            using (var stripe = new SolidBrush(t.Danger))
            using (GraphicsPath sp = IconFactory.RoundedRect(new RectangleF(card.Left, card.Top, S(4), card.Height), S(2)))
                g.FillPath(stripe, sp);

            int left = card.Left + S(12);
            TextRenderer.DrawText(g, fail.Description, f.BodyBold, new Rectangle(left, card.Top + S(5), card.Width - S(90), S(17)),
                t.Danger, t.DangerSurface, LeftFlags);
            TextRenderer.DrawText(g, $"exit {unchecked((int)fail.ExitCode)}", f.Small,
                new Rectangle(card.Right - S(80), card.Top + S(5), S(70), S(17)), t.FaintText, t.DangerSurface, RightFlags);
            TextRenderer.DrawText(g, fail.Message ?? (fail.Remote ? $"on {fail.Where}" : "no output"), f.Small,
                new Rectangle(left, card.Top + S(24), card.Width - S(22), S(15)), t.DimText, t.DangerSurface, LeftFlags);
        }
    }

    private string ChipText(HelperSession h)
    {
        string text = h.IsLocal ? $"{h.Name}" : h.Name;
        var parts = new List<string> { text };
        if (h.Disconnected)
            parts.Add("disconnected");
        else
        {
            parts.Add($"CPU {h.CpuLoad * 100:0}%");
            parts.Add($"{h.Active} running · {Format.Count(h.Done)} done");
            if (!h.IsLocal && (h.SendPerSecond > 0 || h.RecvPerSecond > 0))
                parts.Add($"↑{Format.Rate(h.SendPerSecond)} ↓{Format.Rate(h.RecvPerSecond)}");
        }
        return string.Join("  ·  ", parts);
    }

    private int ChipWidth(Font font, HelperSession h) =>
        TextRenderer.MeasureText(ChipText(h), font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + S(34);

    private int MeasureChipRows(int width)
    {
        _fonts ??= new Fonts(Dpi);
        int rows = 1, x = 0;
        foreach (HelperSession h in _snapshot.Sessions)
        {
            int w = Math.Min(width, ChipWidth(_fonts.Small, h));
            if (x > 0 && x + w > width)
            {
                rows++;
                x = 0;
            }
            x += w + S(CardGap);
        }
        return rows;
    }

    private void DrawMachines(Graphics g, Fonts f, Theme t, BuildSnapshot s, PopupLayout layout, int pad, int width)
    {
        int top = layout.MachinesTop + S(14);
        int remote = s.Sessions.Count(h => !h.IsLocal && !h.Disconnected);
        DrawSectionLabel(g, f, t, "MACHINES", remote > 0 ? $"{remote} helper{(remote == 1 ? "" : "s")}" : (s.RemoteDisabled ? "remote disabled" : "local only"),
            pad, top, width);
        top += S(SectionLabelHeight);

        int x = 0, y = top;
        foreach (HelperSession h in s.Sessions)
        {
            int w = Math.Min(width, ChipWidth(f.Small, h));
            if (x > 0 && x + w > width)
            {
                x = 0;
                y += S(ChipHeight) + S(CardGap);
            }
            var chip = new Rectangle(pad + x, y, w, S(ChipHeight));
            using (GraphicsPath path = IconFactory.RoundedRect(chip, chip.Height / 2f))
            using (var brush = new SolidBrush(t.Surface))
                g.FillPath(brush, path);

            // Status dot coloured by CPU load.
            Color dot = h.Disconnected ? t.FaintText
                : h.CpuLoad > 0.85f ? t.Success
                : h.CpuLoad > 0.4f ? t.Accent
                : t.FaintText;
            float d = S(8);
            using (var dotBrush = new SolidBrush(dot))
                g.FillEllipse(dotBrush, chip.Left + S(11), chip.Top + (chip.Height - d) / 2f, d, d);

            TextRenderer.DrawText(g, ChipText(h), f.Small, new Rectangle(chip.Left + S(25), chip.Top, chip.Width - S(32), chip.Height),
                h.Disconnected ? t.FaintText : t.DimText, t.Surface, LeftFlags);
            x += w + S(CardGap);
        }
    }

    // ---- Native ------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
}
