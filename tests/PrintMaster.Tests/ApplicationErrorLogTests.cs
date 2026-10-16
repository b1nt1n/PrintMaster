using PrintMaster.Services;
using System.IO;
using Xunit;

namespace PrintMaster.Tests;

public sealed class ApplicationErrorLogTests
{
    private static string DirectoryForTest()=>Path.Combine(Fixtures.Root,"error-log-"+Guid.NewGuid().ToString("N"),"nested");

    [Fact]
    public void MissingDirectoryIsCreatedAndRepeatedErrorsAppendTheirOriginalDetails()
    {
        var directory=DirectoryForTest();Assert.False(Directory.Exists(directory));
        var first=new InvalidOperationException("Исходная ошибка печати",new IOException("Подробности исходника"));
        Assert.True(ApplicationErrorLog.TryAppend(first,directory));
        var path=Path.Combine(directory,"errors.log");var original=File.ReadAllText(path);
        Assert.Contains("System.InvalidOperationException: Исходная ошибка печати",original);Assert.Contains("System.IO.IOException: Подробности исходника",original);
        Assert.True(ApplicationErrorLog.TryAppend(new ArgumentException("Вторая независимая ошибка"),directory));
        var appended=File.ReadAllText(path);Assert.StartsWith(original,appended);Assert.Contains("System.ArgumentException: Вторая независимая ошибка",appended);
    }

    [Fact]
    public void ExclusivelyLockedLogDoesNotThrowOrChangePreviousRecordsThenRecovers()
    {
        var directory=DirectoryForTest();Directory.CreateDirectory(directory);var path=Path.Combine(directory,"errors.log");
        const string previous="Существующая диагностическая запись\n";File.WriteAllText(path,previous);
        using(var locked=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            Assert.False(ApplicationErrorLog.TryAppend(new InvalidOperationException("Ошибка при занятом журнале"),directory));
        Assert.Equal(previous,File.ReadAllText(path));
        Assert.True(ApplicationErrorLog.TryAppend(new InvalidOperationException("Журнал снова доступен"),directory));
        var recovered=File.ReadAllText(path);Assert.StartsWith(previous,recovered);Assert.Contains("Журнал снова доступен",recovered);Assert.DoesNotContain("Ошибка при занятом журнале",recovered);
    }

    [Fact]
    public void FileInPlaceOfLogDirectoryIsPreservedAndLoggingFailureDoesNotEscape()
    {
        var directory=DirectoryForTest();Directory.CreateDirectory(Path.GetDirectoryName(directory)!);File.WriteAllText(directory,"Существующий файл");
        Assert.False(ApplicationErrorLog.TryAppend(new IOException("Нельзя создать папку журнала"),directory));
        Assert.Equal("Существующий файл",File.ReadAllText(directory));
    }

    [Fact]
    public void ExceptionFormattingFailureDoesNotReplaceTheErrorBeingReported()
    {
        var error=new FormattingFailureException();Assert.False(ApplicationErrorLog.TryAppend(error,DirectoryForTest()));
        Assert.Equal("Исходная ошибка",error.Message);
    }

    private sealed class FormattingFailureException : Exception
    {
        public FormattingFailureException():base("Исходная ошибка") { }
        public override string ToString()=>throw new InvalidOperationException("Ошибка формирования журнала");
    }
}
