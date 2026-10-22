// Headless-safe tests for CK3xBL.Ux: nothing is ever shown on screen, no input is sent, no focus is changed and no volume is
// touched. Card renders are written to %LOCALAPPDATA%\ck3xbl\selftest\ for a human look.
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text.Json;
using CK3xBL.Core.Abstractions;
using CK3xBL.Ux;
using CK3xBL.Ux.Audio;
using CK3xBL.Ux.Interop;
using CK3xBL.Ux.Overlay;
using CK3xBL.Ux.Rendering;

namespace CK3xBL.SelfTest;

internal static class UxTests
{
    private static readonly Lazy<CardFonts> Fonts = new(() => CardFonts.Load("Fondamento", null));

    public static string OutputDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ck3xbl", "selftest");

    private static readonly TitleCard TitleSample = new()
    {
        Title = "The Battle of Orangallu",
        Subtitle = "Gunda Kakatiya against the Raiders of Hepung · Summer, 870",
        Detail = "Hills near Orangallu",
    };

    private static readonly TitleCard VictorySample = new()
    {
        Title = "Victory at Orangallu",
        Subtitle = "Our losses 312 · Enemy losses 1,204",
        Detail = "Sir Nachana fell in the fighting\nYou cut down 7 foes with your own hand",
    };

    private static readonly TitleCard DefeatSample = new()
    {
        Title = "Defeat at Orangallu",
        Subtitle = "Our losses 1,140 · Enemy losses 388",
        Detail = "The host of Gunda Kakatiya is broken\nSir Nachana was taken captive",
    };

    public static void Register(TestRegistry r)
    {
        // UX diagnostics go to the console with CK3XBL_UX_VERBOSE=1; otherwise they are dropped (never into the user's ux.log).
        bool verbose = Environment.GetEnvironmentVariable("CK3XBL_UX_VERBOSE") == "1";
        UxLog.Sink = line => { if (verbose) Console.WriteLine("      " + line); };

        r.Add("ux.font_discovery", FontDiscovery);
        r.Add("ux.fonts.memory_loaded_without_file_lock", FontsMemoryLoaded);
        r.Add("ux.fonts.load_never_throws", FontsLoadNeverThrows);
        r.Add("ux.log.fallback_file", LogFallbackFile);
        r.Add("ux.result_kind", ResultKind);
        foreach (var (w, h) in new[] { (2560, 1440), (1920, 1080) })
        {
            r.Add($"ux.card_png.title_{w}x{h}", () => RenderAndCheck("title", TitleSample, CardKind.Title, w, h, withUnderlay: true));
            r.Add($"ux.card_png.victory_{w}x{h}", () => RenderAndCheck("victory", VictorySample, CardKind.Victory, w, h, withUnderlay: false));
            r.Add($"ux.card_png.defeat_{w}x{h}", () => RenderAndCheck("defeat", DefeatSample, CardKind.Defeat, w, h, withUnderlay: false));
        }
        r.Add("ux.card_png.long_title_wraps", LongTitleWraps);
        r.Add("ux.card_png.transition_preview", TransitionPreview);
        r.Add("ux.card_png.over_live_ck3", OverLiveCk3);
        r.Add("ux.audio.list_sessions_readonly", AudioListReadOnly);
        r.Add("ux.audio.curve", AudioCurve);
        r.Add("ux.audio.store_keeps_pending_entries", AudioStorePending);
        r.Add("ux.audio.unrestorable_levels_go_pending", AudioUnrestorableLevelsGoPending);
        r.Add("ux.overlay.hidden_window_and_60fps_clock", OverlayHiddenWindowClock);
        r.Add("ux.overlay.state_machine_hidden", OverlayStateMachine);
        r.Add("ux.transition_ux.flow_hidden", TransitionFlowHidden);
        r.Add("ux.transition_ux.no_card_over_foreign_app", TransitionNoCardOverForeignApp);
        r.Add("ux.transition_ux.game_process_registry", GameProcessRegistry);
        r.Add("ux.transition_ux.reveal_holds_then_dissolves", RevealHoldsThenDissolves);
        r.Add("ux.transition_ux.reveal_cancellation", RevealCancellation);
        r.Add("ux.transition_ux.reveal_without_hold_is_hide", RevealWithoutHoldIsHide);
        r.Add("ux.transition_ux.title_card_presented", TitleCardPresented);
        r.Add("ux.window_mode_guard.classification", WindowModeClassification);
        r.Add("ux.window_mode_guard.policy", WindowModePolicy);
        r.Add("ux.window_mode_guard.win32_probe_hidden_window", WindowModeWin32Probe);
        r.Add("ux.foreground.escalation_policy", ForegroundEscalationPolicy);
        r.Add("ux.stager.borderless_roundtrip_hidden_window", StagerRoundTrip);
        r.Add("ux.input_safety.readonly", InputSafetyReadOnly);
        r.Add("ux.input_guard.fallback_and_overlapping_blocks", InputGuardFallbackAndBlocks);
        r.Add("ux.game_windows.readonly", GameWindowsReadOnly);
        r.Add("ux.transition_ux.construct_dispose", () => { using var ux = new TransitionUx(); });

        // On-screen tests for the main agent (they show the overlay over CK3 and ramp CK3's volume down and back).
        // Opt-in only: set CK3XBL_UX_LIVE=1 and run with the filter "ux.live".
        if (Environment.GetEnvironmentVariable("CK3XBL_UX_LIVE") == "1")
        {
            r.Add("ux.live.title_card_over_ck3", LiveTitleCard);
            r.Add("ux.live.result_card", LiveResultCard);
            r.Add("ux.live.audio_ramp_ck3", LiveAudioRamp);
        }
    }

    // ------------------------------------------------------------------------------------------------ live (opt-in, on screen)

    private static async Task LiveTitleCard()
    {
        nint ck3 = GameWindows.FindByProcessName("ck3", GameWindows.Ck3WindowClass);
        Expect.True(ck3 != 0, "CK3 must be running");
        await WaitForCk3InFrontAsync(ck3);
        using var ux = LiveUx();
        nint foregroundBefore = Native.GetForegroundWindow();
        var card = new TitleCard { Title = TitleSample.Title, Subtitle = TitleSample.Subtitle, Detail = TitleSample.Detail, FreezeFrameFrom = ck3 };
        var (left, right) = EnsureSampleCoas();
        card.LeftCoaPng = left; card.RightCoaPng = right;

        var sw = Stopwatch.StartNew();
        await ux.ShowTitleCardAsync(card, CancellationToken.None);
        Console.WriteLine($"      title card opaque after {sw.ElapsedMilliseconds} ms (budget: ~1.2 s incl. 0.6-1.0 s of fades)");
        Expect.Equal(foregroundBefore, Native.GetForegroundWindow(), "the overlay never takes the foreground");
        await Task.Delay(1500);
        await ux.ShowHintAsync("Click to continue to the battle", CancellationToken.None);
        await Task.Delay(1500);
        sw.Restart();
        await ux.HideAsync(CancellationToken.None);
        Console.WriteLine($"      hidden after {sw.ElapsedMilliseconds} ms");
        Expect.False(Native.IsTopmost(ck3), "CK3 is not left TOPMOST");
        Expect.Equal(foregroundBefore, Native.GetForegroundWindow(), "foreground unchanged at the end");
    }

    private static async Task LiveResultCard()
    {
        nint ck3 = GameWindows.FindByProcessName("ck3", GameWindows.Ck3WindowClass);
        Expect.True(ck3 != 0, "CK3 must be running");
        await WaitForCk3InFrontAsync(ck3);
        using var ux = LiveUx();
        nint foregroundBefore = Native.GetForegroundWindow();
        var (left, right) = EnsureSampleCoas();
        var card = new TitleCard { Title = VictorySample.Title, Subtitle = VictorySample.Subtitle, Detail = VictorySample.Detail, LeftCoaPng = left, RightCoaPng = right };
        var sw = Stopwatch.StartNew();
        await ux.ShowResultCardAsync(card, CancellationToken.None);
        Console.WriteLine($"      result card opaque after {sw.ElapsedMilliseconds} ms");
        await Task.Delay(2000);
        await ux.HideAsync(CancellationToken.None);
        Expect.Equal(foregroundBefore, Native.GetForegroundWindow(), "foreground unchanged");
    }

