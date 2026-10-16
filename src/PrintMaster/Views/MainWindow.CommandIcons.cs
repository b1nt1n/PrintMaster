using PrintMaster.Controls;

namespace PrintMaster.Views;

public partial class MainWindow
{
    private static LucideIcon? CommandIcon(string key)
    {
        var name=key switch
        {
            "Include"=>"check", "Exclude"=>"x", "Copies"=>"copy", "PageOptions" or "Properties"=>"settings",
            "Rotate"=>"rotate-cw", "Move" or "MoveUp"=>"chevron-up", "MoveDown"=>"chevron-down",
            "Cut"=>"scissors", "Copy" or "Duplicate"=>"copy", "Paste"=>"clipboard-paste", "Delete"=>"trash-2",
            "AddFiles"=>"folder-open", "CreateSheet"=>"file-plus-2", "PhotoGrid"=>"layout-grid",
            "EditText" or "AddText" or "ImageCaptions"=>"type", "Crop"=>"crop", "PlaceImage" or "AddImage"=>"image",
            "EditMode"=>"pencil", "Fit" or "Align"=>"maximize", "SelectUnder"=>"mouse-pointer-2", "Order"=>"copy",
            _ when key.StartsWith("Align_",StringComparison.Ordinal)=>"maximize",
            "OrderBackward" or "OrderBack"=>"chevron-down",
            _ when key.StartsWith("Order",StringComparison.Ordinal)=>"chevron-up",
            _ when key.StartsWith("Select_",StringComparison.Ordinal)=>"mouse-pointer-2",
            _ when key.StartsWith("Caption_",StringComparison.Ordinal)=>"type",
            _=>null
        };
        return name==null ? null : new LucideIcon {Icon=name,Width=16,Height=16};
    }
}
