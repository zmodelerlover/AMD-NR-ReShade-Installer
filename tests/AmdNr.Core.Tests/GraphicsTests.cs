using AmdNr.Core;

namespace AmdNr.Core.Tests;

public class GraphicsTests
{
    // -- Import tables ---------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ImportsAreReadFromBothTablesInBothWidths(bool x64)
    {
        var path = Path.Combine(Fixture.Temp("imports"), "game.exe");
        File.WriteAllBytes(path, Fixture.PeWithImports(x64, ["KERNEL32.dll", "d3d11.dll"], ["d3d12.dll"]));

        var imports = PeImports.Read(path);
        Assert.Contains("KERNEL32.dll", imports);
        Assert.Contains("d3d11.dll", imports);
        Assert.Contains("d3d12.dll", imports); // delay-loaded, and still counted
        // The image stays a valid PE for the width check the installer already does.
        Assert.Equal(x64 ? Engine.MachineX64 : Engine.MachineX86, Engine.MachineOfFile(path));
    }

    [Fact]
    public void AFileThatIsNotAnImageHasNoImportsRatherThanAnException()
    {
        var path = Path.Combine(Fixture.Temp("not-pe"), "readme.exe");
        File.WriteAllText(path, "not an executable at all");
        Assert.Empty(PeImports.Read(path));
    }

    // -- Deciding --------------------------------------------------------------------------------

