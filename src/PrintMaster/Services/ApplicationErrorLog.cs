using System.Globalization;
using System.IO;

namespace PrintMaster.Services;

// Diagnostic I/O must never replace the original error with a logging failure.
public static class ApplicationErrorLog
{
    public static bool TryAppend(Exception error,string? directory=null)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(error);
            directory ??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PrintMaster");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory,"errors.log"),DateTimeOffset.Now.ToString("O",CultureInfo.InvariantCulture)+" "+error+Environment.NewLine);
            return true;
        }
        catch(Exception)
        {
            // Best effort only: disk-full, denied access, locks, invalid paths and
            // even exception-formatting failures must leave error reporting usable.
            return false;
        }
    }
}