    private static async Task LiveAudioRamp()
    {
        var ck3 = System.Diagnostics.Process.GetProcessesByName("ck3").FirstOrDefault();
        Expect.True(ck3 != null, "CK3 must be running");
        int pid = ck3!.Id;
        float? Level() => AudioSessions.List().Where(s => s.ProcessId == pid).Select(s => (float?)s.Volume).FirstOrDefault();
        var before = Level();
        if (before == null) { Console.WriteLine("      CK3 has no audio session yet (play a sound in CK3); skipped"); return; }
        using var ux = LiveUx();
        await ux.RampProcessVolumeAsync(pid, 0f, TimeSpan.FromMilliseconds(600), CancellationToken.None);
        var silent = Level();
        Console.WriteLine($"      CK3 volume {before:F2} -> {silent:F2}");
        Expect.Near(0, silent ?? -1, 0.01, "ramped to silence");
        Expect.True(File.Exists(ProcessVolumeController.DefaultStorePath), "the user's level is persisted while lowered");
        await ux.RestoreProcessVolumeAsync(pid, TimeSpan.FromMilliseconds(600), CancellationToken.None);
        var after = Level();
        Console.WriteLine($"      restored to {after:F2}");
        Expect.Near(before.Value, after ?? -1, 0.01, "restored to the user's own level");
        string store = ProcessVolumeController.DefaultStorePath;
        Expect.False(File.Exists(store) && File.ReadAllText(store).Contains($"\"processId\": {pid}"), "nothing left to recover for CK3");
    }

    /// <summary>
    /// The overlay never presents a card over another application (such as the console running these tests), so the live
    /// card tests need CK3 in front. Waits up to 15 s for the operator to click CK3; the test itself never changes focus.
    /// </summary>
    private static async Task WaitForCk3InFrontAsync(nint ck3)
    {
        if (Native.GetForegroundWindow() == ck3) return;
        Console.WriteLine("      click CK3 within 15 s (cards are never shown over another application)");
        var sw = Stopwatch.StartNew();
        while (Native.GetForegroundWindow() != ck3 && sw.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(100);
        Expect.True(Native.GetForegroundWindow() == ck3, "CK3 is the foreground window");
    }

    private static TransitionUx LiveUx()
    {
        UxLog.Sink = line => Console.WriteLine("      " + line);
        var ux = new TransitionUx();
        ux.Initialize(new UxOptions());
        return ux;
    }

    // ------------------------------------------------------------------------------------------------ fonts and cards

    private static void FontDiscovery()
    {
        var fonts = Fonts.Value;
        string? dir = Ck3Install.FindFontsDirectory(null);
        Console.WriteLine($"      fonts dir: {dir ?? "(CK3 not found)"}; family '{fonts.Family.Name}' italic={fonts.HasItalic} from {fonts.Source}");
        if (dir == null)
        {
            Expect.False(fonts.IsRequestedFamily && fonts.Source.EndsWith(".ttf"), "without CK3 the family cannot come from its files");
            return;
        }
        var files = CardFonts.FindCandidateFiles("Fondamento", dir);
        Expect.True(files.Any(f => f.EndsWith("Fondamento-Regular.ttf", StringComparison.OrdinalIgnoreCase)), "Fondamento-Regular.ttf found");
        Expect.True(fonts.IsRequestedFamily, "Fondamento loaded from the CK3 install");
        Expect.Equal("Fondamento", fonts.Family.Name);
        Expect.True(fonts.HasItalic, "Fondamento-Italic merged into the family");
        Expect.True(CardFonts.FindCandidateFiles("Gitan", dir).Count > 0, "search finds other CK3 families too (Gitan .otf)");
    }

    /// <summary>
    /// Fonts are read into memory: the file is not held open (AddFontFile would lock it against writers such as a Steam update),
    /// the glyphs are the same as GDI+'s file loading gives, and drawing with disposed fonts fails cleanly. Uses a copy of a
    /// Windows system font in a temp "game/fonts" folder, never a CK3 file.
    /// </summary>
    private static void FontsMemoryLoaded()
    {
        string systemFont = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "georgia.ttf");
        if (!File.Exists(systemFont)) { Console.WriteLine("      georgia.ttf not installed; skipped"); return; }
        string root = Path.Combine(Path.GetTempPath(), "ck3xbl_selftest_fonts_" + Guid.NewGuid().ToString("N"));
        string fontsDir = Path.Combine(root, "game", "fonts");
        Directory.CreateDirectory(fontsDir);
        string copy = Path.Combine(fontsDir, "Georgia-Regular.ttf");
        File.Copy(systemFont, copy);
        try
        {
            var fonts = CardFonts.Load("Georgia", root);
            string memoryOutline;
            try
            {
                Console.WriteLine($"      family '{fonts.Family.Name}' from {fonts.Source}");
                Expect.True(fonts.IsRequestedFamily && fonts.Source == copy, "loaded from the install folder");
                using (new FileStream(copy, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }   // throws if still held open
                memoryOutline = Outline(fonts.Family);
            }
            finally { fonts.Dispose(); }
            fonts.Dispose();   // idempotent

            using (var fileFonts = new PrivateFontCollection())
            {
                fileFonts.AddFontFile(copy);
                using var fileFamily = fileFonts.Families[0];
                Expect.Equal(Outline(fileFamily), memoryOutline, "memory-loaded glyph outlines match file loading");
            }
            using var bmp = new Bitmap(320, 60, PixelFormat.Format32bppPArgb);
            Expect.Throws<Exception>(() => TitleCardRenderer.RenderHint(bmp, "after dispose", fonts, 0.3f));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }   // GDI+ may keep the file-loaded copy mapped for a moment; it is only a temp file
        }
    }

    private static string Outline(FontFamily family)
    {
        using var path = new GraphicsPath();
        path.AddString("Hello Þð Æ ǫ", family, (int)FontStyle.Regular, 40, PointF.Empty, StringFormat.GenericTypographic);
        var b = path.GetBounds();
        return $"{path.PointCount} points, {b.Width:F2}x{b.Height:F2}";
    }

    private static void FontsLoadNeverThrows()
    {
        string missing = Path.Combine(Path.GetTempPath(), "ck3xbl_selftest_missing_" + Guid.NewGuid().ToString("N"));
        var fonts = CardFonts.Load("Ck3xblNoSuchFamily", missing);
        try
        {
            Console.WriteLine($"      unknown family -> '{fonts.Family.Name}' from {fonts.Source}");
            Expect.False(fonts.IsRequestedFamily);
            Expect.True(fonts.Source.StartsWith("fallback:", StringComparison.Ordinal), "falls back to a serif");
        }
        finally { fonts.Dispose(); }
        Expect.Equal(0, CardFonts.FindCandidateFiles("Fondamento", missing).Count, "a missing folder has no candidates");
    }

