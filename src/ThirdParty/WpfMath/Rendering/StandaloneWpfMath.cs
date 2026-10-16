using System.Windows.Media;
using WpfMath.Fonts;
using XamlMath;
using XamlMath.Fonts;

namespace WpfMath.Rendering;

/// <summary>Explicit font ownership without the application's pack resource lifetime.</summary>
public static class StandaloneWpfMath
{
    /// <summary>Wraps an initialized immutable WPF face for the existing math renderer.</summary>
    public static IFontTypeface WrapTypeface(GlyphTypeface face) => new WpfGlyphTypeface(face);

    /// <summary>Uses a caller-owned font provider while retaining the upstream text font and metrics.</summary>
    public static TexEnvironment CreateEnvironment(IFontProvider fontProvider,TexStyle style,double scale,
        string textFontFamily,Brush? foreground=null,Brush? background=null)
        =>new(style,new DefaultTexFont(fontProvider,scale),new WpfSystemFont(scale,new FontFamily(textFontFamily)),
            background.ToPlatform(),foreground.ToPlatform());
}
