using UbaTrayTool.Trace;

namespace UbaTrayTool;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Asset / test helpers: no tray icon for these.
        if (args.Length == 2 && args[0] == "--export-icon")
        {
            IconFactory.SaveIco(args[1]);
            return;
        }
        if (args.Length == 3 && args[0] == "--dump")
        {
            using var monitor = new TraceMonitor("UbaTrayTool.Dump");
            monitor.OpenFile(args[1]);
            File.WriteAllText(args[2], monitor.Describe());
            return;
        }
        // --render trace.uba out.png [light|dark] [fraction]  (fraction < 1 previews a running build)
        if (args.Length >= 3 && args[0] == "--render")
        {
            using var monitor = new TraceMonitor("UbaTrayTool.Dump");
            double? fraction = args.Length >= 5 && double.TryParse(args[4], System.Globalization.CultureInfo.InvariantCulture, out double f) ? f : null;
            monitor.OpenFile(args[1], fraction);
            using var popup = new BuildPopup();
            if (args.Length >= 4)
                popup.ForceTheme(args[3] == "light");
            popup.SetSnapshot(monitor.Current);
            using Bitmap bmp = popup.RenderToBitmap();
            bmp.Save(args[2], System.Drawing.Imaging.ImageFormat.Png);
            return;
        }

        // --render-set trace.uba outDir : running (40%, 75%) and finished, dark and light.
        if (args.Length == 3 && args[0] == "--render-set")
        {
            Directory.CreateDirectory(args[2]);
            foreach ((string name, double? fraction) in new (string, double?)[] { ("running40", 0.4), ("running75", 0.75), ("done", null) })
            {
                foreach (bool light in new[] { false, true })
                {
                    using var monitor = new TraceMonitor("UbaTrayTool.Dump");
                    monitor.OpenFile(args[1], fraction);
                    using var popup = new BuildPopup();
                    popup.ForceTheme(light);
                    popup.SetSnapshot(monitor.Current);
                    using Bitmap bmp = popup.RenderToBitmap();
                    bmp.Save(Path.Combine(args[2], $"{name}-{(light ? "light" : "dark")}.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            foreach (BuildState state in Enum.GetValues<BuildState>())
            {
                using Bitmap icon = IconFactory.Render(64, state, 0.6);
                icon.Save(Path.Combine(args[2], $"icon-{state}.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            return;
        }

        // --render-demo outDir : made-up busy build (every lane full, helpers, failures) to check the layout.
        if (args.Length == 2 && args[0] == "--render-demo")
        {
            Directory.CreateDirectory(args[1]);
            foreach (int running in new[] { Environment.ProcessorCount, 12, 0 })
            {
                foreach (bool light in new[] { false, true })
                {
                    using var popup = new BuildPopup();
                    popup.ForceTheme(light);
                    popup.SetSnapshot(DemoData.Snapshot(running));
                    using Bitmap bmp = popup.RenderToBitmap();
                    bmp.Save(Path.Combine(args[1], $"demo{running}-{(light ? "light" : "dark")}.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            return;
        }

        string? openFile = args.Length == 1 && File.Exists(args[0]) ? args[0] : null;

        using var mutex = new Mutex(true, @"Local\UbaTrayTool.SingleInstance", out bool isFirst);
        if (!isFirst)
            return;

        Application.Run(new TrayContext(openFile));
    }
}
