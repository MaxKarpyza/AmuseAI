# Icon fonts

Amuse's UI uses Font Awesome 7 icons, referenced by codepoint from `TensorStack.WPF`:

```xml
<CommonControls:FontAwesome Icon="f03e" IconStyle="Light" Size="16" />
```

`App.xaml` merges exactly one icon dictionary from this folder, and the csproj
selects it through `-p:UseFontAwesomePro=`:

| Build | Dictionary | Needs a licence? |
|---|---|---|
| default | `Fonts.Free.xaml` | no |
| `-p:UseFontAwesomePro=true` | `Fonts.Pro.xaml` | yes |

## Default build: Font Awesome Free (CC BY 4.0)

`Free/` contains the three free Font Awesome 7.3.1 faces, already in the repository,
so **a plain `dotnet build` works with no paid assets**:

- `FontAwesome-Free-Solid-900.otf` — serves every style except Brands
- `FontAwesome-Free-Brands-Regular-400.otf` — the Brands style
- `LICENSE.txt` — the Font Awesome Free licence (CC BY 4.0)

The original code pointed all nine style keys at Font Awesome **Pro** font files.
Six of those styles (Light, Thin and the whole Sharp family) exist only in Pro, so
without a Pro licence every icon failed to resolve at runtime.

Font Awesome assigns one codepoint per icon and shares it across families and
styles, so a style can be served from a different weight without changing the glyph.
Free Regular covers only a small legacy subset, so `Fonts.Free.xaml` maps every
non-Brand style to Free Solid. Icons authored in Light/Thin therefore render at
Solid weight - slightly bolder than a Pro build, but complete: all 324 icon usages
in the app resolve.

### Patched font names

Stock Font Awesome declares the family name `Font Awesome 7 Free` in **both**
`Free-Solid-900.otf` and `Free-Regular-400.otf`, and WPF resolves fonts by
name ID 16. Left alone, the two faces are indistinguishable.

`Scripts/patch-font-names.py` sets a unique name ID 16 per file
(`Font Awesome 7 Free Solid`, `Font Awesome 7 Free Regular`,
`Font Awesome 7 Brands Regular`). The names are Font Awesome's own full names for
those faces, and CC BY 4.0 permits modification provided the licence travels with
the files, which it does. The script is idempotent.

### Pro-only icons

27 codepoints the app used exist only in Pro (for example `e2b4` delete,
`e74c` drag-and-drop, `f894` media placeholder). `Scripts/verify_remap.py` holds the
table that maps each to a free equivalent; `Scripts/remap_icons.py` applies it and
was used to rewrite the 53 affected usages across 17 XAML files.

## Opting in to Font Awesome Pro

1. Buy the licence and download the **7.3.1 desktop** package.
2. Copy these nine files into this folder (they are gitignored on purpose):

   ```
   Brands-Regular-400.otf   Light-300.otf     Regular-400.otf
   Sharp-Light-300.otf      Sharp-Regular-400.otf   Sharp-Solid-900.otf
   Sharp-Thin-100.otf       Solid-900.otf      Thin-100.otf
   ```
3. Build with `dotnet build -p:UseFontAwesomePro=true`.

Missing Pro files are ignored rather than fatal, so a half-finished set still
builds - it just falls back to the free rendering for whatever is absent.

## Checking the icon setup

```powershell
# per-style coverage against the real Fonts.Free.xaml mapping (needs fontTools)
python Scripts/verify_font_map.py

# confirms no icon is left on a Pro-only codepoint
python Scripts/audit_icons.py

# proves WPF loads the fonts under the exact names the dictionary uses
cd Scripts/FontCheck; dotnet run -c Release
```
