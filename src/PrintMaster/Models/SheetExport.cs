using System.Collections.Immutable;

namespace PrintMaster.Models;

public enum ExportFormat { Png,Pdf }
public enum ExportAppearance { Color,Monochrome }
public enum ExportScope { Current,Highlighted,Included,All }
public sealed record ExportCandidate(PageSnapshot Page,bool Current,bool Highlighted,bool Included);
public sealed record ExportPage(PageSnapshot Page,SheetPaper Paper,RectD SafeAreaMm);
public sealed record ExportOptions(ExportFormat Format,ExportAppearance Appearance,int Dpi,ScaleMode Scaling,
    MonochromeMode Monochrome,int Threshold,bool AllowClipping=false,bool AllowOverflow=false)
{
    public void Validate()
    {
        if(!Enum.IsDefined(Format) || !Enum.IsDefined(Appearance) || !Enum.IsDefined(Scaling) || !Enum.IsDefined(Monochrome)
            || Dpi is not (300 or 600) || Threshold is <0 or >255) throw new ArgumentException("Проверьте формат, вид, DPI и монохром экспорта.");
    }
}
public sealed record ExportDestination(string Path,bool Existed,long Length,DateTime LastWriteUtc);
public sealed record ExportPlan(ImmutableArray<ExportPage> Pages,ExportOptions Options,ImmutableArray<ExportDestination> Files)
{
    public IReadOnlyList<string> ExistingFiles=>Files.Where(f=>f.Existed).Select(f=>f.Path).ToArray();
}
public sealed record ExportProgress(int Completed,int Total,string Message,ExportPhase Phase=ExportPhase.Rendering);
public sealed record ExportResult(IReadOnlyList<string> Files,int Pages);
