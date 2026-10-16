using System.Collections.Immutable;
using PrintMaster.Models;

namespace PrintMaster.Services;

/// <summary>Plans photo placements in paper millimeters without altering the source sheet or files.</summary>
public static class PhotoGridLayout
{
    public static PhotoGridPlan Create(PhotoGridRequest request, PhotoGridOptions options)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(options);
        if (request.Sheet is not {} sheet) throw new ArgumentException("Для раскладки нужен лист.", nameof(request));
        if (options.Rows is < 1 or > 10 || options.Columns is < 1 or > 10 || options.Rows * options.Columns > 100)
            throw new ArgumentException("Выберите от 1 до 10 строк и столбцов, не более 100 ячеек.", nameof(options));
        if (!double.IsFinite(options.GapMm) || options.GapMm < 0 || !double.IsFinite(options.InsetMm) || options.InsetMm < 0)
            throw new ArgumentException("Промежуток и отступ должны быть конечными неотрицательными числами.", nameof(options));
        if (options.Scaling is not (ScaleMode.Fit or ScaleMode.Fill))
            throw new ArgumentException("Для сетки выберите «Вписать» или «Заполнить» без растягивания фотографии.", nameof(options));
        if (sheet.Paper is null || sheet.Assets.IsDefault || sheet.Objects.IsDefault || sheet.Objects.Any(o => o is null) || sheet.Assets.Any(a => a is null))
            throw new ArgumentException("Данные листа или исходников не загружены.", nameof(request));
        sheet.Paper.Validate();
        var safe = request.SafeAreaMm; var paper = sheet.Paper.OrientedSizeMm;
        if (!Finite(safe) || safe.IsEmpty || safe.X < 0 || safe.Y < 0 || safe.Right > paper.Width || safe.Bottom > paper.Height)
            throw new ArgumentException("Печатная область должна находиться целиком внутри выбранного листа.", nameof(request));
        if (request.PhotoIds.IsDefaultOrEmpty)
            throw new ArgumentException("Выберите хотя бы одну фотографию для раскладки.", nameof(request));
        if (request.PhotoIds.Any(id => id == Guid.Empty) || request.PhotoIds.Distinct().Count() != request.PhotoIds.Length)
            throw new ArgumentException("В выборе фотографий есть пустые или повторяющиеся ID.", nameof(request));
        if (sheet.Objects.Select(o => o.Id).Distinct().Count() != sheet.Objects.Length ||
            sheet.Assets.Select(a => a.Id).Distinct().Count() != sheet.Assets.Length)
            throw new ArgumentException("ID объектов и исходников на листе должны быть уникальными.", nameof(request));

        var selectedIds = request.PhotoIds.ToHashSet();
        var selected = sheet.Objects.Where(o => selectedIds.Contains(o.Id)).ToArray();
        if (selected.Length != request.PhotoIds.Length)
            throw new ArgumentException("Одна из выбранных фотографий больше не находится на листе.", nameof(request));
        var photos = selected.Select(item => ValidatePhoto(sheet, item)).ToArray();
        var count = checked(options.Rows * options.Columns);
        if (options.Repeat && photos.Length != 1)
            throw new ArgumentException("Для повтора выберите ровно одну фотографию.", nameof(options));
        if (!options.Repeat && photos.Length > count)
            throw new ArgumentException($"В сетке {count} ячеек, а выбрано {photos.Length} фотографий. Увеличьте сетку: фотографии не будут удалены.", nameof(options));

