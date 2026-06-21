using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Savedrake.App
{
    // Runtime light/dark theme switching. Every themed brush reference in the styles and controls is a DynamicResource
    // (so it re-resolves when the resource changes — a StaticResource setter, by contrast, caches its value when the
    // style is sealed and would NOT update). Switching themes replaces each brush resource in the merged Dark.xaml
    // dictionary with a fresh SolidColorBrush of the other palette's colour; the DynamicResource references repaint
    // live, with no window reload. Palettes are the LOCKED warm-dark (gold) and light (parchment + bronze) sets; the
    // key insight for light mode is that gold becomes its dark sibling BRONZE for accents/text, with the bright gilt
    // surviving only as the thin header rule (GoldRuleBrush, hardcoded and deliberately left out of the swap).
    public static class ThemeManager
    {
        public static bool IsLight { get; private set; }

        // brushKey -> (dark hex, light hex).
        private static readonly Dictionary<string, (string dark, string light)> Map =
            new Dictionary<string, (string, string)>
            {
                ["ShellBrush"] = ("#1F1811", "#E9E0CB"),
                ["BarBrush"] = ("#241C14", "#E3D8BD"),
                ["CardBrush"] = ("#2A2118", "#F5EFDF"),
                ["CardAltBrush"] = ("#251D15", "#EFE7D2"),
                ["InputBrush"] = ("#1A140C", "#FBF8EE"),
                ["BorderBrush"] = ("#3A3025", "#D9CBA8"),
                ["GoldDimOutlineBrush"] = ("#6B5A33", "#B89B5B"),
                ["RowSepBrush"] = ("#2F261A", "#E3D9BF"),
                ["SelectionBrush"] = ("#3E3223", "#DBCBA0"),
                ["TextBrush"] = ("#E9E4D8", "#2E2A1F"),
                ["TextSecondaryBrush"] = ("#A39B88", "#6E664F"),
                ["TextHintBrush"] = ("#837B69", "#968C6F"),
                ["AccentGoldBrush"] = ("#C8A24C", "#7E5E22"),
                ["AccentTextBrush"] = ("#211A0E", "#F5EFDF"),
                ["AccentDimBrush"] = ("#D3BE86", "#6E5320"),
                ["WordmarkBrush"] = ("#D8B968", "#7E5E22"),
                ["SuccessBrush"] = ("#8FB36A", "#3E6B33"),
                ["DangerBrush"] = ("#C77B6F", "#9A3B2E"),
            };

        public static void Apply(bool light)
        {
            IsLight = light;
            ResourceDictionary app = Application.Current?.Resources;
            if (app == null) return;
            foreach (var kv in Map)
            {
                Color c;
                try { c = (Color)ColorConverter.ConvertFromString(light ? kv.Value.light : kv.Value.dark); }
                catch { continue; }
                SetBrush(app, kv.Key, new SolidColorBrush(c));
            }
        }

        // Replace the brush on whichever merged dictionary owns the key (the brushes live in Themes/Dark.xaml).
        private static void SetBrush(ResourceDictionary root, string key, Brush b)
        {
            foreach (ResourceDictionary md in root.MergedDictionaries)
                if (md.Contains(key)) md[key] = b;
        }

        // The Win32 COLORREF (0x00BBGGRR) of the title-bar/caption colour for the current theme, so DWM tints the
        // window caption to match (dark bar #241C14 or light bar #E3D8BD).
        public static int CaptionColorRef => IsLight ? 0x00BDD8E3 : 0x00141C24;
    }
}
