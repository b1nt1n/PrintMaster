using System.Collections.Immutable;
using System.IO;
using PrintMaster.Models;

namespace PrintMaster.Services;

/// <summary>
/// Clones a queue selection as one graph. A Markdown story and each shared material
/// receive one new identity for the entire selection, rather than one identity per page.
/// </summary>
public static class MarkdownGraphCloner
{
    public static ImmutableArray<ProjectPage> CloneMarkdownPages(IReadOnlyList<ProjectPage> pages)
        => ClonePages(pages);

    public static ImmutableArray<ProjectPage> ClonePages(IReadOnlyList<ProjectPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0) return [];
        if (pages.Count > 20000) throw new InvalidDataException("Для копирования выбрано больше 20 000 страниц.");
        foreach (var page in pages)
        {
            if (page is null || page.Sheet is null || page.Sheet.Id == Guid.Empty || page.Sheet.Assets.IsDefault || page.Sheet.Objects.IsDefault
                || page.Sheet.Assets.Any(a => a is null) || page.Sheet.Objects.Any(o => o is null) || page.Copies is < 1 or > 999)
                throw new InvalidDataException("Данные копируемой страницы повреждены или не загружены.");
            try { page.PrintOptions?.Validate(); _ = SheetDocument.Restore(page.Sheet); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or NullReferenceException)
            { throw new InvalidDataException("Копируемый лист содержит некорректный объект или исходник.", error); }
        }
        if (pages.Select(p => p.Sheet.Id).Distinct().Count() != pages.Count)
            throw new InvalidDataException("Одна и та же страница выбрана для копирования несколько раз.");

        var available = pages.SelectMany(p => p.Sheet.Objects.OfType<MarkdownObject>()).Select(o => o.Document)
            .OfType<MarkdownDocument>().DistinctBy(d => d.Id).ToArray();
        var documents = ProjectFileService.CollectMarkdownDocuments(pages, available);
        foreach (var page in pages) _ = ProjectFileService.HydrateSheet(page.Sheet, documents);

        var oldIds = pages.Select(p => p.Sheet.Id).Concat(pages.SelectMany(p => p.Sheet.Objects).Select(o => o.Id))
            .Concat(pages.SelectMany(p => p.Sheet.Assets).Select(a => a.Id)).Concat(documents.Select(d => d.Id))
            .Concat(pages.Select(p => p.Sheet.NotebookText?.Id).OfType<Guid>())
            .Concat(pages.SelectMany(p => p.Sheet.Objects.OfType<TextObject>()).Select(t => t.Flow?.StoryId).OfType<Guid>())
            .Concat(pages.SelectMany(p => p.Sheet.Objects.OfType<ContentObject>()).SelectMany(c => c.Edits.Annotations).Select(a => a.Id))
            .ToHashSet();
        Guid FreshId()
        {
            Guid id; do { id = Guid.NewGuid(); } while (!oldIds.Add(id)); return id;
        }
        var originals = new Dictionary<Guid, SourceAsset>();
        var assets = new Dictionary<Guid, SourceAsset>();
        foreach (var asset in pages.SelectMany(p => p.Sheet.Assets))
        {
            if (originals.TryGetValue(asset.Id, out var previous))
            {
                if (previous != asset) throw new InvalidDataException("Один ID материала связан с разными исходниками на выбранных страницах.");
                continue;
            }
            originals.Add(asset.Id, asset); assets.Add(asset.Id, asset with { Id = FreshId() });
        }
        var assetMap = assets.ToDictionary(pair => pair.Key, pair => pair.Value.Id);
        var copies = documents.ToDictionary(d => d.Id, d => RemapDocument(d, assetMap, FreshId()));
        var result = ImmutableArray.CreateBuilder<ProjectPage>(pages.Count);
        foreach (var page in pages)
        {
            var sheet = page.Sheet; var objects = ImmutableArray.CreateBuilder<SheetObject>(sheet.Objects.Length);
            var notebook=sheet.NotebookText is {} story ? story with {Id=FreshId()} : null;
            var objectMap = new Dictionary<Guid, Guid>();
            foreach (var item in sheet.Objects)
            {
                var id = FreshId(); objectMap.Add(item.Id, id);
                objects.Add(item switch
                {
                    ContentObject content => content with
                    {
                        Id = id, AssetId = assets[content.AssetId].Id,
                        Edits = content.Edits with
                        {
                            Annotations = content.Edits.Annotations.Select(a => a with
                            { Id = FreshId(), Points = a.Points.ToImmutableArray() }).ToImmutableArray()
                        }
                    },
                    // Ordinary page-flow fragments become standalone blocks. Notebook
                    // fragments remain linked to an independent copy of their local story,
                    // retaining overflow accounting and removal during subsequent edits.
                    TextObject text => text with { Id = id, Flow = text.Flow is {Notebook:true} flow && notebook!=null
                        ? flow with {StoryId=notebook.Id} : null },
                    MarkdownObject markdown => markdown with
                    { Id = id, DocumentId = copies[markdown.DocumentId].Id, Document = copies[markdown.DocumentId] },
                    _ => item with { Id = id }
                });
            }
            var cloned = sheet with
            {
                Id = FreshId(), Assets = sheet.Assets.Select(a => assets[a.Id]).ToImmutableArray(),
                Objects = objects.ToImmutable(),
                NotebookText=notebook,
                PrimaryContentId = sheet.PrimaryContentId is {} primary ? objectMap[primary] : null
            };
            // Final model validation freezes annotation points but retains the shared
            // Markdown definition instances and shared material records.
            cloned = SheetDocument.Restore(cloned).Snapshot();
            _ = ProjectFileService.HydrateSheet(cloned, copies.Values);
            result.Add(page with { Sheet = cloned });
        }
        return result.ToImmutable();
    }

    public static MarkdownDocument RemapDocument(MarkdownDocument document,
        IReadOnlyDictionary<Guid, Guid> assetMap, Guid? newId = null)
    {
        ArgumentNullException.ThrowIfNull(assetMap);
        ProjectFileService.ValidateMarkdownDocument(document);
        if (newId == Guid.Empty) throw new InvalidDataException("У копии Markdown отсутствует идентификатор документа.");
        var required = document.Images.Select(i => i.AssetId).Distinct().ToArray();
        foreach (var id in required)
            if (!assetMap.TryGetValue(id, out var mapped) || mapped == Guid.Empty)
                throw new InvalidDataException("Не найден материал изображения для копии Markdown. Скопируйте документ вместе с его изображениями.");
        if (required.Select(id => assetMap[id]).Distinct().Count() != required.Length)
            throw new InvalidDataException("Разные изображения Markdown не могут ссылаться на один скопированный материал.");
        var idCopy = newId ?? Guid.NewGuid();
        return document with
        {
            Id = idCopy,
            Images = document.Images.Select(image => image with { AssetId = assetMap[image.AssetId] }).ToImmutableArray()
        };
    }
}
