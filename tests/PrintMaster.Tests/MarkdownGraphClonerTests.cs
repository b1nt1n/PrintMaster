using System.Collections.Immutable;
using System.IO;
using PrintMaster.Models;
using PrintMaster.Services;
using Xunit;

namespace PrintMaster.Tests;

public sealed class MarkdownGraphClonerTests
{
    private static SourceAsset Asset() => new(Guid.NewGuid(), SourceKind.Image, "original.png", "owned-material.png",
        0, 1, new(1000, 750), new(100, 75));
    private static MarkdownDocument Definition(SourceAsset? asset = null) => new(Guid.NewGuid(),
        "# Задача\n\n$$\\frac{x^2}{2}+C$$\n\n![Фото](photo.png)", MarkdownStyle.Default,
        asset is null ? [] : [new("photo.png", asset.Id), new("same-photo.png", asset.Id)], "Математика");
    private static MarkdownObject Markdown(MarkdownDocument document, int page) =>
        new(Guid.NewGuid(), new(new(0, 0, 210, 297)), document.Id, page) { Document = document };
    private static ImmutableArray<ProjectPage> Pages(MarkdownDocument document, SourceAsset? asset = null)
    {
        ImmutableArray<SourceAsset> assets = asset is null ? [] : [asset];
        return [new(new(Guid.NewGuid(), SheetPaper.A4, assets, [Markdown(document, 0)], null), false, 4),
            new(new(Guid.NewGuid(), SheetPaper.A4, assets, [Markdown(document, 1)], null), true, 2)];
    }
    private static MarkdownObject OnlyMarkdown(ProjectPage page) => Assert.Single(page.Sheet.Objects.OfType<MarkdownObject>());
    private static HashSet<Guid> Identifiers(IEnumerable<ProjectPage> pages) => pages.Select(p => p.Sheet.Id)
        .Concat(pages.SelectMany(p => p.Sheet.Assets).Select(a => a.Id))
        .Concat(pages.SelectMany(p => p.Sheet.Objects).Select(o => o.Id))
        .Concat(pages.SelectMany(p => p.Sheet.Objects.OfType<MarkdownObject>()).Select(o => o.DocumentId))
        .Concat(pages.SelectMany(p => p.Sheet.Objects.OfType<ContentObject>()).SelectMany(c => c.Edits.Annotations).Select(a => a.Id)).ToHashSet();

    [Fact]
    public void ClonePages_TwoLinkedPagesWithSharedImage_UsesOneNewDocumentAndMaterialIdentity()
    {
        var asset = Asset(); var document = Definition(asset); var pages = Pages(document, asset);

        var copied = MarkdownGraphCloner.ClonePages(pages);

        Assert.Equal(2, copied.Length);
        var first = OnlyMarkdown(copied[0]); var second = OnlyMarkdown(copied[1]);
        Assert.NotEqual(document.Id, first.DocumentId); Assert.Equal(first.DocumentId, second.DocumentId);
        Assert.Same(first.Document, second.Document); Assert.NotSame(document, first.Document);
        Assert.Equal(document.Source, first.Document!.Source); Assert.Equal(document.Style, first.Document.Style);
        Assert.Equal(document.Title, first.Document.Title); Assert.Equal(0, first.PageIndex); Assert.Equal(1, second.PageIndex);
        var material = Assert.Single(copied[0].Sheet.Assets);
        Assert.Same(material, Assert.Single(copied[1].Sheet.Assets)); Assert.NotEqual(asset.Id, material.Id);
        Assert.All(first.Document.Images, image => Assert.Equal(material.Id, image.AssetId));
        Assert.Equal(new[] { "photo.png", "same-photo.png" }, first.Document.Images.Select(i => i.Url));
        Assert.Equal(asset with { Id = material.Id }, material);
        Assert.Empty(Identifiers(pages).Intersect(Identifiers(copied)));
        Assert.Equal(new[] { false, true }, copied.Select(p => p.IsIncluded)); Assert.Equal(new[] { 4, 2 }, copied.Select(p => p.Copies));
        Assert.Equal(first.Document, Assert.Single(ProjectFileService.CollectMarkdownDocuments(copied)));
        Assert.Same(document, OnlyMarkdown(pages[0]).Document); Assert.Same(document, OnlyMarkdown(pages[1]).Document);
        Assert.Same(asset, pages[0].Sheet.Assets[0]); Assert.Equal(asset.Id, document.Images[0].AssetId);
    }

