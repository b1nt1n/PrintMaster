using System.Collections.Concurrent;
using System.Collections;
using System.IO;
using System.Resources;
using System.Security.Cryptography;
using System.Windows.Media;
using WpfMath.Parsers;
using WpfMath.Rendering;
using XamlMath;
using XamlMath.Fonts;

namespace PrintMaster.Services;

// Uses the minimal MIT fork at src/ThirdParty/WpfMath, baseline commit
// d6f576345998777dcf7ad112da1add9bd7ad9674 (v2.1.0). The math/text fonts and renderer are upstream code.
// License: Assets/Licenses/WpfMath/LICENSE.md. Embedded font bytes retain their upstream licenses and names.
// The upstream pack:// font loader depends on Application resource lifetime. A file:// font face does not.
internal sealed class MarkdownFontProvider : IFontProvider
{
    private static readonly MarkdownFontProvider Instance=new();
    private static readonly Lazy<IReadOnlyDictionary<string,byte[]>> FontBytes=new(ReadFontBytes,LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentDictionary<string,Lazy<GlyphTypeface>> Faces=new(StringComparer.OrdinalIgnoreCase);
    private MarkdownFontProvider() { }

    public IFontTypeface ReadFontFile(string fontFileName)
        =>StandaloneWpfMath.WrapTypeface(Faces.GetOrAdd(fontFileName,name=>new Lazy<GlyphTypeface>(()=>new GlyphTypeface(new Uri(FontFile(name))),LazyThreadSafetyMode.ExecutionAndPublication)).Value);

    internal static TexEnvironment CreateEnvironment(TexStyle style,double em,string family,Brush foreground)
        =>StandaloneWpfMath.CreateEnvironment(Instance,style,em,family,foreground);

    internal static void Draw(TexFormula formula,DrawingContext context,TexEnvironment environment,double scale)
        =>formula.RenderTo(context,environment,scale);

    internal static ImmutableFontInfo[] FontInfo()=>FontBytes.Value.OrderBy(p=>p.Key,StringComparer.Ordinal)
        .Select(p=>new ImmutableFontInfo(p.Key,p.Value.Length,Convert.ToHexString(SHA256.HashData(p.Value)),FontFile(p.Key))).ToArray();
    internal sealed record ImmutableFontInfo(string Name,int Length,string Sha256,string FilePath);

    private static IReadOnlyDictionary<string,byte[]> ReadFontBytes()
    {
        var assembly=typeof(WpfTeXFormulaParser).Assembly;
        using var resource=assembly.GetManifestResourceStream("WpfMath.g.resources") ?? throw new IOException("В WpfMath отсутствуют встроенные математические шрифты.");
        using var reader=new ResourceReader(resource);var fonts=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach(DictionaryEntry item in reader)
        {
            if(item.Key is not string key || !key.StartsWith("fonts/",StringComparison.OrdinalIgnoreCase) || !key.EndsWith(".ttf",StringComparison.OrdinalIgnoreCase))continue;
            var name=Path.GetFileName(key);using var data=new MemoryStream();
            switch(item.Value)
            {
                case Stream stream:stream.Position=0;stream.CopyTo(data);break;
                case byte[] bytes:data.Write(bytes);break;
                default:throw new IOException("Неизвестный формат встроенного шрифта: "+key);
            }
            fonts.Add(name,data.ToArray());
        }
        if(fonts.Count==0)throw new IOException("Не найдены математические шрифты WpfMath.");return fonts;
    }
    private static string FontFile(string name)
    {
        if(Path.GetFileName(name)!=name || !FontBytes.Value.TryGetValue(name,out var bytes))throw new IOException("Неизвестный математический шрифт: "+name);
        var hash=Convert.ToHexString(SHA256.HashData(bytes));
        var root=Path.Combine(Path.GetTempPath(),"PrintMaster-MathFonts",hash);Directory.CreateDirectory(root);var path=Path.Combine(root,name);
        if(File.Exists(path))
        {
            using var existing=File.OpenRead(path);
            if(existing.Length==bytes.Length && Convert.ToHexString(SHA256.HashData(existing))==hash)return path;
            // Preserve an unexpected occupied file; never replace a font that DirectWrite might already have mapped.
            root=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);path=Path.Combine(root,name);
        }
        var temporary=Path.Combine(root,"."+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))output.Write(bytes);
            try {File.Move(temporary,path);}
            catch(IOException) when(File.Exists(path))
            {
                using var existing=File.OpenRead(path);
                if(existing.Length!=bytes.Length || Convert.ToHexString(SHA256.HashData(existing))!=hash)throw;
            }
            return path;
        }
        finally {if(File.Exists(temporary))File.Delete(temporary);}
    }

}

