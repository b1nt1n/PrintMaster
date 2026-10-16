namespace PrintMaster.Models;

public sealed record SheetChange(SheetSnapshot Before,SheetSnapshot After,string Action);
