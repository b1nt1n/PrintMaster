using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using Xunit;

namespace PrintMaster.Tests;

public sealed class MarkdownClippingTests
{
    [Fact]
    public void DefaultMarkdownMarginsAvoidFalseConsentFromQuantizedHpPrintableArea()
    {
        var page=Page(SheetPaper.A4,MarkdownStyle.Default);
        var safe=PageLayout.PrintSafeArea(new(210,297),new(4,4,202,289),5);
        Assert.Equal(5.08,safe.X,8); Assert.Equal(5.08,safe.Y,8);
        var markdown=Assert.IsType<MarkdownObject>(Assert.Single(page.GetSheet().Objects));
        Assert.True(SheetEditing.Outside(markdown,safe)); // Its empty reference border reaches 5 mm.
        Assert.False(SheetEditing.Outside(markdown,safe,page.GetSheet().Paper));
        Assert.False(SheetEditing.HasClipping(page,safe));
        using var review=new PrintReviewViewModel([page],new() {PrinterName="HP LaserJet Professional P1102"},[]);
        Assert.Empty(review.Error); Assert.False(review.AllowCropping); Assert.True(review.TryAccept(false));
    }

    [Fact]
    public void ZeroMarkdownMarginsStillRequireConsentAtActualPrintedAreaBoundary()
    {
        var page=Page(SheetPaper.A4,MarkdownStyle.Default with {Margins=new(0,0,0,0)});
        var safe=PageLayout.PrintSafeArea(new(210,297),new(4,4,202,289),5);
        Assert.True(SheetEditing.HasClipping(page,safe));
        Assert.False(SheetEditing.HasClipping(page,page.GetSheet().Paper.SafeAreaMm));
        using var review=new PrintReviewViewModel([page],new() {PrinterName="HP LaserJet Professional P1102"},[]);
        Assert.True(review.AllowCropping); Assert.False(review.TryAccept(false)); Assert.True(review.TryAccept(true));
    }

    [Fact]
    public void MovingOrRotatingTheMarkdownContentOutsideStillRequestsConsent()
    {
        var original=Page(SheetPaper.A4,MarkdownStyle.Default);
        var sheet=original.GetSheet(); var markdown=(MarkdownObject)sheet.Objects[0];
        var safe=PageLayout.PrintSafeArea(new(210,297),new(4,4,202,289),5);
        var moved=markdown with {Placement=markdown.Placement with {BoundsMm=markdown.Placement.BoundsMm with {X=-1}}};
        Assert.True(SheetEditing.HasClipping(original with {Sheet=sheet with {Objects=[moved]}},safe));
        var rotated=markdown with {Placement=markdown.Placement with {RotationDegrees=90}};
        Assert.True(SheetEditing.HasClipping(original with {Sheet=sheet with {Objects=[rotated]}},safe));
        var resized=markdown with {Placement=markdown.Placement with {BoundsMm=markdown.Placement.BoundsMm with {Width=240}}};
        Assert.True(SheetEditing.HasClipping(original with {Sheet=sheet with {Objects=[resized]}},safe));
        Assert.False(SheetEditing.HasClipping(original with {Sheet=sheet with {Objects=[moved with {Visible=false}]}},safe));
    }

    [Theory]
    [InlineData(NotebookVariant.GridSingle,false)] [InlineData(NotebookVariant.GridSingle,true)]
    [InlineData(NotebookVariant.RuledSingle,false)] [InlineData(NotebookVariant.RuledSingle,true)]
    [InlineData(NotebookVariant.GridSpread,false)] [InlineData(NotebookVariant.GridSpread,true)]
    [InlineData(NotebookVariant.RuledSpread,false)] [InlineData(NotebookVariant.RuledSpread,true)]
    public void NotebookColumnsAndBothRotationsUseTheSamePhysicalWritingRegions(NotebookVariant variant,bool landscape)
    {
        var paper=NotebookProfiles.Default(variant).ToPaper(landscape:landscape);
        var page=Page(paper,MarkdownStyle.Default); var size=paper.OrientedSizeMm;
        var safe=PageLayout.PrintSafeArea(size,new(4,4,size.Width-8,size.Height-8),5);
        Assert.False(SheetEditing.HasClipping(page,safe));
    }

    [Fact]
    public void NotebookRedFieldCannotCauseFalseClippingOnAnOtherwiseEmptyContainerEdge()
    {
        var profile=NotebookProfiles.Default(NotebookVariant.GridSingle) with {SingleMarginOnRight=false,LeftMarginMm=20};
        var paper=profile.ToPaper(); var page=Page(paper,MarkdownStyle.Default with {Margins=new(0,10,10,10)});
        var safe=new RectD(6,5.08,156,197.84);
        Assert.False(SheetEditing.HasClipping(page,safe)); // Text starts after the 20 mm red field.
        var rightField=profile with {SingleMarginOnRight=true};
        Assert.True(SheetEditing.HasClipping(Page(rightField.ToPaper(),MarkdownStyle.Default with {Margins=new(0,10,10,10)}),safe));
    }

    private static PageSnapshot Page(SheetPaper paper,MarkdownStyle style)
    {
        var page=PrintPageItem.CreateBlank(paper);
        var document=new MarkdownDocument(Guid.NewGuid(),"# Проверка\n\n$x^2$",style,[]);
        page.Document.AddObject(new MarkdownObject(Guid.NewGuid(),new(paper.SafeAreaMm),document.Id,0) {Document=document});
        return page.Snapshot();
    }
}
