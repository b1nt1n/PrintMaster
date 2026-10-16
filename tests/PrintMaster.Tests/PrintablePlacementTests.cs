using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.IO;
using Xunit;

namespace PrintMaster.Tests;

public sealed class PrintablePlacementTests
{
    [Theory]
    [InlineData("top-left", -20, -30)]
    [InlineData("top-right", 120, -30)]
    [InlineData("bottom-left", -20, 170)]
    [InlineData("bottom-right", 120, 170)]
    [InlineData("center-both", 50, 70)]
    [InlineData("left", -20, 0)]
    [InlineData("right", 120, 0)]
    [InlineData("top", 0, -30)]
    [InlineData("bottom", 0, 170)]
    [InlineData("center", 50, 0)]
    [InlineData("middle", 0, 70)]
    [InlineData("unknown", 0, 0)]
    public void Offset_NonzeroPrintableOrigin_AlignsRequestedAxesOnly(string alignment,
        double expectedX, double expectedY)
    {
        var result = PrintableAreaAlignment.Offset(new(25, 37, 60, 80), new(5, 7, 200, 280), alignment);

        Assert.Equal(expectedX, result.X, 10);
        Assert.Equal(expectedY, result.Y, 10);
    }

    [Theory]
    [InlineData("top-left")]
    [InlineData("top-right")]
    [InlineData("bottom-left")]
    [InlineData("bottom-right")]
    [InlineData("center-both")]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("top")]
    [InlineData("bottom")]
    [InlineData("center")]
    [InlineData("middle")]
    public void AlignSheetSelection_RotatedStrokedGroup_MovesExactlyInOneUndo(string alignment)
    {
        using var vm = Session();
        var page = PrintPageItem.CreateBlank(SheetPaper.A4);
        var first = new ShapeObject(Guid.NewGuid(), new(new(35, 45, 40, 20), 31),
            SheetShapeKind.Rectangle, new("#175E9C", 4, .7, "#EEEEEE"));
        var second = new ShapeObject(Guid.NewGuid(), new(new(105, 72, 30, 50), -22),
            SheetShapeKind.Ellipse, new("#0080FF", 3, .4));
        var untouched = new ShapeObject(Guid.NewGuid(), new(new(150, 160, 18, 18)),
            SheetShapeKind.Rectangle, new("#FF8040", 1));
        page.Document.AddObjects([first, second, untouched]);
        vm.AddPage(page);
        vm.SetObjectSelection([first.Id, second.Id]);
        vm.SnapToPrintableArea = false; // Explicit alignment remains available without the magnet.
        var before = page.Document.Snapshot();
        var original = Union(first, second);
        var count = vm.UndoCount;
        var safe = vm.SafeAreaMm;

        Assert.True(vm.AlignSheetSelectionCommand.CanExecute(alignment));
        vm.AlignSheetSelectionCommand.Execute(alignment);

        Assert.Equal(count + 1, vm.UndoCount);
        var updatedFirst = Assert.IsType<ShapeObject>(page.Document.Objects[0]);
        var updatedSecond = Assert.IsType<ShapeObject>(page.Document.Objects[1]);
        var dx = updatedFirst.Placement.BoundsMm.X - first.Placement.BoundsMm.X;
        var dy = updatedFirst.Placement.BoundsMm.Y - first.Placement.BoundsMm.Y;
        Assert.Equal(dx, updatedSecond.Placement.BoundsMm.X - second.Placement.BoundsMm.X, 9);
        Assert.Equal(dy, updatedSecond.Placement.BoundsMm.Y - second.Placement.BoundsMm.Y, 9);
        Assert.Equal(first, updatedFirst with { Placement = updatedFirst.Placement with { BoundsMm = updatedFirst.Placement.BoundsMm with
            { X = first.Placement.BoundsMm.X, Y = first.Placement.BoundsMm.Y } } });
        Assert.Equal(second, updatedSecond with { Placement = updatedSecond.Placement with { BoundsMm = updatedSecond.Placement.BoundsMm with
            { X = second.Placement.BoundsMm.X, Y = second.Placement.BoundsMm.Y } } });
        Assert.Equal(untouched, page.Document.Objects[2]);
        var aligned = Union(updatedFirst, updatedSecond);
        AssertAligned(aligned, original, safe, alignment);
        Assert.False(vm.HasSheetClipping);
        var after = page.Document.Snapshot();

        vm.UndoCommand.Execute(null);
        Assert.Equal(before, page.Document.Snapshot());
        vm.RedoCommand.Execute(null);
        Assert.Equal(after, page.Document.Snapshot());
    }

    [Fact]
    public async Task MagnetAndAlignmentChoiceChanges_DoNotDirtyProjectOrAddUndo()
    {
        using var vm = Session();
        await vm.InitializeWorkspaceAsync();
        var page = PrintPageItem.CreateBlank(SheetPaper.A4);
        page.Document.AddObject(new ShapeObject(Guid.NewGuid(), new(new(30, 40, 25, 20)),
            SheetShapeKind.Rectangle, new()));
        vm.AddPage(page);
        Assert.True(await vm.SaveProjectToAsync(ProjectFileTests.PathFor("pm009-magnet-clean")));
        var project = vm.CaptureProject();
        var undo = vm.UndoCount;
        Assert.False(vm.HasUnsavedChanges);
        Assert.True(vm.SnapToPrintableArea);

        vm.SnapToPrintableArea = false;
        vm.SelectedPlacementAlignment = "bottom-right";

        Assert.False(vm.SavedWorkspace.SnapToPrintableArea);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(undo, vm.UndoCount);
        Assert.Equal(project.Print, vm.CaptureProject().Print);
        Assert.Equal(project.Editor, vm.CaptureProject().Editor);
        Assert.Equal(project.Pages.ToArray(), vm.CaptureProject().Pages.ToArray());

        vm.SnapToPrintableArea = true;
        Assert.True(vm.SavedWorkspace.SnapToPrintableArea);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(undo, vm.UndoCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorkspaceStore_MagnetRoundTrip_PreservesOtherEnvironmentFields(bool enabled)
    {
        var path = WorkspaceStateTests.StatePath();
        var store = new WorkspaceStateStore(path);
        var state = new WorkspaceState
        {
            SnapToPrintableArea = enabled,
            Window = new(-1250, 85, -150, 835, 144, true),
            Panels = new(315, 337, false, true, WorkspacePanel.Inspector, true),
            RecentProjects = [new(ProjectFileTests.PathFor("pm009-recent"), DateTimeOffset.UtcNow)]
        };

        await store.SaveAsync(state, default);
        var restored = await store.LoadAsync(default);

        Assert.Equal(enabled, restored.SnapToPrintableArea);
        Assert.Equal(state.Window, restored.Window);
        Assert.Equal(state.Panels, restored.Panels);
        Assert.Equal(state.RecentProjects.ToArray(), restored.RecentProjects.ToArray());
        using var vm = Session(store);
        await vm.InitializeWorkspaceAsync();
        Assert.Equal(enabled, vm.SnapToPrintableArea);
        Assert.Equal(state.Window, vm.SavedWorkspace.Window);
        Assert.Equal(state.Panels, vm.SavedWorkspace.Panels);
    }

    [Fact]
    public async Task WorkspaceStore_OldJsonMissingMagnet_StartsEnabledWithoutLosingPanels()
    {
        var path = WorkspaceStateTests.StatePath();
        await File.WriteAllTextAsync(path,
            """{"Version":1,"Panels":{"QueueWidth":307,"InspectorWidth":329,"QueueVisible":false,"InspectorVisible":true}}""");
        var store = new WorkspaceStateStore(path);

        var restored = await store.LoadAsync(default);
        using var vm = Session(store);
        await vm.InitializeWorkspaceAsync();

        Assert.True(restored.SnapToPrintableArea);
        Assert.True(vm.SnapToPrintableArea);
        Assert.Equal(307, restored.Panels.QueueWidth);
        Assert.Equal(329, restored.Panels.InspectorWidth);
        Assert.False(restored.Panels.QueueVisible);
        Assert.True(restored.Panels.InspectorVisible);
    }

    [Fact]
    public async Task SaveWorkspaceEnvironment_NewWindowAndMagnet_PreservesRecentProjects()
    {
        var store = new WorkspaceStateStore(WorkspaceStateTests.StatePath());
        var original = new WorkspaceState
        {
            Window = new(60, 70, 1060, 770),
            Panels = new(280, 310),
            RecentProjects = [new(ProjectFileTests.PathFor("pm009-recent-keep"), DateTimeOffset.UtcNow)]
        };
        await store.SaveAsync(original, default);
        using var vm = Session(store);
        await vm.InitializeWorkspaceAsync();
        var window = new SavedWindowPlacement(-1350, 90, -250, 840, 120, true);
        var panels = new WorkspacePanels(327, 341, false, true, WorkspacePanel.Inspector, true);
        vm.SnapToPrintableArea = false;

        Assert.True(await vm.SaveWorkspaceEnvironmentAsync(window, panels));

        var saved = await store.LoadAsync(default);
        Assert.False(saved.SnapToPrintableArea);
        Assert.Equal(window, saved.Window);
        Assert.Equal(panels, saved.Panels);
        Assert.Equal(original.RecentProjects.ToArray(), saved.RecentProjects.ToArray());
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(0, vm.UndoCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpenProject_EditorStateRestoration_DoesNotResetWorkspaceMagnet(bool enabled)
    {
        var path = ProjectFileTests.PathFor("pm009-open-keeps-magnet");
        await WorkspaceStateTests.CreateProjectAsync(path, "Проект не меняет магнит");
        var store = new WorkspaceStateStore(WorkspaceStateTests.StatePath());
        var window = new SavedWindowPlacement(60, 80, 1160, 830);
        var panels = new WorkspacePanels(310, 330, true, false);
        await store.SaveAsync(new WorkspaceState { SnapToPrintableArea = !enabled, Window = window, Panels = panels }, default);
        using var vm = Session(store);
        await vm.InitializeWorkspaceAsync();
        vm.SnapToPrintableArea = enabled;

        Assert.True(await vm.OpenProjectFromAsync(path));

        Assert.Equal(enabled, vm.SnapToPrintableArea);
        Assert.Equal(enabled, vm.SavedWorkspace.SnapToPrintableArea);
        Assert.Equal(window, vm.SavedWorkspace.Window);
        Assert.Equal(panels, vm.SavedWorkspace.Panels);
        Assert.Equal(path, Assert.Single(vm.RecentProjects).Path);
        Assert.Equal(enabled, (await store.LoadAsync(default)).SnapToPrintableArea);
        Assert.Equal("Проект не меняет магнит", Assert.IsType<TextObject>(Assert.Single(vm.Pages).Document.Objects[0]).Text);
    }

    [Fact]
    public void SourcePhoto_MustEnterSheetLayoutBeforePlacement_AndRetainsOriginalEdits()
    {
        using var vm = Session();
        var page = DrawingStyleTests.ImagePage();
        page.Edits.SetCrop(new(60, 50, 650, 500));
        page.RotationQuarterTurns = 1;
        page.Edits.Add(Annotation.Create(AnnotationKind.Arrow, new(100, 100), new(250, 230), "#0080FF", 8));
        page.Edits.Add(Annotation.Create(AnnotationKind.Ink, new(130, 120), new(280, 250), "#175E9C", 4,
            points: [new(130, 120), new(185, 160), new(280, 250)]));
        vm.AddPage(page);
        var source = page.Document.Snapshot();
        var sourcePath = source.PrimaryAsset!.WorkingPath!;
        var sourceBytes = File.ReadAllBytes(sourcePath);
        var history = vm.UndoCount;

        vm.SnapToPrintableArea = false;
        Assert.False(page.IsSheetLayout);
        Assert.False(vm.AlignSheetSelectionCommand.CanExecute("top-left"));
        Assert.Equal(source, page.Document.Snapshot());
        Assert.Equal(history, vm.UndoCount);

        vm.PlaceSourceImageCommand.Execute(null);
        Assert.True(page.IsSheetLayout);
        Assert.Equal(history + 1, vm.UndoCount);
        var placed = page.Document.Snapshot();
        var alignHistory = vm.UndoCount;
        Assert.True(vm.AlignSheetSelectionCommand.CanExecute("top-left"));
        vm.AlignSheetSelectionCommand.Execute("top-left");

        Assert.Equal(alignHistory + 1, vm.UndoCount);
        var content = page.Document.PrimaryContent!;
        Assert.Equal(vm.SafeAreaMm.X, SheetEditing.VisualBounds(content).X, 9);
        Assert.Equal(vm.SafeAreaMm.Y, SheetEditing.VisualBounds(content).Y, 9);
        Assert.Equal(source.PrimaryContent!.Edits.Crop, content.Edits.Crop);
        Assert.Equal(source.PrimaryContent.Edits.QuarterTurns, content.Edits.QuarterTurns);
        Assert.Equal(source.PrimaryContent.Edits.Annotations.Length, content.Edits.Annotations.Length);
        for (var index = 0; index < source.PrimaryContent.Edits.Annotations.Length; index++)
        {
            var expected = source.PrimaryContent.Edits.Annotations[index];
            var actual = content.Edits.Annotations[index];
            Assert.Equal(expected.Points.ToArray(), actual.Points.ToArray());
            // Freezing creates a new immutable points container; every annotation
            // property and every source-space point must still retain its value.
            Assert.Equal(expected, actual with { Points = expected.Points });
        }
        Assert.Equal(source.PrimaryContent.Id, content.Id);
        Assert.Equal(source.PrimaryAsset, page.Document.PrimaryAsset);
        Assert.Equal(sourcePath, page.Document.PrimaryAsset!.WorkingPath);
        Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath));
        Assert.Equal(PlacementMode.Fixed, content.Placement.Mode);
        vm.UndoCommand.Execute(null);
        Assert.Equal(placed, page.Document.Snapshot());
        Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath));
    }

    private static PrintSessionViewModel Session(IWorkspaceStateStore? store = null) =>
        new(new WorkspaceStateTests.Dialogs(),
            Path.Combine(Fixtures.Root, "pm009-recovery-" + Guid.NewGuid().ToString("N")),
            preferences: new WorkspaceStateTests.Preferences(new() { AutosaveEnabled = false }),
            workspaceState: store);

    private static RectD Union(params SheetObject[] objects)
    {
        var bounds = objects.Select(SheetEditing.VisualBounds).ToArray();
        var x = bounds.Min(b => b.X);
        var y = bounds.Min(b => b.Y);
        return new(x, y, bounds.Max(b => b.Right) - x, bounds.Max(b => b.Bottom) - y);
    }

    private static void AssertAligned(RectD actual, RectD original, RectD safe, string alignment)
    {
        if (alignment is "left" or "top-left" or "bottom-left") Assert.Equal(safe.X, actual.X, 9);
        else if (alignment is "right" or "top-right" or "bottom-right") Assert.Equal(safe.Right, actual.Right, 9);
        else if (alignment is "center" or "center-both") Assert.Equal(safe.X + safe.Width / 2, actual.X + actual.Width / 2, 9);
        else Assert.Equal(original.X, actual.X, 9);
        if (alignment is "top" or "top-left" or "top-right") Assert.Equal(safe.Y, actual.Y, 9);
        else if (alignment is "bottom" or "bottom-left" or "bottom-right") Assert.Equal(safe.Bottom, actual.Bottom, 9);
        else if (alignment is "middle" or "center-both") Assert.Equal(safe.Y + safe.Height / 2, actual.Y + actual.Height / 2, 9);
        else Assert.Equal(original.Y, actual.Y, 9);
        Assert.Equal(original.Width, actual.Width, 9);
        Assert.Equal(original.Height, actual.Height, 9);
    }
}
