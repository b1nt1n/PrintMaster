namespace PrintMaster.Models;

public enum SessionOperationKind { None,Import,InsertImages,ClipboardRead,ClipboardCopy,ClipboardInsert,OpenProject,SaveProject,SaveImage,Export,Print,Relink,SavePreferences }
public sealed record OperationReport(string Message,int Completed=0,int? Total=null,string? Phase=null);