    [Fact]
    public void ClonePages_MixedObjects_RemapsPrimaryAndAnnotationsWhilePreservingOrdinaryQueueSemantics()
    {
        var asset = Asset(); var document = Definition(asset); var pages = Pages(document, asset);
        var annotation = Annotation.Create(AnnotationKind.Marker, new(100, 100), new(500, 400), "#123456", 8,
            points: [new(100, 100), new(500, 400)]) with { Opacity = .27, FillColor = "#ABCDEF" };
        var content = new ContentObject(Guid.NewGuid(), new(new(20, 30, 80, 60), 17), asset.Id,
            new(new(10, 20, 900, 650), 1, [annotation]), false, true) { KeepAspectRatio = false };
        var text = new TextObject(Guid.NewGuid(), new(new(10, 15, 100, 20)), "Обычный текст", new(Bold: true),
            Flow: new(Guid.NewGuid(), 0, true));
        var shape = new ShapeObject(Guid.NewGuid(), new(new(80, 90, 15, 20), 13), SheetShapeKind.Ellipse,
            new("#663399", .8, .45, "#ABCDEF"), false, true);
        var ink = new InkObject(Guid.NewGuid(), new(new(30, 70, 40, 20), 37), [new(0, 0), new(1, 1)], new("#175E9C", 2, .3));
        var original = pages[0] with { Sheet = pages[0].Sheet with
        { Objects = [pages[0].Sheet.Objects[0], content, text, shape, ink], PrimaryContentId = content.Id } };

        var copied = MarkdownGraphCloner.CloneMarkdownPages([original, pages[1]]);
        var clonedSheet = copied[0].Sheet;
        var clonedContent = Assert.Single(clonedSheet.Objects.OfType<ContentObject>());
        var clonedText = Assert.Single(clonedSheet.Objects.OfType<TextObject>());
        var clonedShape = Assert.Single(clonedSheet.Objects.OfType<ShapeObject>());
        var clonedInk = Assert.Single(clonedSheet.Objects.OfType<InkObject>());

        Assert.Equal(clonedContent.Id, clonedSheet.PrimaryContentId);
        Assert.Equal(clonedSheet.Assets[0].Id, clonedContent.AssetId);
        Assert.Equal(content with { Id = clonedContent.Id, AssetId = clonedContent.AssetId, Edits = clonedContent.Edits }, clonedContent);
        Assert.Equal(content.Edits.Crop, clonedContent.Edits.Crop); Assert.Equal(1, clonedContent.Edits.QuarterTurns);
        var clonedAnnotation = Assert.Single(clonedContent.Edits.Annotations);
        Assert.NotEqual(annotation.Id, clonedAnnotation.Id);
        Assert.Equal(annotation with { Id = clonedAnnotation.Id, Points = clonedAnnotation.Points }, clonedAnnotation);
        Assert.Equal(annotation.Points.ToArray(), clonedAnnotation.Points.ToArray());
        Assert.Equal(text with { Id = clonedText.Id, Flow = null }, clonedText); Assert.Null(clonedText.Flow);
        Assert.Equal(shape with { Id = clonedShape.Id }, clonedShape); Assert.Equal(ink with { Id = clonedInk.Id }, clonedInk);
        Assert.Equal(original.Sheet.Paper, clonedSheet.Paper);
        Assert.Equal(new[] { typeof(MarkdownObject), typeof(ContentObject), typeof(TextObject), typeof(ShapeObject), typeof(InkObject) },
            clonedSheet.Objects.Select(o => o.GetType()));
        Assert.Empty(Identifiers([original, pages[1]]).Intersect(Identifiers(copied)));
        Assert.Same(content, original.Sheet.Objects[1]); Assert.NotNull(((TextObject)original.Sheet.Objects[2]).Flow);
        Assert.Equal(annotation.Id, content.Edits.Annotations[0].Id);
    }

    [Fact]
    public void ClonePages_SubsetOfStory_RetainsOriginalPageIndexAndGetsIndependentDefinition()
    {
        var asset = Asset(); var document = Definition(asset); var original = Pages(document, asset);
        var copy = Assert.Single(MarkdownGraphCloner.ClonePages([original[1]]));

        var markdown = OnlyMarkdown(copy);
        Assert.Equal(1, markdown.PageIndex); Assert.NotEqual(document.Id, markdown.DocumentId);
        Assert.Equal(document.Source, markdown.Document!.Source);
        Assert.Equal(copy.Sheet.Assets[0].Id, markdown.Document.Images[0].AssetId);
        Assert.Equal(document.Id, OnlyMarkdown(original[0]).DocumentId);
    }

