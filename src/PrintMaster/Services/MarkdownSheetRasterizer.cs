using PrintMaster.Models;
using System.Drawing;

namespace PrintMaster.Services;

public sealed partial class PageRasterizer
{
    private static void DrawMarkdownObject(Graphics graphics,SheetSnapshot sheet,MarkdownObject item,RectD bounds,CancellationToken token)
    {
        var definition=item.Document ?? throw new InvalidOperationException("Исходный текст Markdown отсутствует. Откройте проект снова.");
        if(definition.LayoutEngineVersion!=MarkdownDocument.CurrentLayoutEngineVersion)
            throw new InvalidOperationException(MarkdownDocument.LayoutUpgradeMessage);
        var layout=MarkdownLayoutService.Shared.Layout(definition,sheet.Paper,sheet.Assets,token);
        if(item.PageIndex>=layout.Pages.Length)throw new InvalidOperationException("Разметка Markdown изменилась. Откройте его редактор и примените новые страницы.");
        var page=layout.Pages[item.PageIndex];var safe=sheet.Paper.SafeAreaMm;
        graphics.TranslateTransform((float)bounds.X,(float)bounds.Y);
        graphics.ScaleTransform((float)(bounds.Width/safe.Width),(float)(bounds.Height/safe.Height));
        graphics.TranslateTransform((float)-safe.X,(float)-safe.Y);
        graphics.ScaleTransform(25.4f/96,25.4f/96);
        MarkdownDrawingRenderer.Draw(graphics,page,token);
    }
}
