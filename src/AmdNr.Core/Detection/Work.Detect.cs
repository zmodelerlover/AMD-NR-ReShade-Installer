// Which side of the bridge a game belongs to, read off the PE headers of its executables.

namespace AmdNr.Core;

public static partial class Work
{
    // -- Detection -------------------------------------------------------------------------------

    private static Route? RouteOf(ushort? machine) => machine switch
    {
        Engine.MachineX86 => Route.X86,
        Engine.MachineX64 => Route.X64,
        _ => null,
    };

    /// <summary>Read the target -- an executable, or the executables sitting in a folder -- and
    /// decide.</summary>
    public static Detected Detect(string target)
    {
        var path = ResolveTarget(target);
        if (path.Length == 0) return Detected.Unknown;

        if (File.Exists(path))
        {
            return RouteOf(Engine.MachineOfFile(path)) switch
            {
                Route.X86 => Detected.On(Route.X86, $"{NameOf(path)} is a 32-bit executable, so this is the bridge route."),
                Route.X64 => Detected.On(Route.X64, $"{NameOf(path)} is a 64-bit executable."),
                _ => Detected.Unknown,
            };
        }
        if (!Directory.Exists(path)) return Detected.Unknown;

        var x86 = new List<string>();
        var x64 = new List<string>();
        try
        {
            foreach (var p in Directory.EnumerateFiles(path, "*.exe"))
            {
                switch (RouteOf(Engine.MachineOfFile(p)))
                {
                    case Route.X86: x86.Add(NameOf(p)); break;
                    case Route.X64: x64.Add(NameOf(p)); break;
                }
            }
        }
        catch
        {
            return Detected.Unknown;
        }

        return (x86.Count > 0, x64.Count > 0) switch
        {
            (false, false) => Detected.Unknown,
            (true, false) => Detected.On(Route.X86,
                $"{Joined(x86)} here {(x86.Count == 1 ? "is" : "are")} 32-bit, so this is the bridge route."),
            (false, true) => Detected.On(Route.X64,
                $"{Joined(x64)} here {(x64.Count == 1 ? "is" : "are")} 64-bit."),
            (true, true) => Detected.Mixed(
                $"Both widths are here: {Joined(x86)} is 32-bit and {Joined(x64)} is 64-bit. A 32-bit "
                + "launcher beside a 64-bit game is normal -- pick the one the game actually runs as."),
        };
    }
}
