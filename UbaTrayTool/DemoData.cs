using UbaTrayTool.Trace;

namespace UbaTrayTool;

/// <summary>Made-up build used only by --render-demo to check the popup layout.</summary>
internal static class DemoData
{
    private static readonly string[] Modules =
    [
        "Engine", "CoreUObject", "Renderer", "Slate", "UMG", "Niagara", "Chaos", "AIModule", "Landscape", "MechAnt",
        "Navigation", "AnimGraph", "MovieScene", "GameplayAbilities", "PhysicsCore", "RHI", "MaterialEditor", "UnrealEd",
    ];

    public static BuildSnapshot Snapshot(int running)
    {
        var rng = new Random(7);
        var actions = new List<RunningAction>();
        for (int i = 0; i < running; i++)
        {
            string module = Modules[rng.Next(Modules.Length)];
            int kind = rng.Next(20);
            string desc = kind switch
            {
                0 => $"UnrealEditor-{module}.dll (Link [x64])",
                1 => $"SharedPCH.{module}.Project.ValApi.Cpp20.cpp (Compile [x64])",
                2 => $"{module}.ispc (ISPC [x64])",
                _ => $"Module.{module}.{rng.Next(1, 40)}.cpp (Compile [x64])",
            };
            bool remote = i % 5 == 4;
            actions.Add(new RunningAction((uint)i, desc, rng.NextDouble() * (i == 3 ? 95 : 25), remote,
                remote ? (i % 2 == 0 ? "AMD9800X3D" : "BUILD-02") : "AMD7970X", false));
        }

        bool done = running == 0;
        return new BuildSnapshot
        {
            State = done ? BuildState.Failed : BuildState.Running,
            IsLive = true,
            TraceName = "UbaTrayTool demo",
            StartUtc = DateTimeOffset.UtcNow.AddMinutes(-3),
            Elapsed = TimeSpan.FromSeconds(done ? 412 : 187),
            Total = 2140,
            Done = done ? 2140u : 1312u,
            Errors = 2,
            Running = running,
            RunningRemote = actions.Count(a => a.Remote),
            Finished = 1312,
            MaxActive = Environment.ProcessorCount + 14,
            CacheHits = 384,
            RunningActions = actions,
            FailureCount = 2,
            Failures =
            [
                new FailedAction("Module.MechAnt.3.cpp (Compile [x64])", 2,
                    "AntColonyComponent.cpp(212): error C2065: 'NestDepth': undeclared identifier", false, "AMD7970X"),
                new FailedAction("Module.MechAnt.7.cpp (Compile [x64])", 2,
                    "PheromoneGrid.h(48): error C2143: syntax error: missing ';' before '}'", true, "AMD9800X3D"),
            ],
            Sessions =
            [
                new HelperSession("AMD7970X", "CPU:64 @ 4.0GHz Mem:256gb", true, false, actions.Count(a => !a.Remote), 1100, 64, done ? 0.04f : 0.97f, 0, 0, 0, 0, null),
                new HelperSession("AMD9800X3D", "CPU:16", false, false, done ? 0 : 8, 160, 16, done ? 0.02f : 0.92f, 0, 0, 2.4e6, 9.1e6, null),
                new HelperSession("BUILD-02", "CPU:32", false, done, done ? 0 : 6, 52, 32, done ? 0 : 0.61f, 0, 0, 1.1e6, 3.0e6, null),
            ],
            Concurrency = Enumerable.Range(0, 120).Select(i =>
                (float)Math.Max(0, Math.Min(Environment.ProcessorCount + 14, (Environment.ProcessorCount + 14) * Math.Sin(Math.PI * Math.Min(1, i / (done ? 120.0 : 70.0))) + rng.Next(-6, 6)))).ToArray(),
        };
    }
}
