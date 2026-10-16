using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using Xunit;

namespace PrintMaster.Tests;

public sealed class RussianQuantityTests
{
    [Theory]
    [InlineData(0,"страниц","оттисков","листов","символов")]
    [InlineData(1,"страница","оттиск","лист","символ")]
    [InlineData(2,"страницы","оттиска","листа","символа")]
    [InlineData(4,"страницы","оттиска","листа","символа")]
    [InlineData(5,"страниц","оттисков","листов","символов")]
    [InlineData(11,"страниц","оттисков","листов","символов")]
    [InlineData(12,"страниц","оттисков","листов","символов")]
    [InlineData(14,"страниц","оттисков","листов","символов")]
    [InlineData(21,"страница","оттиск","лист","символ")]
    [InlineData(22,"страницы","оттиска","листа","символа")]
    [InlineData(25,"страниц","оттисков","листов","символов")]
    [InlineData(101,"страница","оттиск","лист","символ")]
    public void RussianFormsAreUsedInQueuePrintReviewAndPaperGuide(int count,string pages,string impressions,string sheets,string characters)
    {
        Assert.Equal($"{count} {pages}",RussianQuantity.Format(count,"страница","страницы","страниц"));
        Assert.Equal($"{count} {impressions}",RussianQuantity.Format(count,"оттиск","оттиска","оттисков"));
        Assert.Equal($"{count} {sheets}",RussianQuantity.Format(count,"лист","листа","листов"));
        Assert.Equal($"{count} {characters}",RussianQuantity.Format(count,"символ","символа","символов"));
        var all=Enumerable.Range(0,count).Select(_=>PrintPageItem.CreateBlank(SheetPaper.A4)).ToArray();
        using var session=ClipboardTests.CreateVm(new(),new());foreach(var page in all)session.Pages.Add(page);
        Assert.Equal($"{count} {pages} в очереди",session.QueueSummary);
        Assert.Contains($"{count} стр. · {count} отт.",session.PrintLabel);
        using var review=new PrintReviewViewModel(all.Select(p=>p.Snapshot()),new(){PrinterName="PrintMaster quantity fixture"},[]);
        var summary=$"{count} {pages} · {count} {impressions}";Assert.Equal(summary,review.Summary);
        if(count>0)Assert.Contains(summary,session.PrintActionAvailability);
        var settings=new PrintSettings{PrinterName="PrintMaster quantity fixture"}.Snapshot();
        var guide=new PaperGuideViewModel(settings,count,PaperGuideStep.Preview);Assert.EndsWith($"{count} {sheets}",guide.Summary);
    }
}