    private static void LogFallbackFile()
    {
        var sink = UxLog.Sink;
        string file = UxLog.FallbackFile;
        string dir = Path.Combine(Path.GetTempPath(), "ck3xbl_selftest_log_" + Guid.NewGuid().ToString("N"));
        try
        {
            UxLog.Sink = null;
            UxLog.FallbackFile = Path.Combine(dir, "ux.log");
            UxLog.Write("selftest: no sink set");
            Expect.True(File.ReadAllText(UxLog.FallbackFile).Contains("[ux] selftest: no sink set"), "without a sink, lines land in the fallback file");
            Expect.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ck3xbl", "ux.log"), file,
                "the default fallback file sits next to bridge.log");
        }
        finally
        {
            UxLog.Sink = sink;
            UxLog.FallbackFile = file;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static void ResultKind()
    {
        Expect.Equal(CardKind.Victory, TitleCardRenderer.KindForResult("Victory at Orangallu"));
        Expect.Equal(CardKind.Defeat, TitleCardRenderer.KindForResult("Defeat at Orangallu"));
        Expect.Equal(CardKind.Defeat, TitleCardRenderer.KindForResult("The Rout at Hastings"));
        Expect.Equal(CardKind.Victory, TitleCardRenderer.KindForResult(null));
    }

    private static void RenderAndCheck(string name, TitleCard sample, CardKind kind, int w, int h, bool withUnderlay)
    {
        var (left, right) = EnsureSampleCoas();
        var card = new TitleCard { Title = sample.Title, Subtitle = sample.Subtitle, Detail = sample.Detail, LeftCoaPng = left, RightCoaPng = right };
        using var underlay = withUnderlay ? FakeMap(1920, 1080) : null;
        var sw = Stopwatch.StartNew();
        using var composite = TitleCardRenderer.RenderComposite(card, kind, w, h, Fonts.Value, underlay, out var layout);
        long renderMs = sw.ElapsedMilliseconds;
        using var backgroundOnly = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        TitleCardRenderer.RenderBackground(backgroundOnly, kind, underlay);

        string path = Save(composite, $"{name}_{w}x{h}.png");
        Console.WriteLine($"      {path} ({renderMs} ms, title em {layout.TitleEmPixels:F0}px, title {Fmt(layout.TitleBounds)})");

        Expect.True(new FileInfo(path).Length > 50_000, "PNG is not trivially small");
        var stats = LuminanceStats(composite, new Rectangle(0, 0, w, h));
        Expect.True(stats.StdDev > 3, $"image is not flat (stddev {stats.StdDev:F1})");
        Expect.True(stats.Mean is > 4 and < 120, $"overall a dark card (mean luminance {stats.Mean:F1})");

        // Layout: title on screen, centred, not cramped; coats of arms never overlap the text.
        var title = layout.TitleBounds;
        Expect.True(title.Left > w * 0.08f && title.Right < w * 0.92f, $"title inside the card ({Fmt(title)})");
        Expect.Near(w / 2.0, title.Left + title.Width / 2.0, w * 0.01, "title centred");
        Expect.Near(h * TitleCardRenderer.TitleCenterFraction, title.Top + title.Height / 2.0, h * 0.05, "title at ~40% height");
        Expect.True(!layout.LeftCoaBounds.IntersectsWith(layout.TextBounds) && !layout.RightCoaBounds.IntersectsWith(layout.TextBounds), "CoAs clear of the text");
        Expect.True(layout.LeftCoaBounds.Width > 0 && layout.RightCoaBounds.Width > 0, "both CoAs drawn");

        // Contrast: bright title strokes against the background they sit on (WCAG ratio, sRGB-linearised).
        var titleRect = Rectangle.Round(title);
        double textL = LuminancePercentile(composite, titleRect, 0.97);
        double backL = LuminanceStats(backgroundOnly, titleRect).Mean;
        double ratio = ContrastRatio(textL, backL);
        Console.WriteLine($"      title contrast {ratio:F1}:1 (text {textL:F0}, background {backL:F0})");
        Expect.True(ratio >= 4.5, $"title contrast {ratio:F1}:1 is at least 4.5:1");
    }

    private static void LongTitleWraps()
    {
        var card = new TitleCard
        {
            Title = "The Battle of Constantinople and the Golden Horn in the Shadow of the Theodosian Walls",
            Subtitle = "Basileios Makedon, Emperor of the Romans, against the Host of Symeon the Great of Bulgaria, and his allies · Winter, 913",
            Detail = "Within the walls of Constantinople",
        };
        using var bmp = TitleCardRenderer.RenderComposite(card, CardKind.Title, 1920, 1080, Fonts.Value, null, out var layout);
        Save(bmp, "title_long_1920x1080.png");
        Expect.Equal(2, layout.TitleLineCount);
        Expect.True(layout.TextBounds.Left >= 1920 * 0.04f && layout.TextBounds.Right <= 1920 * 0.96f, $"all text on screen ({Fmt(layout.TextBounds)})");
    }

    /// <summary>What the player sees mid-darken: freeze frame, card background at 55%, text at 30%.</summary>
    private static void TransitionPreview()
    {
        const int w = 2560, h = 1440;
        var (left, right) = EnsureSampleCoas();
        var card = new TitleCard { Title = TitleSample.Title, Subtitle = TitleSample.Subtitle, Detail = TitleSample.Detail, LeftCoaPng = left, RightCoaPng = right };
        using var freeze = FakeMap(w, h);
        using var bg = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using var fg = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        TitleCardRenderer.RenderBackground(bg, CardKind.Title, freeze);
        TitleCardRenderer.RenderForeground(fg, card, CardKind.Title, Fonts.Value);
        using var frame = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(frame))
        {
            g.DrawImageUnscaled(freeze, 0, 0);
            DrawWithAlpha(g, bg, 0.55f);
            DrawWithAlpha(g, fg, 0.30f);
        }
        Save(frame, "transition_mid_2560x1440.png");
        Expect.True(LuminanceStats(frame, new Rectangle(0, 0, w, h)).StdDev > 5, "preview has content");
    }

    /// <summary>If CK3 is running: a read-only PrintWindow capture (no focus, no input) used as the card's underlay.</summary>
    private static void OverLiveCk3()
    {
        nint ck3 = GameWindows.FindByProcessName("ck3", GameWindows.Ck3WindowClass);
        if (ck3 == 0) { Console.WriteLine("      CK3 is not running; skipped"); return; }
        Console.WriteLine($"      {GameWindows.Describe(ck3)}");
        using var first = FrameGrab.Capture(ck3);
        using var frame = FrameGrab.Capture(ck3);    // second grab = the warm timing the transition sees
        if (frame == null || first == null) { Console.WriteLine("      capture unavailable (minimized, cloaked or blank); skipped"); return; }
        Console.WriteLine($"      capture {frame.ScreenRect}: cold {first.ElapsedMs} ms, warm {frame.ElapsedMs} ms");
        using var view = frame.Surface.CreateView(PixelFormat.Format32bppRgb);
        var monitor = GameWindows.MonitorRectOf(ck3);
        using var bmp = TitleCardRenderer.RenderComposite(TitleSample, CardKind.Title, monitor.Width, monitor.Height, Fonts.Value, view, out _);
        Save(bmp, $"title_over_ck3_{monitor.Width}x{monitor.Height}.png");
        Expect.True(frame.ElapsedMs < 600, "warm capture fits the 600 ms freeze-frame budget");
    }

    // ------------------------------------------------------------------------------------------------ audio (read-only)

    private static void AudioListReadOnly()
    {
        var sessions = AudioSessions.List();
        Console.WriteLine($"      {sessions.Count} render session(s)");
        foreach (var s in sessions)
            Console.WriteLine($"        pid {s.ProcessId,-6} {s.ProcessName,-22} {s.State,-8} vol {s.Volume:F2} muted {s.Muted} system {s.IsSystemSounds}");
        Expect.True(sessions.All(s => s.Volume is >= 0f and <= 1f), "volumes are normalised");
        var again = AudioSessions.List().ToDictionary(s => s.InstanceId + "|" + s.EndpointId, s => s.Volume);
        foreach (var s in sessions)
            if (again.TryGetValue(s.InstanceId + "|" + s.EndpointId, out var v))
                Expect.Near(s.Volume, v, 0.05, $"listing does not change {s.ProcessName}'s volume");
    }

    private static void AudioCurve()
    {
        Expect.Near(0.2, ProcessVolumeController.PerceptualLerp(0.2f, 0f, 0), 1e-6);
        Expect.Near(0.0, ProcessVolumeController.PerceptualLerp(0.2f, 0f, 1), 1e-6);
        Expect.Near(0.2, ProcessVolumeController.PerceptualLerp(0f, 0.2f, 1), 1e-6);
        float previous = 1f;
        for (int i = 0; i <= 20; i++)
        {
            float v = ProcessVolumeController.PerceptualLerp(0.8f, 0f, i / 20.0);
            Expect.True(v <= previous + 1e-6f, "fade-out is monotonic");
            previous = v;
        }
        Expect.True(ProcessVolumeController.PerceptualLerp(0.8f, 0f, 0.5) < 0.4f, "amplitude falls faster than linear (even loudness)");
    }

