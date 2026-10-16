namespace PrintMaster.Models;

public enum ImportPhase { Copying,ConvertingDocx,ReadingPdfPages,DecodingImage }
public sealed record ImportProgress(ImportPhase Phase,string Message);

public enum PrintPhase { Probing,Rendering,Queuing,WaitingForPaper,WaitingForTurn }
public enum ExportPhase { Rendering,AssemblingPdf,CommittingFiles }
