// ReShade.ini and amd-nr.ini, edited in place: every byte nobody asked to change stays as it was.

using System.Text;

namespace AmdNr.Core;

public static partial class Engine
{
    // -- INI -----------------------------------------------------------------------------------
    // Preserve all unedited INI bytes, including comments and unrelated preferences.

    public static string GetIni(string s, string section, string key)
    {
        var sec = string.Empty;
        foreach (var line in s.Split('\n'))
        {
            var t = Trim(line);
            if (t.Length > 1 && t[0] == '[' && t[^1] == ']')
            {
                sec = Lower(t[1..^1]);
            }
            else if (sec == Lower(section))
            {
                var eq = t.IndexOf('=');
                if (eq >= 0 && Lower(Trim(t[..eq])) == Lower(key)) return t[(eq + 1)..];
            }
        }
        return string.Empty;
    }

    public static string SetIni(string s, string section, string key, string value)
    {
        var nl = s.Contains("\r\n") ? "\r\n" : "\n";
        var outText = s;
        var sec = string.Empty;
        var start = 0;
        int? insert = null;
        var found = false;

        while (start < outText.Length)
        {
            var newline = outText.IndexOf('\n', start);
            var end = newline >= 0 ? newline + 1 : outText.Length;
            var t = Trim(outText[start..end]);

            if (t.Length > 1 && t[0] == '[' && t[^1] == ']')
            {
                if (found && sec == Lower(section))
                {
                    insert = start;
                    break;
                }
                sec = Lower(t[1..^1]);
                if (sec == Lower(section))
                {
                    found = true;
                    insert = end;
                }
            }
            else if (sec == Lower(section))
            {
                var eq = t.IndexOf('=');
                if (eq >= 0 && Lower(Trim(t[..eq])) == Lower(key))
                    return string.Concat(outText.AsSpan(0, start), $"{key}={value}{nl}", outText.AsSpan(end));
                insert = end;
            }
            start = end;
        }

        if (found)
        {
            var at = insert ?? outText.Length;
            var lead = at > 0 && outText[at - 1] != '\n' ? nl : string.Empty;
            return outText.Insert(at, $"{lead}{key}={value}{nl}");
        }

        if (outText.Length > 0 && !outText.EndsWith('\n')) outText += nl;
        return $"{outText}{nl}[{section}]{nl}{key}={value}{nl}";
    }

    public static string FreshIni() =>
    // Skin=-1 is the engine's own "automatic": derive skin structure from local structure.
    // Writing 1 switched that off before the panel was ever opened, which is the bug the
    // add-on shipped and fixed in v0.6.5; a fresh 32-bit install was putting it back.
        "[amd-nr]\r\n; x86 fresh-install overrides. All other values follow upstream defaults.\r\nScale=1.0\r\nColourStrength=0.25\r\nStructure=1\r\nSkin=-1\r\nPasses=1\r\n";

    private static string? DockIdIn(string chunk)
    {
        var at = chunk.IndexOf("DockId=0x", StringComparison.Ordinal);
        if (at < 0) return null;
        var rest = chunk[(at + "DockId=".Length)..];
        var end = rest.Length;
        for (var i = 2; i < rest.Length; i++)
        {
            if (!Uri.IsHexDigit(rest[i]))
            {
                end = i;
                break;
            }
        }
        return rest[..end];
    }

    /// <summary>The window titles the add-ons register with reshade::register_overlay. ImGui keys
    /// the saved layout by title, so docking under the wrong one leaves the real panel floating.</summary>
    public const string PanelTitle = "AMD Neural Rendering";
    public const string PanelTitle32 = "AMD Neural Rendering (32-bit)";

    /// <summary>Dock the panel once, on a fresh layout only. A saved layout is authoritative even
    /// when the user undocked the panel, so this never rebuilds or guesses a target.</summary>
    public static string FirstDock(string ini, uint width, uint height, string title = PanelTitle)
    {
        var windows = GetIni(ini, "OVERLAY", "Window");
        var panel = $"[Window][{title}]";
        if (windows.Contains(panel, StringComparison.Ordinal)) return ini;

        var dock = string.Empty;
        var home = windows.IndexOf("[Window][###home]", StringComparison.Ordinal);
        if (home >= 0)
        {
            var next = windows.IndexOf("[Window]", home + 1, StringComparison.Ordinal);
            var chunk = next >= 0 ? windows[home..next] : windows[home..];
            dock = DockIdIn(chunk) ?? string.Empty;
        }

        if (dock.Length == 0)
        {
            // Respect an existing layout without a docked Home. No destructive rebuild, no guessed target.
            if (windows.Length > 0 || GetIni(ini, "OVERLAY", "Docking").Length > 0) return ini;
            Require(width >= 320 && height >= 240, "Viewport size unavailable for fresh docking");

            var left = width * 35 / 100;
            var layout =
                $"[Docking][Data],DockSpace   ID=0xB0DF600F Pos=0,,0 Size={width},,{height} Split=X,  DockNode  ID=0x00000001 Parent=0xB0DF600F SizeRef={left},,{height},  DockNode  ID=0x00000002 Parent=0xB0DF600F SizeRef={width - left},,{height} CentralNode=1";
            ini = SetIni(ini, "OVERLAY", "Docking", layout);
            dock = "0x00000001";

            var tabs = new[] { "###home", "###addons", "###settings", "###statistics", "###log", "###about" };
            var builder = new StringBuilder(windows);
            for (var tab = 0; tab < tabs.Length; tab++)
            {
                if (builder.Length > 0) builder.Append(',');
                builder.Append($"[Window][{tabs[tab]}],Collapsed=0,DockId={dock},,{tab}");
            }
            windows = builder.ToString();
        }

        if (windows.Length > 0) windows += ",";
        windows += $"{panel},Collapsed=0,DockId={dock}";
        return SetIni(ini, "OVERLAY", "Window", windows);
    }
}