    private static string Game(string tag, string exe, byte[] image)
    {
        var root = Fixture.Temp(tag);
        var path = Path.Combine(root, exe);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, image);
        return root;
    }

    [Fact]
    public void AD3D11GameIsNamedByItsImport()
    {
        var root = Game("d3d11", "Game.exe", Fixture.PeWithImports(true, ["d3d11.dll", "dxgi.dll"]));
        var d = GraphicsDetector.Detect(root);
        Assert.Equal(GraphicsApi.D3D11, d.Api);
        Assert.Equal(Preset.Dx11, d.Preset);
        Assert.Contains("d3d11.dll", d.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOld32BitGameGetsTheBridgeRouteForItsApi()
    {
        var root = Game("d3d9", "OldGame.exe", Fixture.PeWithImports(false, ["d3d9.dll"]));
        var d = GraphicsDetector.Detect(root);
        Assert.Equal(Route.X86, d.Width);
        Assert.Equal(Preset.X86Dx9, d.Preset);
        Assert.Equal("DX9 · 32-bit", d.Tag);
    }

    /// <summary>A D3D12 renderer that also imports D3D11 for video or UI is still a D3D12 game. Only
    /// Unreal genuinely ships both renderers.</summary>
    [Fact]
    public void BothD3DImportsMeanBothRenderersOnlyInUnreal()
    {
        var plain = Game("interop", "Game.exe", Fixture.PeWithImports(true, ["d3d12.dll", "d3d11.dll"]));
        var d = GraphicsDetector.Detect(plain);
        Assert.Equal(Preset.Dx12, d.Preset);
        Assert.False(d.AlsoD3D11);

        var unreal = Game("unreal", @"Project\Binaries\Win64\Project-Win64-Shipping.exe",
            Fixture.PeWithImports(true, ["d3d12.dll", "d3d11.dll"]));
        // The root holds only the stub launcher, which imports no renderer at all.
        File.WriteAllBytes(Path.Combine(unreal, "Project.exe"), Fixture.PeWithImports(true, ["KERNEL32.dll"]));

        var ue = GraphicsDetector.Detect(unreal);
        Assert.EndsWith("-Win64-Shipping.exe", ue.Executable, StringComparison.Ordinal);
        Assert.True(ue.AlsoD3D11);
        // Both are offered, and D3D11 is where depth and motion reach the network.
        Assert.Equal(Preset.Dx11, ue.Preset);
        Assert.True(ue.NeedsRendererSwitch);
    }

    /// <summary>Unity loads its renderer dynamically; the player executable shows only an OpenGL
    /// fallback import. Reporting that game as OpenGL -- with no route -- is the wrong answer.</summary>
    [Fact]
    public void AUnityPlayerIsNotMistakenForAnOpenGLGame()
    {
        var root = Game("unity", "MyGame.exe", Fixture.PeWithImports(true, ["opengl32.dll", "KERNEL32.dll"]));
        Directory.CreateDirectory(Path.Combine(root, "MyGame_Data"));

        var d = GraphicsDetector.Detect(root);
        Assert.Equal(GraphicsApi.D3D11, d.Api);
        Assert.Contains("Unity", d.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void AGenuineOpenGLGameHasNoRouteAndSaysSo()
    {
        var root = Game("opengl", "hl.exe", Fixture.PeWithImports(false, ["opengl32.dll"]));
        var d = GraphicsDetector.Detect(root);
        Assert.Equal(GraphicsApi.OpenGL, d.Api);
        Assert.Null(d.Preset);
    }

    [Fact]
    public void TheAgilitySdkNamesD3D12WhenTheImportsAreHidden()
    {
        // A packed executable with no readable imports, beside the D3D12 Agility SDK.
        var root = Game("agility", "Packed.exe", Fixture.Pe(true));
        Directory.CreateDirectory(Path.Combine(root, "D3D12"));
        File.WriteAllBytes(Path.Combine(root, "D3D12", "D3D12Core.dll"), [0]);

        Assert.Equal(GraphicsApi.D3D12, GraphicsDetector.Detect(root).Api);
    }

    // -- Picking the executable --------------------------------------------------------------------

    [Fact]
    public void TheLanguagePickerBesideTheGameIsNotTheGame()
    {
        var root = Fixture.Temp("gtav");
        var big = Fixture.PeWithImports(true, ["d3d11.dll"]).Concat(new byte[4096]).ToArray();
        File.WriteAllBytes(Path.Combine(root, "GTAVLanguageSelect.exe"), Fixture.PeWithImports(true, ["USER32.dll"]));
        File.WriteAllBytes(Path.Combine(root, "GTA5.exe"), big);

        // "V" in the store name, "5" in the file name.
        Assert.Equal("GTA5.exe", Path.GetFileName(GraphicsDetector.FindExecutable(root, "Grand Theft Auto V")));
    }

    [Fact]
    public void CrashReportersAndInstallersAreNeverPicked()
    {
        var root = Fixture.Temp("noise");
        var huge = Fixture.Pe(true).Concat(new byte[64_000]).ToArray();
        File.WriteAllBytes(Path.Combine(root, "UnityCrashHandler64.exe"), huge);
        File.WriteAllBytes(Path.Combine(root, "unins000.exe"), huge);
        File.WriteAllBytes(Path.Combine(root, "Shooter.exe"), Fixture.Pe(true));

        Assert.Equal("Shooter.exe", Path.GetFileName(GraphicsDetector.FindExecutable(root)));
    }

    [Fact]
    public void AFolderWhereEveryExecutableLooksLikeATool_StillAnswers()
    {
        var root = Fixture.Temp("all-tools");
        File.WriteAllBytes(Path.Combine(root, "Launcher.exe"), Fixture.Pe(true));
        Assert.Equal("Launcher.exe", Path.GetFileName(GraphicsDetector.FindExecutable(root)));
    }

    // -- PCGamingWiki template --------------------------------------------------------------------

    private const string Metro2033 = """
        {{API
        |direct3d versions      = 9.0c, 11
        |direct3d notes         = The DX10 option is d3d11 feature level 10_1<ref>{{Refcheck|user=x|date=2025}}</ref>
        |opengl versions        =
        |opengl notes           =
        |vulkan versions        =
        |vulkan notes           =
        |windows 32-bit exe     = true
        |windows 64-bit exe     = false
        }}
        """;

    /// <summary>The regression that made Metro 2033 read as supporting Vulkan and OpenGL: an empty
    /// field swallowing the line after it.</summary>
    [Fact]
    public void AnEmptyFieldDoesNotSwallowTheNextLine()
    {
        var api = PcgwParser.ParseApiTemplate("Metro_2033", Metro2033)!;
        Assert.Equal([GraphicsApi.D3D9, GraphicsApi.D3D11], api.Supported);
        Assert.True(api.Has32Bit);
        Assert.False(api.Has64Bit);
        Assert.Equal("Metro 2033", api.Page);
    }

    [Fact]
    public void VersionsAndFlagsAreReadFromFreeText()
    {
        var api = PcgwParser.ParseApiTemplate("X", """
            {{API
            |direct3d versions      = 10, 11, 12
            |vulkan versions        = 1.3
            |opengl versions        = false
            |windows 64-bit exe     = true
            }}
            """)!;
        // D3D10 has no route here and is left out rather than mapped onto something it is not.
        Assert.Equal([GraphicsApi.D3D11, GraphicsApi.D3D12, GraphicsApi.Vulkan], api.Supported);
        Assert.Null(api.Has32Bit);
    }

    [Fact]
    public void APageWithoutAnApiSectionIsNothing() =>
        Assert.Null(PcgwParser.ParseApiTemplate("X", "{{Infobox game}}\n==Video==\n"));

    [Theory]
    [InlineData("Grand Theft Auto IV: The Complete Edition", "Grand Theft Auto IV")]
    [InlineData("Witcher 3: Wild Hunt, The", "The Witcher 3: Wild Hunt")]
    [InlineData("Need for Speed™", "Need for Speed")]
    [InlineData("The Elder Scrolls V: Skyrim Special Edition", "The Elder Scrolls V: Skyrim")]
    public void StoreTitlesMatchWikiTitles(string store, string wiki) =>
        Assert.Equal(PcgwParser.NormaliseTitle(wiki), PcgwParser.NormaliseTitle(store));

    [Fact]
    public void ANearMissTitleIsNotAMatch() =>
        Assert.Null(PcgwParser.BestTitle("Fortnite", ["Fortnite Battle Royale", "Fortnite Save the World"]));

    /// <summary>A remaster is a different game: same name, different engine, different API, often a
    /// different width. Treating it as an edition made one record answer for both.</summary>
    [Fact]
    public void ARemasterIsNotAnEditionOfTheGameItRemade() =>
        Assert.NotEqual(PcgwParser.NormaliseTitle("The Elder Scrolls IV: Oblivion"),
                        PcgwParser.NormaliseTitle("The Elder Scrolls IV: Oblivion Remastered"));

    // -- The shipped database -----------------------------------------------------------------------

    private static ApiDatabase SampleDb()
    {
        var db = new ApiDatabase();
        db.Put(ApiDatabase.SteamKey("1262540"), new ApiRecord { Title = "Need for Speed (2016)", Apis = ["D3D11"], Has64Bit = true });
        db.Put(ApiDatabase.SteamKey("43110"), new ApiRecord { Title = "Metro 2033", Apis = ["D3D9", "D3D11"], Has32Bit = true });
        return db;
    }

    [Fact]
    public void TheDatabaseAnswersByAppIdAndByTitle()
    {
        var db = ApiDatabase.Parse(SampleDb().Serialise());

        Assert.Equal([GraphicsApi.D3D11], db.Lookup("1262540", "whatever")!.Supported);
        // The same game bought somewhere with no Steam app id finds the record by title.
        Assert.Equal([GraphicsApi.D3D9, GraphicsApi.D3D11], db.Lookup(null, "Metro 2033")!.Supported);
        Assert.Null(db.Lookup(null, "Metro Exodus"));
    }

    /// <summary>The same game reached through two app ids is one answer, and is still answered.</summary>
    [Fact]
    public void ATitleThatTwoRecordsAgreeOnIsStillAnswered()
    {
        var db = new ApiDatabase();
        db.Put(ApiDatabase.SteamKey("22330"),
            new ApiRecord { Title = "The Elder Scrolls IV: Oblivion", Apis = ["D3D9"], Has32Bit = true });
        db.Put(ApiDatabase.SteamKey("900883"),
            new ApiRecord { Title = "The Elder Scrolls IV: Oblivion", Apis = ["D3D9"], Has32Bit = true });

        Assert.Equal([GraphicsApi.D3D9], db.Lookup(null, "The Elder Scrolls IV: Oblivion")!.Supported);
    }

    /// <summary>Two different games under one normalised title answer nothing, so the detection
    /// falls back to the executable instead of reporting the other game's API with confidence.
    /// </summary>
    [Fact]
    public void ATitleTwoGamesDisagreeOnAnswersNothing()
    {
        var db = new ApiDatabase();
        db.Put(ApiDatabase.SteamKey("1"), new ApiRecord { Title = "Some Game", Apis = ["D3D12"], Has64Bit = true });
        db.Put(ApiDatabase.SteamKey("2"), new ApiRecord { Title = "Some Game", Apis = ["D3D9"], Has32Bit = true });

        Assert.Null(db.Lookup(null, "Some Game"));
        // By app id it is still exact, and still answered.
        Assert.Equal([GraphicsApi.D3D9], db.Lookup("2", "Some Game")!.Supported);
    }

    /// <summary>The Frostbite case: the executable links d3d12.dll, the game renders D3D11. What the
    /// wiki says about which APIs exist wins; what the file says about its width still stands.</summary>
    [Fact]
    public void WhatTheWikiSaysReplacesAMisleadingImport()
    {
        var root = Game("frostbite", "NFS16.exe", Fixture.PeWithImports(true, ["d3d12.dll"]));
        var local = GraphicsDetector.Detect(root, "Need for Speed");
        Assert.Equal(Preset.Dx12, local.Preset);

        var merged = local.With(SampleDb().Lookup("1262540", null));
        Assert.Equal(Preset.Dx11, merged.Preset);
        Assert.Equal(Route.X64, merged.Width);
        Assert.Equal("PCGamingWiki", merged.Source);
        Assert.Equal("DX11", merged.Tag);
    }

    [Fact]
    public void ADatabaseFromTheFutureIsRefused() =>
        Assert.Throws<InstallException>(() => ApiDatabase.Parse("{\"schema\":2,\"games\":{}}"));

    /// <summary>Source keeps a launcher stub in the root and its renderer in bin\. Source loads that
    /// folder's modules with LOAD_WITH_ALTERED_SEARCH_PATH, so shaderapidx9.dll resolves its own
    /// d3d9.dll against bin\ and never looks at the root -- a ReShade installed in the root is never
    /// loaded at all, and one in bin\ searches bin\ for add-ons. Proven on Half-Life 2: ReShade.log
    /// appeared only once ReShade was in bin\, and it logged
    /// "Searching for add-ons ... in '...\Half-Life 2\bin'".</summary>
    [Fact]
    public void ASourceGameIsInstalledIntoTheFolderItsRendererLoadsFrom()
    {
        var root = Fixture.Temp("source-game");
        File.WriteAllBytes(Path.Combine(root, "hl2.exe"), Fixture.Pe(false));
        var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        File.WriteAllBytes(Path.Combine(bin, "shaderapidx9.dll"), Fixture.PeWithImports(false, ["d3d9.dll"]));

        var d = GraphicsDetector.Detect(root, "Half-Life 2");
        Assert.Equal(Path.Combine(root, "hl2.exe"), d.Executable);
        Assert.Equal(Path.Combine(bin, "shaderapidx9.dll"), d.Target);
        Assert.Equal(Route.X86, d.Width);
        Assert.Equal(Preset.X86Dx9, d.Preset);
        Assert.Contains("bin", d.Why, StringComparison.Ordinal);

        // The 64-bit layout wins over the 32-bit one when both are there.
        var bin64 = Directory.CreateDirectory(Path.Combine(bin, "x64")).FullName;
        File.WriteAllBytes(Path.Combine(bin64, "shaderapidx9.dll"), Fixture.PeWithImports(true, ["d3d9.dll"]));
        var wide = GraphicsDetector.Detect(root, "Half-Life 2");
        Assert.Equal(Path.Combine(bin64, "shaderapidx9.dll"), wide.Target);
        Assert.Equal(Route.X64, wide.Width);

        // The database can say which APIs exist; it cannot know the layout, so the sentence that
        // says which folder the files go in has to survive being merged with it.
        var wiki = d.With(new PcgwApi("Half-Life 2", [GraphicsApi.Vulkan, GraphicsApi.D3D9, GraphicsApi.OpenGL], true, false));
        Assert.Equal(Path.Combine(bin, "shaderapidx9.dll"), wiki.Target);
        Assert.Contains("bin", wiki.Why, StringComparison.Ordinal);
        Assert.Contains("PCGamingWiki", wiki.Why, StringComparison.Ordinal);
    }

    /// <summary>A game whose renderer is in its own folder keeps pointing at its executable, so the
    /// Source rule cannot move an ordinary install somewhere else.</summary>
    [Fact]
    public void AnOrdinaryGameStillInstallsBesideItsExecutable()
    {
        var root = Fixture.Temp("plain-target");
        File.WriteAllBytes(Path.Combine(root, "Game.exe"), Fixture.PeWithImports(true, ["d3d11.dll"]));

        var d = GraphicsDetector.Detect(root, "Game");
        Assert.Equal(Path.Combine(root, "Game.exe"), d.Executable);
        Assert.Equal(d.Executable, d.Target);
        Assert.Null(d.InstallTarget);
    }
}