    [Fact]
    public void ClonePages_SeparateBatches_NeverLinkCopiesToEachOtherOrOriginal()
    {
        var asset = Asset(); var document = Definition(asset); var original = Pages(document, asset);
        var first = MarkdownGraphCloner.ClonePages(original); var second = MarkdownGraphCloner.ClonePages(original);
        Assert.Empty(Identifiers(first).Intersect(Identifiers(second)));
        Assert.Empty(Identifiers(original).Intersect(Identifiers(first)));
        Assert.NotSame(OnlyMarkdown(first[0]).Document, OnlyMarkdown(second[0]).Document);
        Assert.Same(OnlyMarkdown(first[0]).Document, OnlyMarkdown(first[1]).Document);
        Assert.Same(OnlyMarkdown(second[0]).Document, OnlyMarkdown(second[1]).Document);
    }

    [Fact]
    public void ClonePages_MultipleIndependentStories_SharingOneMaterialRemainSeparateStories()
    {
        var asset = Asset(); var first = Definition(asset); var second = Definition(asset) with { Source = "Другой исходник" };
        var pages = ImmutableArray.Create(Pages(first, asset)[0], Pages(second, asset)[1]);
        var copied = MarkdownGraphCloner.ClonePages(pages);
        var firstCopy = OnlyMarkdown(copied[0]); var secondCopy = OnlyMarkdown(copied[1]);
        Assert.NotEqual(firstCopy.DocumentId, secondCopy.DocumentId);
        Assert.NotSame(firstCopy.Document, secondCopy.Document);
        Assert.Same(copied[0].Sheet.Assets[0], copied[1].Sheet.Assets[0]);
        Assert.Equal(firstCopy.Document!.Images[0].AssetId, secondCopy.Document!.Images[0].AssetId);
        Assert.Equal("Другой исходник", secondCopy.Document.Source);
    }

    [Fact]
    public void ClonePages_UnhydratedPage_UsesDefinitionFromAnotherPageInSameBatch()
    {
        var asset = Asset(); var document = Definition(asset); var pages = Pages(document, asset);
        var missing = OnlyMarkdown(pages[0]) with { Document = null };
        pages = pages.SetItem(0, pages[0] with { Sheet = pages[0].Sheet with { Objects = [missing] } });
        var copied = MarkdownGraphCloner.ClonePages(pages);
        Assert.NotNull(OnlyMarkdown(copied[0]).Document);
        Assert.Same(OnlyMarkdown(copied[0]).Document, OnlyMarkdown(copied[1]).Document);
        Assert.Null(OnlyMarkdown(pages[0]).Document);
    }

    [Fact]
    public void ClonePages_NoMarkdown_PreservesExistingStandaloneTextAndMissingFileSemantics()
    {
        var asset = Asset() with { WorkingPath = null }; var content = new ContentObject(Guid.NewGuid(),
            ObjectPlacement.Automatic, asset.Id, ContentEdits.Empty(asset.LogicalSize));
        var text = new TextObject(Guid.NewGuid(), new(new(20, 30, 80, 20)), "Продолжение", new(), Flow: new(Guid.NewGuid(), 1, false));
        var sheet = new SheetSnapshot(Guid.NewGuid(), SheetPaper.A4, [asset], [content, text], content.Id);
        var original = new ProjectPage(sheet, false, 7);
        var copied = Assert.Single(MarkdownGraphCloner.ClonePages([original]));
        Assert.Empty(copied.Sheet.Objects.OfType<MarkdownObject>());
        Assert.Null(copied.Sheet.Assets[0].WorkingPath); Assert.NotEqual(asset.Id, copied.Sheet.Assets[0].Id);
        Assert.Null(Assert.Single(copied.Sheet.Objects.OfType<TextObject>()).Flow);
        Assert.Equal(PlacementMode.Automatic, copied.Sheet.PrimaryContent!.Placement.Mode);
        Assert.Equal(7, copied.Copies); Assert.False(copied.IsIncluded);
    }

