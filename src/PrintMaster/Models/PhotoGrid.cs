using System.Collections.Immutable;

namespace PrintMaster.Models;

public sealed record PhotoGridOptions(int Rows = 2, int Columns = 2, double GapMm = 3,
    double InsetMm = 0, ScaleMode Scaling = ScaleMode.Fit, bool Repeat = false);

public sealed record PhotoGridRequest(SheetSnapshot Sheet, ImmutableArray<Guid> PhotoIds,
    RectD SafeAreaMm);

public sealed record PhotoGridPlan(ImmutableArray<RectD> Cells, ImmutableArray<ContentObject> Photos,
    ImmutableArray<Guid> OriginalPhotoIds, bool Repeat);
