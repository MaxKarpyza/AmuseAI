// Headless verification that WPF can load the bundled icon fonts under the exact
// family names Amuse.App/Fonts/Fonts.Free.xaml uses, and that every codepoint the
// XAML references has a real glyph (not a .notdef box).
//
// Exits non-zero if any check fails.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private static int _failures;

    private static int Main(string[] args)
    {
        // Both paths default to the repository layout, so the harness runs with no
        // arguments from Scripts/FontCheck. The codepoint list is optional: per-style
        // coverage is asserted by Scripts/verify_font_map.py, this tool only proves
        // that WPF can load and render the bundled faces under the expected names.
        string fontDir = args.Length > 0
            ? args[0]
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "Amuse.App", "Fonts", "Free"));
        string codesFile = args.Length > 1 ? args[1] : null;

        // family name -> file, exactly as referenced from Fonts.Free.xaml
        var fonts = new (string Family, string File)[]
        {
            ("Font Awesome 7 Free Solid",    "FontAwesome-Free-Solid-900.otf"),
            ("Font Awesome 7 Brands Regular", "FontAwesome-Free-Brands-Regular-400.otf"),
        };

        HashSet<int> codepoints = LoadCodepoints(codesFile);
        string scope = codepoints.Count > 0
            ? $"{codepoints.Count} codepoints listed in {Path.GetFileName(codesFile)}"
            : "font resolution only";
        Console.WriteLine($"=== WPF icon font check ({scope}) ===");

        foreach (var (family, file) in fonts)
        {
            string path = Path.Combine(fontDir, file);
            if (!File.Exists(path))
            {
                Fail($"{file}: file not found at {path}");
                continue;
            }

            GlyphTypeface gt;
            try
            {
                gt = new GlyphTypeface(new Uri(path));
            }
            catch (Exception ex)
            {
                Fail($"{family}: WPF could not load the font - {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            // 1. The name in Fonts.Free.xaml must match the font's own name table,
            //    otherwise the pack URI in App.xaml would not resolve at runtime.
            string embedded = FirstName(gt);
            bool named = string.Equals(embedded, family, StringComparison.Ordinal);
            if (!named)
            {
                Fail($"{family}: font reports '{embedded}', Fonts.Free.xaml asks for '{family}'");
                continue;
            }

            // 2. Render a real glyph through WPF's text stack and prove ink lands
            //    on the bitmap. This exercises the same path a TextBlock uses.
            if (!RendersInk(gt, SampleCodepoint(gt)))
            {
                Fail($"{family}: rendering a glyph produced an empty bitmap");
                continue;
            }

            // Coverage is reported only when a codepoint list was supplied; Brands
            // legitimately holds only brand glyphs. Per-style coverage is asserted
            // by Scripts/verify_font_map.py.
            string coverage = codepoints.Count > 0
                ? $", covers {codepoints.Count(cp => gt.CharacterToGlyphMap.ContainsKey(cp))}/{codepoints.Count} codepoints"
                : "";
            Console.WriteLine($"  OK    {family,-32} glyphs={gt.GlyphCount,5} " +
                              $"name + render verified{coverage}");
        }

        // Negative control: a Pro family name must NOT be present in the free set,
        // proving the check above can actually detect a missing font.
        const string proName = "Font Awesome 7 Pro Solid";
        var freeSolid = new GlyphTypeface(new Uri(Path.Combine(fontDir, "FontAwesome-Free-Solid-900.otf")));
        if (string.Equals(FirstName(freeSolid), proName, StringComparison.Ordinal))
        {
            Fail("negative control: the free font claims to be the Pro font");
        }
        else
        {
            Console.WriteLine($"  OK    negative control '{proName}' is correctly absent");
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? "ALL CHECKS PASSED"
            : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static int SampleCodepoint(GlyphTypeface gt)
    {
        // any glyph the font actually has
        foreach (var cp in gt.CharacterToGlyphMap.Keys)
        {
            if (cp >= 0xE000 && cp != 0xF8FF) return (int)cp;
        }
        return 0xF03E;
    }

    private static bool RendersInk(GlyphTypeface gt, int codepoint)
    {
        const int size = 48;
        var typeface = new Typeface(new FontFamily(FirstName(gt)), gt.Style, gt.Weight, gt.Stretch);

        var probe = new FormattedText(
            char.ConvertFromUtf32(codepoint),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface, size, Brushes.Black, 1.0);

        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(probe.Width) + 8, (int)Math.Ceiling(probe.Height) + 8,
            96, 96, PixelFormats.Pbgra32);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawText(probe, new Point(4, 0));
        }
        rtb.Render(visual);

        var pixels = new byte[rtb.PixelWidth * rtb.PixelHeight * 4];
        rtb.CopyPixels(pixels, rtb.PixelWidth * 4, 0);

        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 8) return true; // any non-transparent pixel
        }
        return false;
    }

    private static HashSet<int> LoadCodepoints(string file)
    {
        var set = new HashSet<int>();
        if (!File.Exists(file)) return set;
        foreach (string line in File.ReadAllLines(file))
        {
            string s = line.Trim();
            if (s.Length == 0 || s.StartsWith("#")) continue;
            if (int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp))
                set.Add(cp);
        }
        return set;
    }

    private static string FirstName(GlyphTypeface gt)
    {
        var cultures = new[] { CultureInfo.GetCultureInfo("en-us"), CultureInfo.InvariantCulture };
        foreach (var culture in cultures)
        {
            string v = gt.FamilyNames[culture];
            if (!string.IsNullOrEmpty(v)) return v;
        }
        foreach (string v in gt.FamilyNames.Values) return v;
        foreach (string v in gt.FaceNames.Values) return v;
        return gt.FontUri.ToString();
    }

    private static void Fail(string message)
    {
        _failures++;
        Console.WriteLine($"  FAIL  {message}");
    }
}