    [Theory]
    [InlineData(NotebookVariant.GridSingle)] [InlineData(NotebookVariant.GridSpread)]
    [InlineData(NotebookVariant.RuledSingle)] [InlineData(NotebookVariant.RuledSpread)]
    public void ClonePages_NotebookText_PreservesFlowAndGetsIndependentEditableStory(NotebookVariant variant)
    {
        var paper=NotebookProfiles.Default(variant).ToPaper();
        var story=new NotebookTextStory(Guid.NewGuid(),"Первая строка.\nВторая строка.",new());
        var parts=NotebookTextLayout.Layout(paper,story,paper.SafeAreaMm);
        var original=new ProjectPage(new(Guid.NewGuid(),paper,[],parts.Objects.Cast<SheetObject>().ToImmutableArray(),null,story),true,2);

        var copy=Assert.Single(MarkdownGraphCloner.ClonePages([original])).Sheet;

        Assert.NotNull(copy.NotebookText); Assert.NotEqual(story.Id,copy.NotebookText.Id);
        Assert.Equal(story with {Id=copy.NotebookText.Id},copy.NotebookText);
        Assert.Equal(NotebookTextLayout.Remainder(original.Sheet),NotebookTextLayout.Remainder(copy));
        var copiedParts=copy.Objects.OfType<TextObject>().ToArray();
        Assert.Equal(parts.Objects.Length,copiedParts.Length);
        for(var index=0;index<copiedParts.Length;index++)
        {
            var text=copiedParts[index]; var old=parts.Objects[index];
            Assert.NotNull(text.Flow); Assert.Equal(copy.NotebookText.Id,text.Flow.StoryId);
            Assert.Equal(old.Flow! with {StoryId=copy.NotebookText.Id},text.Flow);
            Assert.Equal(old with {Id=text.Id,Flow=text.Flow},text); Assert.NotEqual(old.Id,text.Id);
        }
        // Replacing a copied notebook story must remove its previous fragments rather
        // than leave them behind as detached text underneath the new content.
        var edited=SheetDocument.Restore(copy); var replacement=copy.NotebookText with {Source="Изменённая копия"};
        edited.SetNotebookText(replacement,NotebookTextLayout.Layout(paper,replacement,paper.SafeAreaMm).Objects);
        Assert.Equal(replacement.Source,string.Concat(edited.Objects.OfType<TextObject>().Select(t=>t.Text)));
        Assert.Equal(story,original.Sheet.NotebookText);
        Assert.Equal(story.Source,string.Concat(original.Sheet.Objects.OfType<TextObject>().Select(t=>t.Text)));
    }

    [Fact]
    public void ClonePages_NotebookOverflow_PreservesOnlyTheUnprintedRemainder()
    {
        var paper=NotebookProfiles.Default(NotebookVariant.GridSpread).ToPaper();
        var story=new NotebookTextStory(Guid.NewGuid(),string.Join("\n",Enumerable.Range(1,300).Select(i=>$"Строка {i}")),new());
        var parts=NotebookTextLayout.Layout(paper,story,paper.SafeAreaMm);
        Assert.NotEmpty(parts.Remainder); Assert.True(parts.PrintedCharacters>0);
        var original=new ProjectPage(new(Guid.NewGuid(),paper,[],parts.Objects.Cast<SheetObject>().ToImmutableArray(),null,story),true,1);

        var first=Assert.Single(MarkdownGraphCloner.ClonePages([original])).Sheet;
        var second=Assert.Single(MarkdownGraphCloner.ClonePages([original])).Sheet;

        Assert.Equal(parts.Remainder,NotebookTextLayout.Remainder(first));
        Assert.Equal(parts.Remainder,NotebookTextLayout.Remainder(second));
        Assert.NotEqual(first.NotebookText!.Id,second.NotebookText!.Id);
        Assert.NotEqual(story.Id,first.NotebookText.Id); Assert.NotEqual(story.Id,second.NotebookText.Id);
    }

    [Fact]
    public void ClonePages_EmptyBatch_ReturnsEmptyWithoutInventingDefinitions()
        => Assert.Empty(MarkdownGraphCloner.ClonePages([]));

    [Theory]
    [InlineData("no-definition")] [InlineData("wrong-document-id")] [InlineData("conflicting-definition")]
    [InlineData("missing-image")] [InlineData("wrong-image-kind")] [InlineData("conflicting-material")]
    [InlineData("negative-page")] [InlineData("too-large-page")] [InlineData("invalid-source")]
    public void ClonePages_MalformedMarkdownGraph_RejectsBeforeReturningPartialCopy(string mutation)
    {
        var asset = Asset(); var document = Definition(asset); var pages = Pages(document, asset);
        switch (mutation)
        {
            case "no-definition": pages = pages.Select(p => p with { Sheet = p.Sheet with
                { Objects = [OnlyMarkdown(p) with { Document = null }] } }).ToImmutableArray(); break;
            case "wrong-document-id": pages = pages.SetItem(0, pages[0] with { Sheet = pages[0].Sheet with
                { Objects = [OnlyMarkdown(pages[0]) with { DocumentId = Guid.NewGuid() }] } }); break;
            case "conflicting-definition": pages = pages.SetItem(1, pages[1] with { Sheet = pages[1].Sheet with
                { Objects = [OnlyMarkdown(pages[1]) with { Document = document with { Source = "Повреждённый другой исходник" } }] } }); break;
            case "missing-image": pages = pages.SetItem(1, pages[1] with { Sheet = pages[1].Sheet with { Assets = [] } }); break;
            case "wrong-image-kind": pages = pages.SetItem(1, pages[1] with { Sheet = pages[1].Sheet with
                { Assets = [asset with { Kind = SourceKind.Pdf }] } }); break;
            case "conflicting-material": pages = pages.SetItem(1, pages[1] with { Sheet = pages[1].Sheet with
                { Assets = [asset with { WorkingPath = "another-owned-material.png" }] } }); break;
            case "negative-page": pages = pages.SetItem(0, pages[0] with { Sheet = pages[0].Sheet with
                { Objects = [OnlyMarkdown(pages[0]) with { PageIndex = -1 }] } }); break;
            case "too-large-page": pages = pages.SetItem(0, pages[0] with { Sheet = pages[0].Sheet with
                { Objects = [OnlyMarkdown(pages[0]) with { PageIndex = TextLimits.MaxPages }] } }); break;
            case "invalid-source": pages = pages.Select(p => p with { Sheet = p.Sheet with
                { Objects = [OnlyMarkdown(p) with { Document = document with { Source = null! } }] } }).ToImmutableArray(); break;
        }
        Assert.Throws<InvalidDataException>(() => MarkdownGraphCloner.ClonePages(pages));
    }

