using PrintMaster.Services;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace PrintMaster.Tests;

public sealed class MarkdownFontLifecycleTests
{
    [Theory]
    [InlineData("cold")]
    [InlineData("compare")]
    public async Task FreshChildFontsWorkAfterApplicationShutdownAndMatchUpstreamMetrics(string mode)
    {
        var folder=new DirectoryInfo(AppContext.BaseDirectory);
        while(folder!=null && !File.Exists(Path.Combine(folder.FullName,"PrintMaster.sln")))folder=folder.Parent;
        Assert.NotNull(folder);var configuration=AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar+"Debug"+Path.DirectorySeparatorChar,StringComparison.Ordinal) ? "Debug" : "Release";
        var executable=Path.Combine(folder!.FullName,"tests","PrintMaster.FontLifecycle","bin","x64",configuration,"net8.0-windows","win-x64","PrintMaster.FontLifecycle.exe");
        Assert.True(File.Exists(executable),"Build-only FontLifecycle project must be built before tests: "+executable);
        var report=Path.Combine(Path.GetTempPath(),"PrintMaster-font-lifecycle-"+Guid.NewGuid().ToString("N")+".json");
        try
        {
            var start=new ProcessStartInfo(executable) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add(mode);start.ArgumentList.Add(report);
            using var process=Process.Start(start)!;var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try {await process.WaitForExitAsync(timeout.Token);}catch {if(!process.HasExited)process.Kill(true);throw;}
            var stdout=await output;var stderr=await error;Assert.True(process.ExitCode==0,$"Child exit {process.ExitCode}: {stderr}\n{stdout}");
            var json=JsonNode.Parse(await File.ReadAllTextAsync(report))!;Assert.True(json["Success"]!.GetValue<bool>());Assert.True(json["ApplicationShutdown"]!.GetValue<bool>());
            Assert.Equal(0,json["VisibleWindows"]!.GetValue<int>());Assert.Equal(0,json["PhysicalPrints"]!.GetValue<int>());
            Assert.Equal(5,json["Production"]!.AsArray().Count);Assert.True(json["MetricsMatch"]!.GetValue<bool>());
            if(mode=="cold")Assert.True(json["ColdProductionFonts"]!.GetValue<bool>());
            Assert.All(json["Production"]!.AsArray(),m=>{Assert.True(m!["Frozen"]!.GetValue<bool>());Assert.True(m["Width"]!.GetValue<double>()>20);Assert.True(m["Height"]!.GetValue<double>()>5);});
        }
        finally {if(File.Exists(report))File.Delete(report);}
    }
    [Fact]
    public void PhysicalCacheFontBytesRemainExactAndUseOriginalNames()
    {
        var info=MarkdownFontProvider.FontInfo();Assert.Equal(5,info.Length);
        Assert.Equal(new[]{"cmex10.ttf","cmmi10.ttf","cmr10.ttf","cmsy10.ttf","jlm_msam10.ttf"},info.Select(i=>i.Name));
        foreach(var font in info)
        {
            Assert.Equal(font.Name,Path.GetFileName(font.FilePath));using var input=File.OpenRead(font.FilePath);
            Assert.Equal(font.Length,input.Length);Assert.Equal(font.Sha256,Convert.ToHexString(SHA256.HashData(input)));
        }
    }
}
