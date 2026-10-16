using PrintMaster.Models;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrintMaster.Services;

public sealed class ProjectFileService
{
    public const string Extension=".printmaster";
    public const int CurrentVersion=10;
    public const long MaxResourceBytes=512L*1024*1024;
    private const long MaxManifestBytes=32L*1024*1024,MaxTotalBytes=4L*1024*1024*1024;
    private static readonly JsonSerializerOptions Json=new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase,
        WriteIndented=true,IgnoreReadOnlyProperties=true,MaxDepth=64,Converters={new JsonStringEnumConverter()} };
    private readonly SemaphoreSlim _writeGate=new(1,1);

    public async Task SaveAsync(ProjectSession session,string path,bool includeSources,CancellationToken token)
    {
        if(session==null || session.Pages.IsDefault || session.MarkdownDocuments.IsDefault) throw new InvalidDataException("Недопустимое состояние проекта.");
        session=session with {MarkdownDocuments=CollectMarkdownDocuments(session.Pages,session.MarkdownDocuments)};
        ValidateSession(session); path=Path.GetFullPath(path);
        foreach(var source in session.Pages.SelectMany(p=>p.Sheet.Assets))
            if(SamePath(path,source.WorkingPath) || SamePath(path,source.OriginalPath)) throw new IOException("Проект нельзя сохранять поверх исходного материала.");
        await _writeGate.WaitAsync(token).ConfigureAwait(false);
        try { await Task.Run(()=>WriteProject(session,path,includeSources,token),token).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }
    private static bool SamePath(string left,string? right)=>!string.IsNullOrEmpty(right) && string.Equals(left,Path.GetFullPath(right),StringComparison.OrdinalIgnoreCase);

    private static void WriteProject(ProjectSession session,string path,bool includeSources,CancellationToken token)
    {
        var directory=Path.GetDirectoryName(path)!; Directory.CreateDirectory(directory);
        var temporary=Path.Combine(directory,"."+Path.GetFileName(path)+"."+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
            {
                using(var archive=new ZipArchive(file,ZipArchiveMode.Create,true))
                {
                    var resources=new Dictionary<string,ProjectResource>(StringComparer.Ordinal);
                    var byFile=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                    var links=ImmutableArray.CreateBuilder<ProjectAssetLink>(); long total=0;
                    foreach(var page in session.Pages)
                    foreach(var source in page.Sheet.Assets)
                    {
                        token.ThrowIfCancellationRequested();
                        var working=source.WorkingPath;
                        string key;
                        if(string.IsNullOrEmpty(working) || !File.Exists(working))
                        {
                            key="missing-"+source.Id.ToString("N");
                            resources.TryAdd(key,new(key,SourceExtension(source),null,source.OriginalPath,null,0));
                        }
                        else if(!byFile.TryGetValue(Path.GetFullPath(working),out key!))
                        {
                            var length=new FileInfo(working).Length;
                            if(length<=0 || length>MaxResourceBytes || total+length>MaxTotalBytes) throw new IOException("Материалы проекта превышают допустимый размер.");
                            var hash=Hash(working,token); var extension=SourceExtension(source); key=hash+extension;
                            if(!resources.ContainsKey(key))
                            {
                                total+=length; var reference=source.Kind!=SourceKind.Docx && File.Exists(source.OriginalPath) && Hash(source.OriginalPath!,token)==hash ? Path.GetFullPath(source.OriginalPath!) : null;
                                var entry=includeSources || reference==null ? "assets/"+key : null;
                                var resource=new ProjectResource(key,extension,entry,reference ?? source.OriginalPath,hash,length); resources.Add(key,resource);
                                if(entry!=null)
                                {
                                    using var input=File.OpenRead(working); using var output=archive.CreateEntry(entry,CompressionLevel.Fastest).Open();
                                    CopyLimited(input,output,length,token);
                                }
                            }
                            byFile[Path.GetFullPath(working)]=key;
                        }
                        links.Add(new(page.Sheet.Id,source.Id,key));
                    }
                    // No ephemeral working paths are written into the manifest.
                    var persisted=session with { Pages=session.Pages.Select(p=>p with { Sheet=p.Sheet with {
                        Assets=p.Sheet.Assets.Select(a=>a with { WorkingPath=null }).ToImmutableArray() } }).ToImmutableArray() };
                    var manifest=new ProjectManifest(CurrentVersion,"PrintMaster",DateTimeOffset.UtcNow,persisted,resources.Values.ToImmutableArray(),links.ToImmutable());
                    var json=JsonSerializer.SerializeToUtf8Bytes(manifest,Json);
                    if(json.Length>MaxManifestBytes) throw new IOException("Описание проекта слишком большое.");
                    using var metadata=archive.CreateEntry("manifest.json",CompressionLevel.Optimal).Open(); metadata.Write(json);
                }
                token.ThrowIfCancellationRequested(); file.Flush(true);
            }
            token.ThrowIfCancellationRequested(); File.Move(temporary,path,true);
        }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }

    public Task<LoadedProject> LoadAsync(string path,SessionWorkspace workspace,CancellationToken token,
        IReadOnlyDictionary<string,string>? replacements=null,bool embeddedOnly=false,long? materialLimit=null)
        =>Task.Run(()=>ReadProject(path,workspace,token,replacements,embeddedOnly,materialLimit ?? MaxTotalBytes),token);

    public Task<SourceAsset> RelinkAsync(SourceAsset asset,string replacement,SessionWorkspace workspace,CancellationToken token)=>Task.Run(()=>
    {
        var extension=SourceExtension(asset); if(asset.Kind==SourceKind.Image) extension=(Path.GetExtension(replacement) ?? "").ToLowerInvariant();
        if(!new[] { ".png",".jpg",".jpeg",".bmp",".pdf" }.Contains(extension)) throw new InvalidDataException("Формат замены не поддерживается.");
        var length=new FileInfo(replacement).Length; if(length<=0 || length>MaxResourceBytes) throw new InvalidDataException("Недопустимый размер файла замены.");
        ValidateMaterial(replacement,extension); ValidateSourcePage(asset,replacement);
        var copy=Path.Combine(workspace.NewImportDirectory(),"source"+extension);
        using(var input=File.OpenRead(replacement)) using(var output=File.Create(copy)) CopyLimited(input,output,length,token);
        return asset with { WorkingPath=copy,OriginalPath=asset.Kind==SourceKind.Docx ? asset.OriginalPath : Path.GetFullPath(replacement) };
    },token);

    private static LoadedProject ReadProject(string path,SessionWorkspace workspace,CancellationToken token,IReadOnlyDictionary<string,string>? replacements,bool embeddedOnly,long materialLimit)
    {
        path=Path.GetFullPath(path); using var file=File.OpenRead(path); using var archive=new ZipArchive(file,ZipArchiveMode.Read);
        if(archive.Entries.Count>30000) throw new InvalidDataException("Слишком много файлов в проекте.");
        var entry=archive.GetEntry("manifest.json") ?? throw new InvalidDataException("В проекте нет manifest.json.");
        if(entry.Length<=0 || entry.Length>MaxManifestBytes) throw new InvalidDataException("Недопустимый размер описания проекта.");
        ProjectManifest manifest;
        using(var input=entry.Open()) using(var data=new MemoryStream())
        {
            CopyLimited(input,data,entry.Length,token);
            try
            {
                using var json=JsonDocument.Parse(data.ToArray(),new JsonDocumentOptions {MaxDepth=64});
                manifest=json.RootElement.Deserialize<ProjectManifest>(Json) ?? throw new InvalidDataException("Пустое описание проекта.");
                // Schema 9 must carry an explicit generation. A missing property must
                // never inherit the constructor default and silently authorize old page indices.
                if(manifest.Version>=9 && json.RootElement.TryGetProperty("session",out var session)
                    && session.TryGetProperty("markdownDocuments",out var documents) && documents.ValueKind==JsonValueKind.Array)
                    foreach(var document in documents.EnumerateArray())
                        if(document.ValueKind!=JsonValueKind.Object || !document.TryGetProperty("layoutEngineVersion",out var version)
                            || version.ValueKind!=JsonValueKind.Number || !version.TryGetInt32(out var value)
                            || value is not (1 or 2 or MarkdownDocument.CurrentLayoutEngineVersion)
                            || manifest.Version==9 && value>2)
                            throw new InvalidDataException("В проекте отсутствует или повреждена версия верстки Markdown.");
            }
            catch(JsonException ex) { throw new InvalidDataException("Поврежденное или несовместимое описание проекта.",ex); }
        }
        if(manifest.Format!="PrintMaster" || manifest.Version is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or CurrentVersion)) throw new InvalidDataException("Эта версия формата проекта не поддерживается.");
        if(manifest.Version<7 && (!manifest.Session.MarkdownDocuments.IsDefaultOrEmpty || manifest.Session.Pages.Any(p=>p.Sheet.Objects.OfType<MarkdownObject>().Any())))
            throw new InvalidDataException("Markdown требует версии проекта 7.");
        if(manifest.Version<8 && manifest.Session.Pages.Any(p=>p.PrintOptions!=null))
            throw new InvalidDataException("Настройки отдельных страниц требуют версии проекта 8.");
        if(manifest.Version<9 && !manifest.Session.MarkdownDocuments.IsDefaultOrEmpty)
            manifest=manifest with {Session=manifest.Session with {MarkdownDocuments=manifest.Session.MarkdownDocuments
                .Select(document=>document is null ? throw new InvalidDataException("Недопустимый исходник Markdown.") : document with {LayoutEngineVersion=1}).ToImmutableArray()}};
        ValidateSession(manifest.Session);
        if(manifest.Resources.IsDefault || manifest.Links.IsDefault || manifest.Resources.Length>20000 || manifest.Links.Length>40000)
            throw new InvalidDataException("Недопустимый список материалов.");
        if(manifest.Resources.Select(r=>r.Id).Distinct().Count()!=manifest.Resources.Length || manifest.Links.Select(l=>(l.SheetId,l.AssetId)).Distinct().Count()!=manifest.Links.Length)
            throw new InvalidDataException("Повторяющиеся идентификаторы материалов.");
        var restored=new Dictionary<string,string?>(StringComparer.Ordinal); var resourceDirectory=workspace.NewImportDirectory(); long total=0;
        foreach(var resource in manifest.Resources)
        {
            token.ThrowIfCancellationRequested(); ValidateResource(resource); total+=resource.Length;
            if(total>Math.Min(MaxTotalBytes,materialLimit)) throw new InvalidDataException("Материалы проекта слишком большие.");
            if(embeddedOnly && (resource.ArchiveEntry==null || archive.GetEntry(resource.ArchiveEntry)==null))
                throw new InvalidDataException("Копия в буфере должна содержать все материалы внутри архива.");
            var target=Path.Combine(resourceDirectory,Guid.NewGuid().ToString("N")+resource.Extension);
            if(replacements!=null && replacements.TryGetValue(resource.Id,out var substitute))
            {
                if(!File.Exists(substitute)) throw new FileNotFoundException("Файл замены не найден.",substitute);
                var length=new FileInfo(substitute).Length; if(length<=0 || length>MaxResourceBytes) throw new InvalidDataException("Файл замены слишком большой.");
                ValidateMaterial(substitute,resource.Extension);
                using var input=File.OpenRead(substitute); using var output=File.Create(target); CopyLimited(input,output,length,token); restored[resource.Id]=target; continue;
            }
            if(resource.ArchiveEntry!=null && archive.GetEntry(resource.ArchiveEntry) is {} embedded)
            {
                if(embedded.Length!=resource.Length) throw new InvalidDataException("Размер встроенного материала не совпадает с описанием.");
                using(var input=embedded.Open()) using(var output=File.Create(target)) CopyLimited(input,output,resource.Length,token);
                if(Hash(target,token)!=resource.Sha256) throw new InvalidDataException("Встроенный материал поврежден: контрольная сумма не совпадает.");
                ValidateMaterial(target,resource.Extension); restored[resource.Id]=target;
            }
            else if(!string.IsNullOrWhiteSpace(resource.ReferencePath) && File.Exists(resource.ReferencePath)
                && (resource.Sha256==null || Hash(resource.ReferencePath,token)==resource.Sha256))
            {
                var length=new FileInfo(resource.ReferencePath).Length; if(length<=0 || length>MaxResourceBytes) throw new InvalidDataException("Недопустимый исходный материал.");
                ValidateMaterial(resource.ReferencePath,resource.Extension);
                using var input=File.OpenRead(resource.ReferencePath); using var output=File.Create(target); CopyLimited(input,output,length,token); restored[resource.Id]=target;
            }
            else restored[resource.Id]=null;
        }
        var missing=ImmutableArray.CreateBuilder<MissingProjectSource>();
        var pdfSizes=new Dictionary<string,IReadOnlyList<SizeD>>(StringComparer.OrdinalIgnoreCase);
        var pages=manifest.Session.Pages.Select(page=>
        {
            var assets=page.Sheet.Assets.Select(asset=>
            {
                var link=manifest.Links.SingleOrDefault(l=>l.SheetId==page.Sheet.Id && l.AssetId==asset.Id)
                    ?? throw new InvalidDataException("Нет привязки исходника к материалу.");
                if(!restored.TryGetValue(link.ResourceId,out var working)) throw new InvalidDataException("Материал исходника не найден в описании.");
                if(working==null) missing.Add(new(page.Sheet.Id,asset.Id,asset.DisplayName,asset.OriginalPath,link.ResourceId,asset.Kind));
                else ValidateSourcePage(asset,working,pdfSizes);
                return asset with { WorkingPath=working };
            }).ToImmutableArray();
            var sheet=page.Sheet with { Assets=assets }; _=SheetDocument.Restore(sheet); return page with { Sheet=sheet };
        }).ToImmutableArray();
        return new(HydrateSession(manifest.Session with { Pages=pages }),missing.ToImmutable(),path);
    }

    private static void ValidateSourcePage(SourceAsset asset,string path,Dictionary<string,IReadOnlyList<SizeD>>? pdfSizes=null)
    {
        if(asset.Kind==SourceKind.Image)
        {
            using var image=System.Drawing.Image.FromFile(path);
            if((long)image.Width*image.Height>150_000_000) throw new InvalidDataException("Изображение слишком большое.");
            var actual=image.Width/(double)image.Height; var expected=asset.LogicalSize.Width/asset.LogicalSize.Height;
            if(Math.Abs(actual-expected)/expected>0.01) throw new InvalidDataException("Пропорции файла замены отличаются. Кроп и правки нельзя перенести без искажения.");
        }
        else
        {
            IReadOnlyList<SizeD> dimensions;
            if(pdfSizes==null || !pdfSizes.TryGetValue(path,out dimensions!))
            { dimensions=new PdfiumRenderer().ReadPages(path); if(pdfSizes!=null) pdfSizes[path]=dimensions; }
            if(asset.PageIndex>=dimensions.Count) throw new InvalidDataException("В файле замены нет нужной страницы.");
            var size=dimensions[asset.PageIndex];
            if(Math.Abs(size.Width-asset.LogicalSize.Width)>2 || Math.Abs(size.Height-asset.LogicalSize.Height)>2)
                throw new InvalidDataException("Размер страницы файла замены отличается от проекта.");
        }
    }
    private static void ValidateResource(ProjectResource resource)
    {
        if(string.IsNullOrEmpty(resource.Id) || resource.Id.Length>100 || resource.Id.Any(c=>!char.IsAsciiLetterOrDigit(c) && c!='-' && c!='.')) throw new InvalidDataException("Недопустимое имя материала.");
        if(!new[] { ".pdf",".png",".jpg",".jpeg",".bmp" }.Contains(resource.Extension) || resource.Length<0 || resource.Length>MaxResourceBytes)
            throw new InvalidDataException("Недопустимый формат/размер материала.");
        if(resource.ArchiveEntry!=null && (resource.ArchiveEntry!="assets/"+resource.Id || resource.Sha256==null || resource.Length==0))
            throw new InvalidDataException("Недопустимый путь встроенного материала.");
    }
    internal static void ValidateSession(ProjectSession session)
    {
        if(session==null || session.Print==null || session.Editor==null || session.Pages.IsDefault || session.MarkdownDocuments.IsDefault || session.Pages.Length>20000) throw new InvalidDataException("Недопустимое состояние проекта.");
        var markdown=MarkdownDefinitions(session.MarkdownDocuments);
        if(session.Pages.Select(p=>p.Sheet.Id).Distinct().Count()!=session.Pages.Length) throw new InvalidDataException("Повторяющиеся листы в проекте.");
        foreach(var page in session.Pages)
        {
            if(page.Copies is <1 or >999) throw new InvalidDataException("Недопустимое количество копий.");
            try { page.PrintOptions?.Validate(); }
            catch(ArgumentException error) { throw new InvalidDataException("Недопустимые настройки печати страницы.",error); }
            _=SheetDocument.Restore(page.Sheet); ValidateMarkdownReferences(page.Sheet,markdown);
        }
        foreach(var story in session.Pages.SelectMany(p=>p.Sheet.Objects.OfType<TextObject>()).Where(t=>t.Flow!=null).GroupBy(t=>t.Flow!.StoryId))
            if(story.Sum(t=>(long)t.Text.Length)>TextLimits.MaxCharacters || story.Select(t=>t.Flow!.PartIndex).Distinct().Count()!=story.Count())
                throw new InvalidDataException("Повреждена связь продолжений или превышен размер текста.");
        var p=session.Print;
        p.Notebook?.Validate();
        if(!double.IsFinite(p.PrintOffsetXmm) || !double.IsFinite(p.PrintOffsetYmm) || Math.Abs(p.PrintOffsetXmm)>30 || Math.Abs(p.PrintOffsetYmm)>30)
            throw new InvalidDataException("Недопустимые поправки печати.");
        if(!Enum.IsDefined(p.Paper) || !Enum.IsDefined(p.Scaling) || !Enum.IsDefined(p.Monochrome) || p.Dpi is not (300 or 600)
            || p.Threshold is <0 or >255 || !double.IsFinite(p.CustomWidthMm) || !double.IsFinite(p.CustomHeightMm) || p.CustomWidthMm<25 || p.CustomWidthMm>1000 || p.CustomHeightMm<25 || p.CustomHeightMm>1000)
            throw new InvalidDataException("Недопустимые настройки печати.");
        var e=session.Editor;
        if(!double.IsFinite(e.Zoom) || e.Zoom<ViewportZoom.Minimum || e.Zoom>ViewportZoom.Maximum || !double.IsFinite(e.StrokeThickness) || e.StrokeThickness<=0 || e.StrokeThickness>1000
            || !double.IsFinite(e.TextSize) || e.TextSize<=0 || e.TextSize>1000 || !double.IsFinite(e.CropRatio) || e.CropRatio<0 || e.CropRatio>1000 || e.ExportDpi is not (300 or 600))
            throw new InvalidDataException("Недопустимые настройки редактора.");
        if(!double.IsFinite(e.BrushOpacity) || e.BrushOpacity<0 || e.BrushOpacity>1 || !double.IsFinite(e.MarkerOpacity) || e.MarkerOpacity<0 || e.MarkerOpacity>1
            || !double.IsFinite(e.MarkerThickness) || e.MarkerThickness<=0 || e.MarkerThickness>1000 || !double.IsFinite(e.EraserDiameter) || e.EraserDiameter<=0 || e.EraserDiameter>1000)
            throw new InvalidDataException("Недопустимые настройки кисти, маркера или ластика.");
        _=System.Drawing.ColorTranslator.FromHtml(e.InkColor);
        if(e.SheetTextStyle!=null) TextLayoutService.ValidateStyle(e.SheetTextStyle);
    }
    public static ImmutableArray<MarkdownDocument> CollectMarkdownDocuments(IEnumerable<ProjectPage> pages,
        IEnumerable<MarkdownDocument>? definitions=null)
    {
        var known=MarkdownDefinitions(definitions ?? []);
        var used=new Dictionary<Guid,MarkdownDocument>();
        foreach(var item in pages.SelectMany(p=>p.Sheet.Objects.OfType<MarkdownObject>()))
        {
            var document=item.Document;
            if(document!=null)
            {
                ValidateMarkdownDocument(document);
                if(document.Id!=item.DocumentId) throw new InvalidDataException("Идентификатор Markdown не совпадает с объектом.");
                if(known.TryGetValue(document.Id,out var existing) && !SameMarkdownDocument(existing,document))
                    throw new InvalidDataException("Связанные страницы содержат разные исходники Markdown.");
                known[document.Id]=document;
            }
            else if(!known.TryGetValue(item.DocumentId,out document)) throw new InvalidDataException("Исходник Markdown не найден.");
            if(used.TryGetValue(document.Id,out var previous) && !SameMarkdownDocument(previous,document))
                throw new InvalidDataException("Связанные страницы содержат разные исходники Markdown.");
            used.TryAdd(document.Id,document);
        }
        return used.Values.ToImmutableArray();
    }
    public static ProjectSession HydrateSession(ProjectSession session)
    {
        ValidateSession(session);
        var documents=MarkdownDefinitions(session.MarkdownDocuments);
        return session with {Pages=session.Pages.Select(p=>p with {Sheet=HydrateSheet(p.Sheet,documents.Values)}).ToImmutableArray()};
    }
    public static SheetSnapshot HydrateSheet(SheetSnapshot sheet,IEnumerable<MarkdownDocument> definitions)
    {
        var documents=MarkdownDefinitions(definitions); ValidateMarkdownReferences(sheet,documents);
        return sheet with {Objects=sheet.Objects.Select(item=>item is MarkdownObject markdown
            ? markdown with {Document=documents[markdown.DocumentId]} : item).ToImmutableArray()};
    }
    public static void ValidateMarkdownDocument(MarkdownDocument document)
    {
        if(document==null || document.Id==Guid.Empty || document.Source==null || document.Source.Length>TextLimits.MaxCharacters
            || document.Title==null || document.Title.Length>512 || document.Style==null || document.Style.Body==null || document.Style.Margins==null
            || document.Images.IsDefault || document.Images.Length>20000) throw new InvalidDataException("Недопустимый исходник Markdown.");
        if(document.LayoutEngineVersion is not (1 or 2 or MarkdownDocument.CurrentLayoutEngineVersion))
            throw new InvalidDataException("Версия верстки Markdown не поддерживается.");
        var style=document.Style;
        if(style.Body.FontFamily?.Length>256 || style.Body.Color?.Length>64) throw new InvalidDataException("Недопустимое оформление Markdown.");
        TextLayoutService.ValidateStyle(style.Body);
        foreach(var value in new[] {style.Margins.LeftMm,style.Margins.TopMm,style.Margins.RightMm,style.Margins.BottomMm,style.ParagraphGapMm})
            if(!double.IsFinite(value) || value<0 || value>1000) throw new InvalidDataException("Недопустимые поля или интервалы Markdown.");
        if(document.Images.Any(i=>i==null || string.IsNullOrWhiteSpace(i.Url) || i.Url.Length>4096 || i.AssetId==Guid.Empty)
            || document.Images.Select(i=>i.Url).Distinct(StringComparer.Ordinal).Count()!=document.Images.Length)
            throw new InvalidDataException("Недопустимые ссылки изображений Markdown.");
    }
    private static Dictionary<Guid,MarkdownDocument> MarkdownDefinitions(IEnumerable<MarkdownDocument> definitions)
    {
        var result=new Dictionary<Guid,MarkdownDocument>();
        foreach(var document in definitions)
        {
            ValidateMarkdownDocument(document);
            if(result.Count>=20000 || !result.TryAdd(document.Id,document)) throw new InvalidDataException("Повторяющиеся или слишком многочисленные исходники Markdown.");
        }
        return result;
    }
    private static void ValidateMarkdownReferences(SheetSnapshot sheet,IReadOnlyDictionary<Guid,MarkdownDocument> documents)
    {
        foreach(var item in sheet.Objects.OfType<MarkdownObject>())
        {
            if(item.DocumentId==Guid.Empty || item.PageIndex<0 || item.PageIndex>=TextLimits.MaxPages || !documents.TryGetValue(item.DocumentId,out var document))
                throw new InvalidDataException("Страница Markdown не связана с исходником.");
            if(item.Document!=null && !SameMarkdownDocument(item.Document,document)) throw new InvalidDataException("Связанные страницы содержат разные исходники Markdown.");
            foreach(var image in document.Images)
                if(!sheet.Assets.Any(a=>a.Id==image.AssetId && a.Kind==SourceKind.Image)) throw new InvalidDataException("Материал изображения Markdown не найден на листе.");
        }
    }
    private static bool SameMarkdownDocument(MarkdownDocument left,MarkdownDocument right)
        =>left.Id==right.Id && left.Source==right.Source && left.Style==right.Style && left.Title==right.Title && left.LayoutEngineVersion==right.LayoutEngineVersion
            && left.Images.Length==right.Images.Length && left.Images.OrderBy(i=>i.Url,StringComparer.Ordinal).SequenceEqual(right.Images.OrderBy(i=>i.Url,StringComparer.Ordinal));

    private static string SourceExtension(SourceAsset source)
    {
        var extension=(Path.GetExtension(source.WorkingPath ?? source.OriginalPath) ?? "").ToLowerInvariant();
        return source.Kind==SourceKind.Image ? new[] { ".png",".jpg",".jpeg",".bmp" }.Contains(extension) ? extension : ".png" : ".pdf";
    }
    private static string Hash(string path,CancellationToken token)
    {
        using var stream=File.OpenRead(path); using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer=new byte[81920]; int count;
        while((count=stream.Read(buffer))>0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer,0,count); } return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private static void CopyLimited(Stream input,Stream output,long expected,CancellationToken token)
    {
        var buffer=new byte[81920]; long total=0; int count;
        while((count=input.Read(buffer))>0) { token.ThrowIfCancellationRequested(); total+=count; if(total>expected || total>MaxResourceBytes && expected<=MaxResourceBytes) throw new InvalidDataException("Материал превышает заявленный размер."); output.Write(buffer,0,count); }
        if(total!=expected) throw new InvalidDataException("Материал прочитан не полностью.");
    }
    private static void ValidateMaterial(string path,string extension)
    {
        using var stream=File.OpenRead(path); Span<byte> header=stackalloc byte[8]; var count=stream.Read(header);
        var valid=extension switch { ".pdf"=>count>=5 && header[..5].SequenceEqual("%PDF-"u8),".png"=>count>=8 && header.SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}),
            ".jpg" or ".jpeg"=>count>=3 && header[0]==255 && header[1]==216 && header[2]==255,".bmp"=>count>=2 && header[0]==66 && header[1]==77,_=>false };
        if(!valid) throw new InvalidDataException("Содержимое материала не соответствует его формату.");
    }
}