    [Fact]
    public void ClonePages_DuplicatePageOrDefaultData_Rejects()
    {
        var document = Definition(); var pages = Pages(document);
        Assert.Throws<InvalidDataException>(() => MarkdownGraphCloner.ClonePages([pages[0], pages[0]]));
        Assert.Throws<InvalidDataException>(() => MarkdownGraphCloner.ClonePages([pages[0] with { Sheet = pages[0].Sheet with { Objects = default } }]));
        Assert.Throws<InvalidDataException>(() => MarkdownGraphCloner.ClonePages([pages[0] with { Copies = 0 }]));
        Assert.Throws<InvalidDataException>(() => MarkdownGraphCloner.ClonePages([pages[0] with { Sheet = pages[0].Sheet with { Assets = default } }]));
    }

    [Fact]
    public void RemapDocument_AllMaterials_MapDuplicateUrlsForSharedAssetAndKeepOriginalIntact()
    {
        var first = Asset(); var second = Asset(); var document = Definition(first) with
        { Images = [new("one.png", first.Id), new("same.png", first.Id), new("two.png", second.Id)] };
        var firstId = Guid.NewGuid(); var secondId = Guid.NewGuid(); var documentId = Guid.NewGuid();
        var copy = MarkdownGraphCloner.RemapDocument(document,
            new Dictionary<Guid, Guid> { [first.Id] = firstId, [second.Id] = secondId }, documentId);
        Assert.Equal(documentId, copy.Id);
        Assert.Equal(new[] { firstId, firstId, secondId }, copy.Images.Select(i => i.AssetId));
        Assert.Equal(new[] { "one.png", "same.png", "two.png" }, copy.Images.Select(i => i.Url));
        Assert.Equal(document.Source, copy.Source); Assert.Equal(document.Style, copy.Style); Assert.Equal(document.Title, copy.Title);
        Assert.Equal(new[] { first.Id, first.Id, second.Id }, document.Images.Select(i => i.AssetId));
        Assert.NotSame(document, copy);
    }

    [Fact]
    public void RemapDocument_NoImages_GeneratesIndependentDocumentIdentity()
    {
        var document = Definition(); var copy = MarkdownGraphCloner.RemapDocument(document, new Dictionary<Guid, Guid>());
        Assert.NotEqual(document.Id, copy.Id); Assert.Empty(copy.Images);
        Assert.Equal(document.Source, copy.Source); Assert.Equal(document.Style, copy.Style);
    }

    [Theory]
    [InlineData("missing")] [InlineData("empty")] [InlineData("alias")] [InlineData("document-id")]
    public void RemapDocument_MissingOrAmbiguousMaterialMapping_Rejects(string mutation)
    {
        var first = Asset(); var second = Asset(); var document = Definition(first) with
        { Images = [new("one.png", first.Id), new("two.png", second.Id)] };
        var map = new Dictionary<Guid, Guid> { [first.Id] = Guid.NewGuid(), [second.Id] = Guid.NewGuid() };
        switch (mutation)
        {
            case "missing": map.Remove(second.Id); break;
            case "empty": map[second.Id] = Guid.Empty; break;
            case "alias": map[second.Id] = map[first.Id]; break;
        }
        Assert.Throws<InvalidDataException>(() => MarkdownGraphCloner.RemapDocument(document, map,
            mutation == "document-id" ? Guid.Empty : null));
    }
}