    private static void AudioStorePending()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ck3xbl_selftest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "audio.json");
        try
        {
            // An entry for a process that is not running stays pending (untouched) for when that game is next ramped.
            File.WriteAllText(path, """
                { "version": 1, "entries": [ { "processName": "ck3xbl_selftest_no_such_process", "processId": 4242,
                  "processStartUtcTicks": 1, "userLevel": 0.2, "lastSetLevel": 0.2,
                  "sessions": { "{inst}": { "user": 0.2, "lastSet": 0.0 } }, "savedUtc": "2026-10-03T00:00:00Z" } ] }
                """);
            using var controller = new ProcessVolumeController(path);
            Expect.Equal(0, controller.RecoverFromPreviousRun());
            Expect.Equal(1, controller.PendingCount);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var entries = doc.RootElement.GetProperty("entries");
            Expect.Equal(1, entries.GetArrayLength());
            Expect.Equal("ck3xbl_selftest_no_such_process", entries[0].GetProperty("processName").GetString());
            Expect.Near(0.2, entries[0].GetProperty("userLevel").GetDouble(), 1e-6);
            Expect.False(controller.IsRemembered(4242), "a pending entry is not an active memory");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// A level that cannot be put back (the process exited, or it has no audio session) becomes pending instead of being
    /// forgotten; pending levels of one game merge; and a pending level is folded into the memory of a process being managed.
    /// No audio session is ever touched: the processes are fake pids and this test process (which plays no sound).
    /// </summary>
    private static async Task AudioUnrestorableLevelsGoPending()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ck3xbl_selftest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "audio.json");
        const string game = "ck3xbl_selftest_game";
        const int goneA = 0x7FFFFFF8, goneB = 0x7FFFFFF4;   // no process has these ids
        try
        {
            using var controller = new ProcessVolumeController(path);
            controller.RememberForTest(goneA, game, 1, userLevel: 0.6f, lastSetLevel: 0.6f);
            Expect.True(controller.IsRemembered(goneA));
            await controller.RestoreAsync(goneA, TimeSpan.Zero, CancellationToken.None);
            Expect.False(controller.IsRemembered(goneA), "an exited process is not remembered any more");
            Expect.Equal(1, controller.PendingCount, "its level is pending, not lost");
            var stored = StoredAudioEntries(path).Single(e => e.Name == game);
            Expect.Near(0.6, stored.User, 1e-6, "pending user level persisted");

            controller.RememberForTest(goneB, game, 1, userLevel: 0.3f, lastSetLevel: 0.1f);
            controller.RestoreAllNow();
            Expect.Equal(1, controller.PendingCount, "pending levels of one game merge");
            stored = StoredAudioEntries(path).Single(e => e.Name == game);
            Expect.Near(0.6, stored.User, 1e-6, "the higher user level wins");
            Expect.Near(0.6, stored.LastSet, 1e-6, "the higher last-set level wins");

            // This process is running but has no audio session: the level waits as pending, and checking applies nothing.
            using var self = Process.GetCurrentProcess();
            long start = self.StartTime.ToUniversalTime().Ticks;
            controller.RememberForTest(self.Id, self.ProcessName, start, userLevel: 0.5f, lastSetLevel: 0f);
            await controller.RestoreAsync(self.Id, TimeSpan.Zero, CancellationToken.None);
            Expect.False(controller.IsRemembered(self.Id));
            Expect.Equal(2, controller.PendingCount, "no audio session: pending");
            Expect.Equal(0, controller.CheckPendingNow(), "no session to apply it to yet");
            Expect.Equal(2, controller.PendingCount);

            // The game is managed again (lowered at our level 0, so it looked like a user level of 0): the pending level is folded in.
            controller.RememberForTest(self.Id, self.ProcessName, start, userLevel: 0f, lastSetLevel: 0f);
            controller.CheckPendingNow();   // the 2 s watch may have done it already; the outcome is what counts
            Expect.Equal(1, controller.PendingCount, "the pending level was consumed");
            Expect.Near(0.5, StoredAudioEntries(path).Single(e => e.Pid == self.Id).User, 1e-6, "the managed process now restores to 0.5");

            controller.RestoreAllNow();
            Expect.False(controller.IsRemembered(self.Id), "no session at exit either: pending again");
            Expect.Equal(2, controller.PendingCount);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static List<(string Name, int Pid, double User, double LastSet)> StoredAudioEntries(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("entries").EnumerateArray()
            .Select(e => (e.GetProperty("processName").GetString() ?? "", e.GetProperty("processId").GetInt32(),
                          e.GetProperty("userLevel").GetDouble(), e.GetProperty("lastSetLevel").GetDouble()))
            .ToList();
    }

    // ------------------------------------------------------------------------------------------------ overlay plumbing (hidden)

    /// <summary>
    /// Creates an overlay window WITHOUT showing it, uploads content, checks the window styles, then fades its alpha with the
    /// real 60 fps animation clock and checks the frame cadence and duration.
    /// </summary>
    private static async Task OverlayHiddenWindowClock()
    {
        using var ui = new OverlayThread();
        nint foregroundBefore = Native.GetForegroundWindow();
        var window = await ui.InvokeAsync(() =>
        {
            var w = LayeredWindow.Create("selftest", new ScreenRect(0, 0, 64, 64), perPixelAlpha: true);
            using var dib = DibSurface.Create(64, 64);
            Expect.True(w.SetContent(dib, 0), "UpdateLayeredWindow accepts content on a hidden window");
            return w;
        });
        try
        {
            var (ex, visible) = await ui.InvokeAsync(() => (Native.ExStyle(window.Hwnd), Native.IsWindowVisible(window.Hwnd)));
            Expect.Equal(LayeredWindow.OverlayExStyle, ex & LayeredWindow.OverlayExStyle, "ex-style has LAYERED|TRANSPARENT|NOACTIVATE|TOOLWINDOW|TOPMOST");
            Expect.False(visible, "the test window is never shown");

            const double durationMs = 400;
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var (startFrames, startMs) = await ui.InvokeAsync(() =>
            {
                ui.Animator.FadeTo(window, 255, durationMs, 0, Easing.InOutSine, ok => done.TrySetResult(ok));
                return (ui.Animator.FrameCount, ui.NowMs);
            });
            Expect.True(await done.Task.WaitAsync(TimeSpan.FromSeconds(3)), "fade completed");
            var (endFrames, endMs, alpha) = await ui.InvokeAsync(() => (ui.Animator.FrameCount, ui.NowMs, window.Alpha));
            long frames = endFrames - startFrames;
            double elapsed = endMs - startMs;
            Console.WriteLine($"      fade {durationMs} ms took {elapsed:F0} ms over {frames} frames ({1000 * frames / elapsed:F1} fps)");
            Expect.Equal((byte)255, alpha);
            Expect.True(elapsed >= durationMs - 1 && elapsed < durationMs + 120, $"fade duration {elapsed:F0} ms");
            Expect.True(frames >= 20 && frames <= 30, $"~60 fps cadence ({frames} frames for {durationMs} ms)");
            Expect.Equal(foregroundBefore, Native.GetForegroundWindow(), "foreground unchanged");
        }
        finally { await ui.InvokeAsync(() => window.Dispose()); }
    }

    /// <summary>
    /// The card state machine with real overlay windows that are never shown: present, hide, stale requests, replacement,
    /// a hide overtaking a freeze frame, cancellation during the fade-in, and the max-hold safety valve.
    /// </summary>
    private static async Task OverlayStateMachine()
    {
        LayeredWindow.ShowOnScreen = false;
        try
        {
            using var ui = new OverlayThread();
            long latest = 0;
            nint foreground = 0;   // what the controller sees as the foreground window (0 = none: never foreign)
            var controller = await ui.InvokeAsync(() =>
                new OverlayController(ui, () => Interlocked.Read(ref latest), _ => false, () => foreground));
            var bounds = new ScreenRect(0, 0, 320, 180);
            CardSurfaces Card() => new(DibSurface.Create(320, 180), DibSurface.Create(320, 180), bounds, new CardLayout());
            Task<int> Count() => ui.InvokeAsync(() => controller.WindowCount);
            var wait = TimeSpan.FromSeconds(3);

            // Present, then hide.
            latest = 1;
            var shown = await ui.InvokeAsync(() => controller.Present(1, Card(), CardTiming.FadeIn));
            Expect.True(await shown.WaitAsync(wait), "card 1 became opaque");
            Expect.Equal(2, await Count(), "background + text windows");
            latest = 2;
            await (await ui.InvokeAsync(() => controller.HideAll(200))).WaitAsync(wait);
            Expect.Equal(0, await Count(), "hidden cards are destroyed");

            // A request that is no longer the latest is dropped.
            Expect.False(await (await ui.InvokeAsync(() => controller.Present(1, Card(), CardTiming.FadeIn))).WaitAsync(wait), "stale card dropped");
            Expect.Equal(0, await Count());

            // A newer card stacks above the old one, which goes once the new one is opaque.
            latest = 3;
            Expect.True(await (await ui.InvokeAsync(() => controller.Present(3, Card(), CardTiming.FadeIn))).WaitAsync(wait));
            latest = 4;
            var shown4 = await ui.InvokeAsync(() => controller.Present(4, Card(), CardTiming.FadeIn));
            Expect.Equal(4, await Count(), "old card waits under the new one");
            Expect.True(await shown4.WaitAsync(wait), "card 4 became opaque");
            Expect.Equal(2, await Count(), "old card destroyed");

            // A hide that overtakes a slow show: the freeze frame goes, and the late card is never presented.
            latest = 5;
            using (var frame = new CapturedFrame(DibSurface.Create(320, 180), bounds, 0))
                Expect.True(await ui.InvokeAsync(() => controller.ShowFreeze(5, frame)), "freeze frame shown");
            Expect.Equal(3, await Count(), "freeze frame above card 4");
            latest = 6;
            await (await ui.InvokeAsync(() => controller.HideAll(100))).WaitAsync(wait);
            Expect.False(await (await ui.InvokeAsync(() => controller.Present(5, Card(), CardTiming.FromFreezeFrame))).WaitAsync(wait));
            Expect.Equal(0, await Count(), "nothing stranded");

            // Cancelling during the fade-in removes the card quickly.
            latest = 7;
            var shown7 = await ui.InvokeAsync(() => controller.Present(7, Card(), new CardTiming(800, 0, 800)));
            await Task.Delay(120);
            await ui.InvokeAsync(() => controller.CancelShow(7));
            Expect.False(await shown7.WaitAsync(wait), "cancelled card reports not shown");
            await Task.Delay(400);
            Expect.Equal(0, await Count(), "cancelled card removed");

            // Safety valve: a card held too long removes itself.
            var reasons = new List<string>();
            await ui.InvokeAsync(() => { controller.MaxHold = TimeSpan.FromMilliseconds(800); controller.AutoHidden += reasons.Add; });
            latest = 8;
            Expect.True(await (await ui.InvokeAsync(() => controller.Present(8, Card(), CardTiming.FadeIn))).WaitAsync(wait));
            var sw = Stopwatch.StartNew();
            while (await Count() > 0 && sw.Elapsed < TimeSpan.FromSeconds(4)) await Task.Delay(100);
            Expect.Equal(0, await Count(), "held card removed by the max-hold valve");
            Expect.Equal(1, reasons.Count, "AutoHidden raised once");
            Console.WriteLine($"      max-hold valve fired: {reasons[0]}");
            Expect.False(await (await ui.InvokeAsync(() => controller.Present(8, Card(), CardTiming.FadeIn))).WaitAsync(wait),
                "a request removed by a valve never comes back");
            Expect.Equal(0, await Count());

            // The player is already in another application: nothing is presented, whatever is up leaves, and the request stays
            // dismissed even after they come back. A newer request is shown normally.
            nint foreign = FindForeignWindow();
            if (foreign == 0) Console.WriteLine("      no window of another application found; foreign-foreground part skipped");
            else
            {
                await ui.InvokeAsync(() => controller.MaxHold = TimeSpan.FromMinutes(2));
                latest = 9;
                using (var frame = new CapturedFrame(DibSurface.Create(320, 180), bounds, 0))
                    Expect.True(await ui.InvokeAsync(() => controller.ShowFreeze(9, frame)), "freeze frame shown while CK3 is in front");
                foreground = foreign;   // the player alt-tabs away while the card renders
                Expect.False(await (await ui.InvokeAsync(() => controller.Present(9, Card(), CardTiming.FromFreezeFrame))).WaitAsync(wait),
                    "card not presented over another application");
                Expect.Equal(2, reasons.Count, "AutoHidden raised for the declined card");
                Console.WriteLine($"      declined: {reasons[1]}");
                var sw2 = Stopwatch.StartNew();
                while (await Count() > 0 && sw2.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(50);
                Expect.Equal(0, await Count(), "the freeze frame left too");
                foreground = 0;         // back in the game
                Expect.False(await (await ui.InvokeAsync(() => controller.Present(9, Card(), CardTiming.FromFreezeFrame))).WaitAsync(wait),
                    "the declined request stays dismissed");
                latest = 10;
                Expect.True(await (await ui.InvokeAsync(() => controller.Present(10, Card(), CardTiming.FadeIn))).WaitAsync(wait), "a newer card is shown");
                await (await ui.InvokeAsync(() => controller.HideAll(100))).WaitAsync(wait);
            }
            await ui.InvokeAsync(() => controller.Dispose());
        }
        finally { LayeredWindow.ShowOnScreen = true; }
    }

    /// <summary>TransitionUx end to end with hidden windows: title card, hint, result card replacing it, hide, cancellation.</summary>
    private static async Task TransitionFlowHidden()
    {
        LayeredWindow.ShowOnScreen = false;
        string store = Path.Combine(Path.GetTempPath(), "ck3xbl_selftest_audio_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var ux = new TransitionUx(store, () => 0);
            ux.Initialize(new UxOptions());
            var sw = Stopwatch.StartNew();
            await ux.ShowTitleCardAsync(TitleSample, CancellationToken.None);
            long titleMs = sw.ElapsedMilliseconds;
            Expect.Equal(2, await ux.OverlayWindowCountAsync(), "title card up");
            await ux.ShowHintAsync("Click to continue to the battle", CancellationToken.None);
            Expect.Equal(3, await ux.OverlayWindowCountAsync(), "hint strip added");
            sw.Restart();
            await ux.ShowResultCardAsync(VictorySample, CancellationToken.None);
            long resultMs = sw.ElapsedMilliseconds;
            Expect.Equal(2, await ux.OverlayWindowCountAsync(), "result card replaced the title card");
            sw.Restart();
            await ux.HideAsync(CancellationToken.None);
            long hideMs = sw.ElapsedMilliseconds;
            Expect.Equal(0, await ux.OverlayWindowCountAsync(), "all overlay windows destroyed after hide");
            Console.WriteLine($"      title card {titleMs} ms, result card {resultMs} ms, hide {hideMs} ms (render + fades)");
            Expect.True(titleMs < 2500 && resultMs < 2500 && hideMs is >= 400 and < 1500, "timings in budget");

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            bool cancelled = false;
            try { await ux.ShowTitleCardAsync(TitleSample, cts.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Expect.True(cancelled, "cancellation surfaces as OperationCanceledException");
            await Task.Delay(500);
            Expect.Equal(0, await ux.OverlayWindowCountAsync(), "a cancelled card leaves nothing behind");
        }
        finally
        {
            LayeredWindow.ShowOnScreen = true;
            File.Delete(store);
        }
    }

    /// <summary>The player is already in another application: the card is declined (never presented), the call still completes.</summary>
    private static async Task TransitionNoCardOverForeignApp()
    {
        nint foreign = FindForeignWindow();
        if (foreign == 0) { Console.WriteLine("      no window of another application found; skipped"); return; }
        LayeredWindow.ShowOnScreen = false;
        string store = Path.Combine(Path.GetTempPath(), "ck3xbl_selftest_audio_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var ux = new TransitionUx(store, () => foreign);
            ux.Initialize(new UxOptions());
            var reasons = new List<string>();
            ux.OverlayAutoHidden += reason => { lock (reasons) reasons.Add(reason); };
            await ux.ShowTitleCardAsync(TitleSample, CancellationToken.None);
            Expect.Equal(0, await ux.OverlayWindowCountAsync(), "nothing presented over another application");
            Expect.False(ux.TitleCardPresented, "a declined title card is reported as not presented");
            lock (reasons)
            {
                Expect.Equal(1, reasons.Count, "OverlayAutoHidden reports the declined card");
                Console.WriteLine($"      {reasons[0]}");
            }
            await ux.ShowHintAsync("Click to continue to the battle", CancellationToken.None);
            Expect.Equal(0, await ux.OverlayWindowCountAsync(), "no hint without a card");
        }
        finally
        {
            LayeredWindow.ShowOnScreen = true;
            File.Delete(store);
        }
    }

    /// <summary>Registered games are trusted only while the same process lives; lookups never throw.</summary>
    private static void GameProcessRegistry()
    {
        using var ux = new TransitionUx(Path.Combine(Path.GetTempPath(), "ck3xbl_selftest_unused.json"), () => 0);
        using var self = Process.GetCurrentProcess();
        Expect.False(ux.IsGameProcess(self.Id), "the bridge itself is not a game");
        ux.RegisterGameProcess(self.Id);
        Expect.False(ux.IsGameProcess(self.Id), "the bridge cannot register itself");
        Expect.False(ux.IsGameProcess(0x7FFFFFF8), "a pid without a process is not a game (and does not throw)");

        using var other = Process.GetProcessesByName("explorer").FirstOrDefault();
        if (other == null) { Console.WriteLine("      explorer not running; registration part skipped"); return; }
        Expect.False(ux.IsGameProcess(other.Id), "explorer is not a game by name");
        ux.RegisterGameProcess(other.Id);
        Expect.True(ux.IsGameProcess(other.Id), "a registered process is trusted");
    }

    // ------------------------------------------------------------------------------------------------ reveal (PROTOCOL §13.6)

    /// <summary>Runs <paramref name="body"/> with an initialized TransitionUx whose windows are never shown (no foreign foreground).</summary>
    private static async Task WithHiddenUx(Func<TransitionUx, Task> body)
    {
        LayeredWindow.ShowOnScreen = false;
        string store = Path.Combine(Path.GetTempPath(), "ck3xbl_selftest_audio_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var ux = new TransitionUx(store, () => 0);
            ux.Initialize(new UxOptions());
            await body(ux);
        }
        finally
        {
            LayeredWindow.ShowOnScreen = true;
            File.Delete(store);
        }
    }

    /// <summary>Polls until no overlay window is left (or <paramref name="timeout"/>); returns how long that took.</summary>
    private static async Task<TimeSpan> WaitUntilNoOverlayAsync(TransitionUx ux, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (await ux.OverlayWindowCountAsync() > 0 && sw.Elapsed < timeout) await Task.Delay(5);
        return sw.Elapsed;
    }

    private static bool FullyOpaqueCard(IReadOnlyList<byte> alphas) => alphas.Count == 2 && alphas.All(a => a == 255);

    /// <summary>The card stays fully opaque for the hold, dissolves after it and is gone after hold + dissolve + 1 frame.</summary>
    private static Task RevealHoldsThenDissolves() => WithHiddenUx(async ux =>
    {
        const double holdMs = 400, dissolveMs = 600, slackMs = 150;
        await ux.ShowTitleCardAsync(TitleSample, CancellationToken.None);
        Expect.True(FullyOpaqueCard(await ux.OverlayWindowAlphasAsync()), "title card fully up");

        var samples = new List<(double StartMs, double EndMs, IReadOnlyList<byte> Alphas)>();
        var sw = Stopwatch.StartNew();
        var reveal = ux.RevealAsync(TimeSpan.FromMilliseconds(holdMs), TimeSpan.FromMilliseconds(dissolveMs), CancellationToken.None);
        while (!reveal.IsCompleted)
        {
            double start = sw.Elapsed.TotalMilliseconds;
            var alphas = await ux.OverlayWindowAlphasAsync();
            samples.Add((start, sw.Elapsed.TotalMilliseconds, alphas));
            await Task.Delay(15);
        }
        await reveal;
        double totalMs = sw.Elapsed.TotalMilliseconds;
        Expect.Equal(0, await ux.OverlayWindowCountAsync(), "the card is gone when the reveal completes");

        var held = samples.Where(s => s.EndMs < holdMs - 10).ToList();
        Console.WriteLine($"      reveal {holdMs} + {dissolveMs} ms took {totalMs:F0} ms; {held.Count} of {samples.Count} samples in the hold");
        Expect.True(held.Count >= 5, "sampled during the hold");
        Expect.True(held.All(s => FullyOpaqueCard(s.Alphas)), "fully opaque throughout the hold");
        Expect.True(samples.Any(s => s.StartMs > holdMs + 60 && s.Alphas.Any(a => a is > 0 and < 255)), "dissolving after the hold");
        Expect.True(totalMs >= holdMs + dissolveMs - 1, $"not gone before hold + dissolve ({totalMs:F0} ms)");
        Expect.True(totalMs < holdMs + dissolveMs + OverlayThread.FrameIntervalMs + slackMs, $"gone after hold + dissolve + 1 frame ({totalMs:F0} ms)");
    });

    /// <summary>Cancelling during the hold leaves the card up; cancelling during the dissolve removes it at once.</summary>
    private static Task RevealCancellation() => WithHiddenUx(async ux =>
    {
        await ux.ShowTitleCardAsync(TitleSample, CancellationToken.None);
        bool cancelled = false;
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
        {
            try { await ux.RevealAsync(TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(600), cts.Token); }
            catch (OperationCanceledException) { cancelled = true; }
        }
        Expect.True(cancelled, "cancellation during the hold surfaces as OperationCanceledException");
        await Task.Delay(300);
        Expect.True(FullyOpaqueCard(await ux.OverlayWindowAlphasAsync()), "cancelled during the hold: the card stays up for the caller");

        // Cancel 250 ms into a 1000 ms dissolve.
        cancelled = false;
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150 + 250)))
        {
            try { await ux.RevealAsync(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(1000), cts.Token); }
            catch (OperationCanceledException) { cancelled = true; }
        }
        Expect.True(cancelled, "cancellation during the dissolve surfaces as OperationCanceledException");
        var gone = await WaitUntilNoOverlayAsync(ux, TimeSpan.FromSeconds(1));
        Console.WriteLine($"      cancelled mid-dissolve: card gone {gone.TotalMilliseconds:F0} ms later");
        Expect.Equal(0, await ux.OverlayWindowCountAsync(), "cancelled during the dissolve: the card is removed");
        Expect.True(gone < TimeSpan.FromMilliseconds(100), "removed at once, not faded out");
    });

    /// <summary>RevealAsync(0, 500 ms) is HideAsync: the same fade, and an already cancelled token removes the card at once.</summary>
    private static Task RevealWithoutHoldIsHide() => WithHiddenUx(async ux =>
    {
        await ux.ShowTitleCardAsync(TitleSample, CancellationToken.None);
        var sw = Stopwatch.StartNew();
        await ux.HideAsync(CancellationToken.None);
        double hideMs = sw.Elapsed.TotalMilliseconds;
        Expect.Equal(0, await ux.OverlayWindowCountAsync(), "hide removed the card");

        await ux.ShowTitleCardAsync(TitleSample, CancellationToken.None);
        sw.Restart();
        await ux.RevealAsync(TimeSpan.Zero, TimeSpan.FromMilliseconds(500), CancellationToken.None);
        double revealMs = sw.Elapsed.TotalMilliseconds;
        Expect.Equal(0, await ux.OverlayWindowCountAsync(), "reveal removed the card");
        Console.WriteLine($"      hide {hideMs:F0} ms, reveal(0, 500) {revealMs:F0} ms");
        Expect.True(hideMs >= 499 && revealMs >= 499, "both fade for 500 ms");
        Expect.True(Math.Abs(hideMs - revealMs) < 150, "the same duration");

        foreach (bool reveal in new[] { false, true })
        {
            await ux.ShowTitleCardAsync(TitleSample, CancellationToken.None);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            bool cancelled = false;
            try
            {
                if (reveal) await ux.RevealAsync(TimeSpan.Zero, TimeSpan.FromMilliseconds(500), cts.Token);
                else await ux.HideAsync(cts.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            string name = reveal ? "reveal(0, 500)" : "hide";
            Expect.True(cancelled, $"{name}: a cancelled token surfaces");
            Expect.True(await WaitUntilNoOverlayAsync(ux, TimeSpan.FromSeconds(1)) < TimeSpan.FromMilliseconds(100), $"{name}: the card is removed at once");
        }
    });

    /// <summary>TitleCardPresented follows the last title card: true when it went fully up, false when cancelled or not shown.</summary>
    private static Task TitleCardPresented() => WithHiddenUx(async ux =>
    {
        Expect.False(ux.TitleCardPresented, "no title card yet");
        await ux.ShowTitleCardAsync(TitleSample, CancellationToken.None);
        Expect.True(ux.TitleCardPresented, "title card presented");
        await ux.ShowResultCardAsync(VictorySample, CancellationToken.None);
        Expect.True(ux.TitleCardPresented, "a result card does not change it");

        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
        {
            try { await ux.ShowTitleCardAsync(TitleSample, cts.Token); }
            catch (OperationCanceledException) { }
        }
        Expect.False(ux.TitleCardPresented, "a cancelled title card is not presented");
        await ux.HideAsync(CancellationToken.None);

        await ux.ShowTitleCardAsync(TitleSample, CancellationToken.None);
        Expect.True(ux.TitleCardPresented, "presented again");
        ux.Dispose();
        await ux.ShowTitleCardAsync(TitleSample, CancellationToken.None);
        Expect.False(ux.TitleCardPresented, "a disposed UX presents nothing");
    });

    // ------------------------------------------------------------------------------------------------ CK3 window mode

    private const long Ck3FullscreenStyle = 0x96020000;   // WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN | WS_MINIMIZEBOX
    private const long CaptionedStyle = 0x10CF0000;       // WS_VISIBLE | WS_OVERLAPPEDWINDOW (caption and sizing frame)
    private static readonly ScreenRect Monitor1440 = new(0, 0, 2560, 1440);

    private static void WindowModeClassification()
    {
        Expect.True(Ck3WindowModeGuard.IsFullscreenLike(Ck3FullscreenStyle, Monitor1440, Monitor1440),
            "CK3's verified fullscreen window (minimize box included) covering its monitor");
        Expect.True(Ck3WindowModeGuard.IsFullscreenLike(Ck3FullscreenStyle, new ScreenRect(2560, 0, 1920, 1080), new ScreenRect(2560, 0, 1920, 1080)),
            "on a secondary monitor");
        Expect.False(Ck3WindowModeGuard.IsFullscreenLike(Ck3FullscreenStyle, new ScreenRect(0, 0, 1920, 1080), Monitor1440),
            "a popup not covering its monitor is windowed");
        Expect.False(Ck3WindowModeGuard.IsFullscreenLike(CaptionedStyle, new ScreenRect(320, 180, 1920, 1080), Monitor1440),
            "a captioned window smaller than the monitor");
        Expect.False(Ck3WindowModeGuard.IsFullscreenLike(CaptionedStyle, Monitor1440, Monitor1440), "a caption covering the monitor");
        Expect.False(Ck3WindowModeGuard.IsFullscreenLike(Native.WS_POPUP | Native.WS_THICKFRAME, Monitor1440, Monitor1440), "a sizing frame alone");
        Expect.False(Ck3WindowModeGuard.IsFullscreenLike(Ck3FullscreenStyle, default, default), "no monitor");
        Expect.True(Ck3WindowModeGuard.IsFullscreenLike(unchecked((long)0xFFFFFFFF96020000), Monitor1440, Monitor1440),
            "a sign-extended style reads the same");
    }

    private static void WindowModePolicy()
    {
        var windowed = (Style: CaptionedStyle, Window: new ScreenRect(320, 180, 1920, 1080), Monitor: Monitor1440);
        var fullscreen = (Style: Ck3FullscreenStyle, Window: Monitor1440, Monitor: Monitor1440);
        nint hwnd = 0x1234;

        // facts = what the fake probe reports; borderlessWorks = what the fake restyle returns.
        (Ck3WindowModeGuard Guard, List<string> Notices, List<(nint, ScreenRect)> Restyled, Func<int> Probes) Make(
            string mode, Func<(long, ScreenRect, ScreenRect)?> facts, bool borderlessWorks = true)
        {
            var notices = new List<string>();
            var restyled = new List<(nint, ScreenRect)>();
            int probes = 0;
            var guard = new Ck3WindowModeGuard(
                _ => { probes++; return facts(); },
                notices.Add,
                (h, monitor) => { restyled.Add((h, monitor)); return borderlessWorks; },
                mode);
            return (guard, notices, restyled, () => probes);
        }

        foreach (string mode in new[] { "notice", "borderless", "ignore", "fullscreen please" })
        {
            var g = Make(mode, () => fullscreen);
            g.Guard.Check(100, hwnd);
            g.Guard.Check(100, hwnd);
            Expect.Equal(0, g.Notices.Count, $"{mode}: fullscreen-like CK3, no notice");
            Expect.Equal(0, g.Restyled.Count, $"{mode}: fullscreen-like CK3 is never touched");

            var gone = Make(mode, () => null);
            gone.Guard.Check(100, hwnd);
            Expect.True(gone.Notices.Count == 0 && gone.Restyled.Count == 0, $"{mode}: window gone, nothing");

            var failing = Make(mode, () => throw new InvalidOperationException("probe failed"));
            failing.Guard.Check(100, hwnd);
            Expect.True(failing.Notices.Count == 0 && failing.Restyled.Count == 0, $"{mode}: a failing probe does nothing and does not throw");
        }

        var notice = Make("notice", () => windowed);
        Expect.Equal(Ck3WindowModeGuard.ModeNotice, notice.Guard.Mode);
        notice.Guard.Check(100, hwnd);
        Expect.SequenceEqual([Ck3WindowModeGuard.WindowedNotice], notice.Notices, "one notice");
        notice.Guard.Check(100, hwnd);
        Expect.Equal(1, notice.Notices.Count, "the same CK3 process is told once");
        notice.Guard.Check(200, hwnd);
        Expect.Equal(2, notice.Notices.Count, "a new CK3 process is told again");
        Expect.Equal(0, notice.Restyled.Count, "notice never restyles");

        var borderless = Make(" Borderless ", () => windowed);
        Expect.Equal(Ck3WindowModeGuard.ModeBorderless, borderless.Guard.Mode, "mode is trimmed and case-insensitive");
        borderless.Guard.Check(100, hwnd);
        Expect.SequenceEqual([(hwnd, Monitor1440)], borderless.Restyled, "one makeBorderless over CK3's monitor");
        Expect.Equal(0, borderless.Notices.Count, "no notice when the restyle stuck");

        var stuck = Make("borderless", () => windowed, borderlessWorks: false);
        stuck.Guard.Check(100, hwnd);
        stuck.Guard.Check(100, hwnd);
        Expect.Equal(2, stuck.Restyled.Count, "the restyle is tried on every check");
        Expect.SequenceEqual([Ck3WindowModeGuard.WindowedNotice], stuck.Notices, "a restyle that did not stick: one notice");

        var ignore = Make("ignore", () => windowed);
        ignore.Guard.Check(100, hwnd);
        Expect.True(ignore.Notices.Count == 0 && ignore.Restyled.Count == 0 && ignore.Probes() == 0, "ignore: nothing, not even a probe");

        foreach (string? unknown in new[] { "fullscreen please", "", null })
        {
            var g = Make(unknown!, () => windowed);
            Expect.Equal(Ck3WindowModeGuard.ModeNotice, g.Guard.Mode, $"'{unknown}' reads as notice");
            g.Guard.Check(100, hwnd);
            Expect.Equal(1, g.Notices.Count, $"'{unknown}': notice");
        }

        var throwing = new Ck3WindowModeGuard(_ => windowed, _ => throw new InvalidOperationException("tray gone"),
            (_, _) => throw new InvalidOperationException("restyle failed"), "borderless");
        throwing.Check(100, hwnd);   // must not throw
    }

    /// <summary>The Win32 probe on a hidden captioned test window this test owns (read-only), and on handles without a window.</summary>
    private static void WindowModeWin32Probe()
    {
        Expect.Null(Ck3WindowModeGuard.Win32Probe(0), "no handle");
        const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        nint hwnd = Native.CreateWindowExW(0, "STATIC", "ck3xbl window mode selftest", WS_OVERLAPPEDWINDOW, 100, 100, 800, 600, 0, 0, 0, 0);
        Expect.True(hwnd != 0, "test window created");
        try
        {
            var facts = Ck3WindowModeGuard.Win32Probe(hwnd);
            Expect.True(facts.HasValue, "a live window is probed");
            var (style, window, monitor) = facts!.Value;
            Console.WriteLine($"      style 0x{style & 0xFFFFFFFF:X8}, window {window}, monitor {monitor}");
            Expect.Equal(new ScreenRect(100, 100, 800, 600), window);
            Expect.False(monitor.IsEmpty, "monitor rect");
            Expect.True((style & Native.WS_CAPTION) == Native.WS_CAPTION, "caption read");
            Expect.False(Ck3WindowModeGuard.IsFullscreenLike(style, window, monitor), "a captioned 800x600 window is windowed");
            Expect.False(Native.IsWindowVisible(hwnd), "still hidden");
        }
        finally { Native.DestroyWindow(hwnd); }
        Expect.Null(Ck3WindowModeGuard.Win32Probe(hwnd), "a destroyed window");
    }

    /// <summary>The ladder escalates only over nobody's focus, the desktop/taskbar or our own processes (read-only checks).</summary>
    private static void ForegroundEscalationPolicy()
    {
        const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        nint target = Native.CreateWindowExW(0, "STATIC", "ck3xbl ladder selftest", WS_OVERLAPPEDWINDOW, 0, 0, 100, 100, 0, 0, 0, 0);
        Expect.True(target != 0, "hidden target window created");
        try
        {
            Expect.True(Foreground.MayEscalateOver(0, target, null), "no foreground window");
            Expect.True(Foreground.MayEscalateOver(target, target, null), "our own process");
            foreach (var cls in new[] { "Shell_TrayWnd", "Progman" })
            {
                nint shell = FindWindowW(cls, null);
                if (shell != 0) Expect.True(Foreground.MayEscalateOver(shell, target, null), $"{cls} is neutral");
            }
            nint start = FindWindowW("Windows.UI.Core.CoreWindow", null);
            if (start != 0)
            {
                Expect.True(Foreground.IsShellWindow(start), "Start/Search counts as shell for the overlay valve");
                Expect.False(Foreground.MayEscalateOver(start, target, null), "but the ladder never injects Alt into Start/Search");
            }
            nint foreign = FindForeignWindow();
            if (foreign == 0) { Console.WriteLine("      no window of another application found; foreign part skipped"); return; }
            Expect.False(Foreground.MayEscalateOver(foreign, target, null), "another application");
            Expect.True(Foreground.MayEscalateOver(foreign, target, _ => true), "a trusted game");
            Expect.False(Foreground.MayEscalateOver(foreign, target, _ => throw new InvalidOperationException("exited")),
                "a failing trust check means foreign, and does not throw");
        }
        finally { Native.DestroyWindow(target); }
    }

    /// <summary>Without hooks the guard reports the conservative idle time; overlapping blocks keep blocking until the last ends.</summary>
    private static async Task InputGuardFallbackAndBlocks()
    {
        using var guard = new InputGuard(installHooks: false);   // never sees, swallows or sends input
        Expect.False(guard.IsActive, "no hooks");
        Expect.Near(InputSafety.LastInputIdle().TotalMilliseconds, guard.PhysicalIdle.TotalMilliseconds, 100,
            "without hooks, physical idle falls back to GetLastInputInfo");

        var a = guard.BlockPhysicalInput(TimeSpan.FromSeconds(3));
        var b = guard.BlockPhysicalInput(TimeSpan.FromSeconds(3));
        Expect.True(guard.IsBlocking && a.IsActive && b.IsActive);
        a.Dispose();
        Expect.False(a.IsActive);
        Expect.True(guard.IsBlocking, "ending one block leaves the overlapping one in force");
        b.Dispose();
        Expect.False(guard.IsBlocking, "no block left");

        var capped = guard.BlockPhysicalInput(TimeSpan.FromMilliseconds(60));
        Expect.True(capped.IsActive);
        await Task.Delay(150);
        Expect.False(capped.IsActive, "a block reports its own cap");
        Expect.False(guard.IsBlocking);
        capped.Dispose();
        using var huge = guard.BlockPhysicalInput(TimeSpan.FromHours(1));
        Expect.True(huge.DeadlineTick - Environment.TickCount64 <= InputGuard.MaxBlock.TotalMilliseconds, "capped at MaxBlock");
    }

    /// <summary>The main window of some other application (not this process, not a game, not a shell surface), or 0.</summary>
    private static nint FindForeignWindow()
    {
        string[] games = ["ck3", "Bannerlord", "Bannerlord.Native"];
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == Environment.ProcessId || games.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)) continue;
                    nint hwnd = p.MainWindowHandle;
                    if (hwnd != 0 && !Foreground.IsShellWindow(hwnd)) return hwnd;
                }
                catch (InvalidOperationException) { }   // exited meanwhile
            }
        }
        return 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string? className, string? windowName);

    /// <summary>MakeBorderless + Restore on a hidden captioned window this test owns.</summary>
    private static void StagerRoundTrip()
    {
        const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        nint hwnd = Native.CreateWindowExW(0, "STATIC", "ck3xbl stager selftest", WS_OVERLAPPEDWINDOW, 100, 100, 800, 600, 0, 0, 0, 0);
        Expect.True(hwnd != 0, "test window created");
        try
        {
            long originalStyle = Native.Style(hwnd);
            var stager = new WindowStager();
            var target = new ScreenRect(0, 0, 1280, 720);
            Expect.True(stager.MakeBorderless(hwnd, target), "frame stripped and rect applied");
            Expect.True(WindowStager.IsBorderlessAt(hwnd, target));
            Expect.Equal(0L, Native.Style(hwnd) & Native.WS_CAPTION, "no caption");
            Expect.False(Native.IsWindowVisible(hwnd), "still hidden");
            Expect.True(stager.EnsureBorderless(hwnd, target), "idempotent");
            stager.RestoreAll();
            Expect.Equal(originalStyle, Native.Style(hwnd), "style restored");
            Native.GetWindowRect(hwnd, out var r);
            Expect.Equal(new ScreenRect(100, 100, 800, 600), ScreenRect.From(r), "rect restored");
        }
        finally { Native.DestroyWindow(hwnd); }
    }

    // ------------------------------------------------------------------------------------------------ input and windows (read-only)

    private static void InputSafetyReadOnly()
    {
        var held = InputSafety.HeldKeys();
        var idle = InputSafety.LastInputIdle();
        var fg = InputSafety.GetForeground();
        Console.WriteLine($"      held [{string.Join(",", held.Select(k => "0x" + k.ToString("X2")))}], idle {idle.TotalMilliseconds:F0} ms, " +
                          $"foreground {(fg.IsNone ? "none" : $"{fg.ProcessName} '{fg.WindowClass}'")}");
        Expect.True(idle >= TimeSpan.Zero);
        Expect.True(held.All(vk => vk is >= 0x01 and <= 0xFE && vk is not (0x10 or 0x11 or 0x12)), "generic modifiers are reported as left/right");
        if (!fg.IsNone) Expect.True(InputSafety.IsForegroundProcess(fg.ProcessId));
    }

    private static void GameWindowsReadOnly()
    {
        var primary = GameWindows.PrimaryMonitorRect();
        Expect.False(primary.IsEmpty, "primary monitor rect");
        foreach (var (process, cls) in new[] { ("ck3", GameWindows.Ck3WindowClass), ("Bannerlord", GameWindows.BannerlordWindowClass) })
        {
            nint hwnd = GameWindows.FindByProcessName(process, cls);
            if (hwnd == 0) { Console.WriteLine($"      {process}: not running"); continue; }
            Console.WriteLine($"      {process}: {GameWindows.Describe(hwnd)}; monitor {GameWindows.MonitorRectOf(hwnd)}; client {GameWindows.ClientRectOnScreen(hwnd)}");
            Expect.False(GameWindows.MonitorRectOf(hwnd).IsEmpty);
            Expect.False(Native.IsTopmost(hwnd), $"{process} is not left TOPMOST");
        }
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static string Save(Bitmap bmp, string fileName)
    {
        Directory.CreateDirectory(OutputDirectory);
        string path = Path.Combine(OutputDirectory, fileName);
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    private static string Fmt(RectangleF r) => $"{r.X:F0},{r.Y:F0} {r.Width:F0}x{r.Height:F0}";

    private static void DrawWithAlpha(Graphics g, Bitmap image, float alpha)
    {
        var matrix = new ColorMatrix { Matrix33 = alpha };
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(matrix);
        g.DrawImage(image, new Rectangle(0, 0, image.Width, image.Height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
    }

    /// <summary>Two procedural shields (not real CK3 art) standing in for rendered coats of arms.</summary>
    private static (string left, string right) EnsureSampleCoas()
    {
        Directory.CreateDirectory(OutputDirectory);
        string left = Path.Combine(OutputDirectory, "sample_coa_left.png"), right = Path.Combine(OutputDirectory, "sample_coa_right.png");
        lock (Fonts)
        {
            if (!File.Exists(left)) MakeShield(left, Color.FromArgb(150, 24, 28), Color.FromArgb(222, 180, 70), chevron: true);
            if (!File.Exists(right)) MakeShield(right, Color.FromArgb(28, 52, 120), Color.FromArgb(235, 235, 225), chevron: false);
        }
        return (left, right);
    }

    private static void MakeShield(string path, Color field, Color charge, bool chevron)
    {
        using var b = new Bitmap(256, 256, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(b);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var shield = new GraphicsPath();
        shield.AddLine(24, 20, 232, 20);
        shield.AddLine(232, 20, 232, 120);
        shield.AddBezier(232, 120, 232, 190, 170, 225, 128, 244);
        shield.AddBezier(128, 244, 86, 225, 24, 190, 24, 120);
        shield.CloseFigure();
        using (var f = new SolidBrush(field)) g.FillPath(f, shield);
        g.SetClip(shield);
        using (var c = new SolidBrush(charge))
        {
            if (chevron) g.FillPolygon(c, [new PointF(10, 200), new PointF(128, 70), new PointF(246, 200), new PointF(246, 150), new PointF(128, 20), new PointF(10, 150)]);
            else { g.FillRectangle(c, 108, 0, 40, 256); g.FillRectangle(c, 0, 90, 256, 40); }
        }
        g.ResetClip();
        using var rim = new Pen(Color.FromArgb(30, 22, 14), 6);
        g.DrawPath(rim, shield);
        b.Save(path, ImageFormat.Png);
    }

    /// <summary>A colourful stand-in for a CK3 map frame (sea, land blobs, an event window panel).</summary>
    private static Bitmap FakeMap(int w, int h)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(b);
        using (var sea = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(40, 80, 110), Color.FromArgb(20, 50, 80), 45f))
            g.FillRectangle(sea, 0, 0, w, h);
        var rnd = new Random(3);
        for (int i = 0; i < 40; i++)
        {
            using var land = new SolidBrush(Color.FromArgb(90 + rnd.Next(60), 110 + rnd.Next(50), 50 + rnd.Next(30)));
            g.FillEllipse(land, rnd.Next(w), rnd.Next(h), w / 10 + rnd.Next(w / 4), h / 8 + rnd.Next(h / 4));
        }
        using var panel = new SolidBrush(Color.FromArgb(230, 60, 45, 35));
        g.FillRectangle(panel, w * 0.3f, h * 0.15f, w * 0.4f, h * 0.65f);
        return b;
    }

    private readonly record struct Stats(double Mean, double StdDev);

    private static Stats LuminanceStats(Bitmap bmp, Rectangle rect)
    {
        var values = Luminances(bmp, rect);
        double mean = values.Average();
        double variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
        return new Stats(mean, Math.Sqrt(variance));
    }

    private static double LuminancePercentile(Bitmap bmp, Rectangle rect, double p)
    {
        var values = Luminances(bmp, rect);
        values.Sort();
        return values[(int)Math.Clamp(p * (values.Count - 1), 0, values.Count - 1)];
    }

    /// <summary>Rec. 709 luma (0-255) of every 2nd pixel in each direction.</summary>
    private static List<double> Luminances(Bitmap bmp, Rectangle rect)
    {
        rect.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] pixels;
        int stride = data.Stride;
        try
        {
            pixels = new byte[stride * rect.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        }
        finally { bmp.UnlockBits(data); }
        var list = new List<double>(rect.Width * rect.Height / 4 + 1);
        for (int y = 0; y < rect.Height; y += 2)
            for (int x = 0, i = y * stride; x < rect.Width; x += 2, i += 8)
                list.Add(0.0722 * pixels[i] + 0.7152 * pixels[i + 1] + 0.2126 * pixels[i + 2]);
        return list;
    }

    private static double ContrastRatio(double luma1, double luma2)
    {
        static double Linear(double v8)
        {
            double c = v8 / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        double l1 = Linear(Math.Max(luma1, luma2)), l2 = Linear(Math.Min(luma1, luma2));
        return (l1 + 0.05) / (l2 + 0.05);
    }
}
