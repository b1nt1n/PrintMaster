using PrintMaster.Models;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PrintMaster.Tests;

public static class Fixtures
{
    public static string Root
    {
        get
        {
            var directory=new DirectoryInfo(AppContext.BaseDirectory);
            while(directory!=null && !File.Exists(Path.Combine(directory.FullName,"PrintMaster.sln"))) directory=directory.Parent;
            var root=Path.Combine(directory?.FullName ?? Path.GetTempPath(),"artifacts","verification"); Directory.CreateDirectory(root); return root;
        }
    }
    public static string CreatePdf(string name="sample.pdf",int pages=2)
    {
        var path=Path.Combine(Root,name); var objects=new List<string>();
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        var kids=string.Join(" ",Enumerable.Range(0,pages).Select(i=>$"{4+i*2} 0 R")); objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages} >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        for(var i=0;i<pages;i++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595.276 841.89] /Resources << /Font << /F1 3 0 R >> >> /Contents {5+i*2} 0 R >>");
            var content=$"0 0 0 rg BT /F1 32 Tf 48 740 Td (PrintMaster page {i+1}) Tj ET 0 0 0 RG 3 w 48 48 498 660 re S 0.6 g 60 100 180 90 re f";
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream");
        }
        using var stream=File.Create(path); using var writer=new StreamWriter(stream,Encoding.ASCII,leaveOpen:true) { NewLine="\n" };
        writer.Write("%PDF-1.4\n"); writer.Flush(); var offsets=new List<long>();
        for(var i=0;i<objects.Count;i++) { offsets.Add(stream.Position); writer.Write($"{i+1} 0 obj\n{objects[i]}\nendobj\n"); writer.Flush(); }
        var xref=stream.Position; writer.Write($"xref\n0 {objects.Count+1}\n0000000000 65535 f \n");
        foreach(var offset in offsets) writer.Write($"{offset:0000000000} 00000 n \n");
        writer.Write($"trailer\n<< /Size {objects.Count+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); return path;
    }
    public static string CreateImage(string name="sample.png")
    {
        var path=Path.Combine(Root,name); using var image=new Bitmap(800,600,PixelFormat.Format32bppArgb); image.SetResolution(150,150);
        using(var g=Graphics.FromImage(image))
        {
            g.Clear(Color.White);
            for(var x=0;x<800;x++) { using var pen=new Pen(Color.FromArgb(x*255/799,x*255/799,x*255/799)); g.DrawLine(pen,x,0,x,599); }
            g.FillRectangle(Brushes.White,60,60,430,100);
            using var font=new Font("Segoe UI",25); g.DrawString("PrintMaster — 01",font,Brushes.Black,80,80);
            g.FillRectangle(Brushes.DarkSlateBlue,60,380,180,160); g.FillEllipse(Brushes.DarkOrange,480,330,220,210);
        }
        image.Save(path,Path.GetExtension(name).ToLowerInvariant() switch { ".bmp"=>ImageFormat.Bmp,".jpg" or ".jpeg"=>ImageFormat.Jpeg,_=>ImageFormat.Png }); return path;
    }
    public static string CreateDocx()
    {
        var path=Path.Combine(Root,"sample.docx"); using var zip=new ZipArchive(File.Create(path),ZipArchiveMode.Create);
        Write(zip,"[Content_Types].xml","<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Write(zip,"_rels/.rels","<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        Write(zip,"word/document.xml","<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>PrintMaster. Проверка DOCX без MS Office.</w:t></w:r></w:p><w:p><w:r><w:t>Первая страница. Текст и русские символы.</w:t></w:r></w:p><w:p><w:r><w:br w:type=\"page\"/></w:r></w:p><w:p><w:r><w:t>Вторая страница документа.</w:t></w:r></w:p><w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\"/></w:sectPr></w:body></w:document>"); return path;
    }
    private static void Write(ZipArchive zip,string path,string contents) { using var writer=new StreamWriter(zip.CreateEntry(path).Open(),new UTF8Encoding(false)); writer.Write(contents); }
    public static PageSnapshot ImagePage(string path)=>new(Guid.NewGuid(),path,path,SourceKind.Image,0,1000,750,800/150.0*25.4,600/150.0*25.4,0,1,new(new(0,0,1000,750),Array.Empty<Annotation>()));
}
