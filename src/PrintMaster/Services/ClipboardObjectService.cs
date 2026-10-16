using PrintMaster.Models;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrintMaster.Services;

public static class ClipboardObjectService
{
    public const int MaxJsonCharacters=96*1024*1024;
    public const int MaxMaterialBytes=64*1024*1024;
    private static readonly JsonSerializerOptions Json=new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase,IgnoreReadOnlyProperties=true,MaxDepth=64,Converters={ new JsonStringEnumConverter() } };
    public static string Serialize(ClipboardObjectPackage package)
    {
        if(package.Version is >=1 and <=5 && package.Object is MarkdownObject)
        {
            var sheet=ClipboardSheet(package);
            var documents=ProjectFileService.CollectMarkdownDocuments([new(sheet,true,1)],package.MarkdownDocuments);
            package=package with {Version=documents.Max(d=>d.LayoutEngineVersion) switch {3=>5,2=>4,_=>3},MarkdownDocuments=documents};
        }
        if(package.Version==1 && (package.Annotation is {} annotation && HasNewAppearance(annotation)
            || package.Object is ContentObject content && content.Edits.Annotations.Any(HasNewAppearance))) package=package with {Version=2};
        Validate(package); var json=JsonSerializer.Serialize(package,Json); if(json.Length>MaxJsonCharacters) throw new InvalidDataException("Объект слишком большой для буфера обмена."); return json;
    }
    public static ClipboardObjectPackage Deserialize(string json)
    {
        if(json.Length>MaxJsonCharacters) throw new InvalidDataException("Объект слишком большой для буфера обмена.");
        using var data=JsonDocument.Parse(json,new JsonDocumentOptions {MaxDepth=64});
        var package=data.RootElement.Deserialize<ClipboardObjectPackage>(Json) ?? throw new InvalidDataException("Пустая копия объекта.");
        // Version 3 existed before pagination metadata. Missing metadata means that
        // the copied page index still belongs to the previous layout engine.
        if(package.Object is MarkdownObject && !package.MarkdownDocuments.IsDefault
            && data.RootElement.TryGetProperty("markdownDocuments",out var definitions) && definitions.ValueKind==JsonValueKind.Array)
            package=package with {MarkdownDocuments=package.MarkdownDocuments.Select((document,index)=>
            {
                if(document is null || definitions[index].ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Недопустимый исходник Markdown.");
                if(definitions[index].TryGetProperty("layoutEngineVersion",out _))return document;
                if(package.Version>=4)throw new InvalidDataException("В копии Markdown отсутствует версия верстки.");
                return document with {LayoutEngineVersion=1};
            }).ToImmutableArray()};
        Validate(package);
        if(package.Object is MarkdownObject) package=package with {Object=ProjectFileService.HydrateSheet(ClipboardSheet(package),package.MarkdownDocuments).Objects[0]};
        return package;
    }
    public static ClipboardObjectPackage Capture(SheetSnapshot sheet,SheetObject item)
    {
        var materials=ImmutableArray<ClipboardMaterial>.Empty;
        if(item is ContentObject content)
        {
            materials=[CaptureMaterial(sheet.Assets.First(a=>a.Id==content.AssetId))];
        }
        else if(item is MarkdownObject markdown)
        {
            var document=markdown.Document ?? throw new InvalidDataException("Исходник Markdown не найден.");
            ProjectFileService.ValidateMarkdownDocument(document);
            var ids=document.Images.Select(i=>i.AssetId).ToHashSet();
            if(ids.Any(id=>!sheet.Assets.Any(a=>a.Id==id && a.Kind==SourceKind.Image))) throw new InvalidDataException("Изображение Markdown отсутствует.");
            var assets=sheet.Assets.Where(a=>ids.Contains(a.Id)).ToArray(); long total=0;
            foreach(var asset in assets)
            {
                if(asset.WorkingPath==null || !File.Exists(asset.WorkingPath)) throw new InvalidOperationException("Материал объекта отсутствует. Переподключите исходник.");
                total+=new FileInfo(asset.WorkingPath).Length;
                if(total>MaxMaterialBytes) throw new InvalidOperationException("Материалы Markdown превышают 64 МБ и не помещаются в копию объекта.");
            }
            materials=assets.Select(CaptureMaterial).ToImmutableArray();
            var package=new ClipboardObjectPackage(document.LayoutEngineVersion switch {3=>5,2=>4,_=>3},item,materials) {MarkdownDocuments=[document]}; Validate(package); return package;
        }
        return new(item is ContentObject c && c.Edits.Annotations.Any(HasNewAppearance) ? 2 : 1,item,materials);
    }
    private static ClipboardMaterial CaptureMaterial(SourceAsset asset)
    {
        var path=asset.WorkingPath;
        if(path==null || !File.Exists(path)) throw new InvalidOperationException("Материал объекта отсутствует. Переподключите исходник.");
        if(new FileInfo(path).Length>MaxMaterialBytes) throw new InvalidOperationException("Материал превышает 64 МБ и не помещается в копию объекта.");
        return new(asset with {OriginalPath=null,WorkingPath=null},asset.Kind==SourceKind.Image ? Path.GetExtension(path).ToLowerInvariant() : ".pdf",File.ReadAllBytes(path));
    }
    private static SheetSnapshot ClipboardSheet(ClipboardObjectPackage package)
        =>new(Guid.NewGuid(),SheetPaper.A4,package.Materials.Select(m=>m.Asset).ToImmutableArray(),[package.Object!],null);
    public static int AnnotationVersion(Annotation annotation)=>HasNewAppearance(annotation) ? 2 : 1;
    private static bool HasNewAppearance(Annotation annotation)=>annotation.Opacity!=null || annotation.FillColor!=null;
    public static SheetObject CloneObject(SheetObject original,PointD offset,Guid? assetId=null)
    {
        var bounds=original.Placement.BoundsMm; var placement=original.Placement with { BoundsMm=bounds with { X=bounds.X+offset.X,Y=bounds.Y+offset.Y },Mode=PlacementMode.Fixed };
        return original switch
        {
            ContentObject c=>c with { Id=Guid.NewGuid(),AssetId=assetId ?? c.AssetId,Placement=placement,Visible=true,Locked=false,
                Edits=c.Edits with { Annotations=c.Edits.Annotations.Select(a=>a with { Id=Guid.NewGuid() }).ToImmutableArray() } },
            TextObject t=>t with { Id=Guid.NewGuid(),Placement=placement,Flow=null,Visible=true,Locked=false },
            ShapeObject s=>s with { Id=Guid.NewGuid(),Placement=placement,Visible=true,Locked=false },
            InkObject i=>i with { Id=Guid.NewGuid(),Placement=placement,Visible=true,Locked=false },
            MarkdownObject markdown=>markdown with {Id=Guid.NewGuid(),Placement=placement,Visible=true},
            _=>throw new InvalidDataException("Неизвестный объект в буфере.")
        };
    }
    public static void Validate(ClipboardObjectPackage package)
    {
        if(package.Version is not (1 or 2 or 3 or 4 or 5) || package.Materials.IsDefault || package.MarkdownDocuments.IsDefault
            || package.Materials.Length>20000 || (package.Object==null)==(package.Annotation==null)) throw new InvalidDataException("Неподдерживаемая копия объекта.");
        if(package.Object is not MarkdownObject && (!package.MarkdownDocuments.IsEmpty || package.Materials.Length>1)
            || package.Object is MarkdownObject && package.Version<3) throw new InvalidDataException("Неподдерживаемая копия Markdown.");
        if(package.Version>=4 && (package.Object is not MarkdownObject
            || package.MarkdownDocuments.Any(d=>d is null || d.LayoutEngineVersion!=(package.Version==4 ? 2 : MarkdownDocument.CurrentLayoutEngineVersion))))
            throw new InvalidDataException("Версия буфера не соответствует верстке Markdown.");
        if(package.Materials.Select(m=>m.Asset.Id).Distinct().Count()!=package.Materials.Length || package.Materials.Sum(m=>(long)(m.Bytes?.Length ?? 0))>MaxMaterialBytes)
            throw new InvalidDataException("Материалы в буфере повторяются или превышают 64 МБ.");
        foreach(var material in package.Materials)
        {
            if(material.Bytes==null || material.Bytes.Length==0 || material.Bytes.Length>MaxMaterialBytes || material.Asset.OriginalPath!=null || material.Asset.WorkingPath!=null) throw new InvalidDataException("Некорректный материал в буфере.");
            var bytes=material.Bytes;
            var valid=material.Extension switch { ".png"=>bytes.Length>=8 && bytes.AsSpan(0,8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}),
                ".jpg" or ".jpeg"=>bytes.Length>=3 && bytes[0]==255 && bytes[1]==216 && bytes[2]==255,".bmp"=>bytes.Length>=2 && bytes[0]==66 && bytes[1]==77,".pdf"=>bytes.Length>=5 && bytes.AsSpan(0,5).SequenceEqual("%PDF-"u8),_=>false };
            if(!valid || material.Asset.Kind==SourceKind.Image && material.Extension==".pdf" || material.Asset.Kind!=SourceKind.Image && material.Extension!=".pdf") throw new InvalidDataException("Формат материала в буфере не совпадает с данными.");
        }
        if(package.Object!=null)
        {
            if(package.Object is ContentObject content && (package.Materials.Length!=1 || package.Materials[0].Asset.Id!=content.AssetId)
                || package.Object is not (ContentObject or MarkdownObject) && package.Materials.Length!=0) throw new InvalidDataException("Материал скопированного объекта не найден.");
            var sheet=ClipboardSheet(package); _=SheetDocument.Restore(sheet);
            if(package.Object is MarkdownObject markdown)
            {
                if(package.MarkdownDocuments.Length!=1 || package.MarkdownDocuments[0].Id!=markdown.DocumentId) throw new InvalidDataException("Исходник Markdown в буфере не найден.");
                _=ProjectFileService.HydrateSheet(sheet,package.MarkdownDocuments);
                var required=package.MarkdownDocuments[0].Images.Select(i=>i.AssetId).ToHashSet();
                if(!required.SetEquals(package.Materials.Select(m=>m.Asset.Id))) throw new InvalidDataException("Материалы Markdown в буфере не совпадают с исходником.");
            }
        }
        else
        {
            if(package.Materials.Length!=0 || package.AnnotationSourceSize is not {} size) throw new InvalidDataException("Нет размеров исходника аннотации.");
            var document=SheetDocument.FromSource(new(Guid.NewGuid(),SourceKind.Image,null,null,0,1,size,size));
            document.SetContentEdits(document.PrimaryContent!.Id,new(new(0,0,size.Width,size.Height),0,[package.Annotation!]),"Проверить аннотацию");
        }
    }
}