        var width = (safe.Width - 2 * options.InsetMm - (options.Columns - 1) * options.GapMm) / options.Columns;
        var height = (safe.Height - 2 * options.InsetMm - (options.Rows - 1) * options.GapMm) / options.Rows;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1)
            throw new ArgumentException("Каждая ячейка должна быть не меньше 1 × 1 мм. Уменьшите сетку, промежуток или отступ.", nameof(options));
        var cells = ImmutableArray.CreateBuilder<RectD>(count);
        for (var row = 0; row < options.Rows; row++)
            for (var column = 0; column < options.Columns; column++)
                cells.Add(new(safe.X + options.InsetMm + column * (width + options.GapMm),
                    safe.Y + options.InsetMm + row * (height + options.GapMm), width, height));

        var arranged = ImmutableArray.CreateBuilder<ContentObject>(options.Repeat ? count : photos.Length);
        var allocatedIds = sheet.Objects.Select(o => o.Id).ToHashSet();
        for (var i = 0; i < (options.Repeat ? count : photos.Length); i++)
        {
            var original = photos[options.Repeat ? 0 : i];
            var cell = cells[i]; var edits = original.Edits;
            var bounds = cell;
            if (options.Scaling == ScaleMode.Fit)
                bounds = PageLayout.Place(new SizeD(edits.Crop.Width, edits.Crop.Height).Rotated(edits.QuarterTurns), cell, ScaleMode.Fit);
            else
                edits = CenterFill(edits, sheet.Assets.First(a => a.Id == original.AssetId).LogicalSize, cell);
            var id = original.Id;
            if (options.Repeat && i > 0)
            {
                do { id = Guid.NewGuid(); } while (!allocatedIds.Add(id));
            }
            arranged.Add(original with { Id = id, Placement = new(bounds, 0, PlacementMode.Fixed), Edits = edits });
        }
        return new(cells.ToImmutable(), arranged.ToImmutable(), photos.Select(p => p.Id).ToImmutableArray(), options.Repeat);
    }

    private static ContentObject ValidatePhoto(SheetSnapshot sheet, SheetObject item)
    {
        if (item is not ContentObject content || !content.Visible || content.Locked)
            throw new ArgumentException("Для раскладки подходят только видимые незаблокированные фотографии.", nameof(item));
        var asset = sheet.Assets.FirstOrDefault(a => a.Id == content.AssetId);
        if (asset is null || asset.Kind != SourceKind.Image)
            throw new ArgumentException("У выбранного объекта нет исходной фотографии; страницы PDF и DOCX не поддерживаются в сетке.", nameof(item));
        if (string.IsNullOrWhiteSpace(asset.WorkingPath))
            throw new ArgumentException("У фотографии отсутствует рабочий исходник. Сначала переподключите файл.", nameof(item));
        if (content.Edits is null || content.Edits.Annotations.IsDefault || content.Edits.Annotations.Any(a => a is null || a.Points is null))
            throw new ArgumentException("Правки выбранной фотографии повреждены или не загружены.", nameof(item));
        // Reuse the authoritative model validation without replacing any original records.
        _ = SheetDocument.Restore(new(sheet.Id, sheet.Paper, [asset], [content], content.Id));
        return content;
    }

    private static ContentEdits CenterFill(ContentEdits edits, SizeD source, RectD cell)
    {
        var oriented = CropOrientation.ToOriented(edits.Crop, source, edits.QuarterTurns);
        var ratio = cell.Width / cell.Height; var currentRatio = oriented.Width / oriented.Height;
        if (Math.Abs(currentRatio - ratio) <= 1e-10 * Math.Max(currentRatio, ratio)) return edits;
        RectD crop;
        if (currentRatio > ratio)
        {
            var width = oriented.Height * ratio;
            crop = new(oriented.X + (oriented.Width - width) / 2, oriented.Y, width, oriented.Height);
        }
        else
        {
            var height = oriented.Width / ratio;
            crop = new(oriented.X, oriented.Y + (oriented.Height - height) / 2, oriented.Width, height);
        }
        // Intersection removes only floating-point roundoff at the original crop boundary.
        var original = CropOrientation.ToSource(crop, source, edits.QuarterTurns).Intersect(edits.Crop);
        return edits with { Crop = original };
    }

    private static bool Finite(RectD rectangle) => new[] { rectangle.X, rectangle.Y, rectangle.Width,
        rectangle.Height, rectangle.Right, rectangle.Bottom }.All(double.IsFinite);
}
