using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace ClothingSkeleton.Core;

public sealed record ReferenceModel(string Id,string Group,string Slot,string Label,string GamePath,string File,bool Editable=false);
public sealed record ReferenceModels(Guid ProjectId,Guid ModId,List<ReferenceModel> Models,bool MaterialsIncluded=false);
public sealed record GarmentPart(string Id,string Name,string GamePath,string File,string Sha256);
public sealed record GarmentReturn(Guid ProjectId,Guid? ModId,bool FullClothing,List<GarmentPart> Parts,int SchemaVersion=1);

public static class ModelExchange
{
    public static bool ReferenceFile(string name)=>Regex.IsMatch(name,@"^ref-[0-9a-f]{16}\.glb$");
    public static bool GarmentFile(string name)=>Regex.IsMatch(name,@"^cloth-[0-9a-f]{16}\.gltf$");
    public static void CheckSelfContained(byte[] bytes)
    {
        using var json=JsonDocument.Parse(bytes);var root=json.RootElement;
        if(root.GetProperty("asset").GetProperty("version").GetString()!="2.0" || !root.TryGetProperty("meshes",out var meshes) || meshes.GetArrayLength()==0)throw new InvalidDataException("服装文件必须是包含网格的 glTF 2.0。");
        void Visit(JsonElement element)
        {
            if(element.ValueKind==JsonValueKind.Object)
                foreach(var property in element.EnumerateObject())
                {
                    if(property.Name=="uri" && (property.Value.ValueKind!=JsonValueKind.String || !property.Value.GetString()!.StartsWith("data:",StringComparison.Ordinal)))throw new InvalidDataException("服装交换文件不能引用外部路径或网络资源。");
                    Visit(property.Value);
                }
            else if(element.ValueKind==JsonValueKind.Array)foreach(var child in element.EnumerateArray())Visit(child);
        }
        Visit(root);
        if(!root.TryGetProperty("skins",out var skins) || skins.GetArrayLength()!=1)throw new InvalidDataException("每个服装部件需要独立且唯一的绑定骨架。");
        foreach(var mesh in meshes.EnumerateArray())
            foreach(var primitive in mesh.GetProperty("primitives").EnumerateArray())
            {
                var attributes=primitive.GetProperty("attributes");
                if(!attributes.TryGetProperty("POSITION",out _) || !attributes.TryGetProperty("JOINTS_0",out _) || !attributes.TryGetProperty("WEIGHTS_0",out _))throw new InvalidDataException("服装网格缺少位置或骨骼权重，请先完成绑定。");
                if(primitive.TryGetProperty("mode",out var mode) && mode.GetInt32()!=4)throw new InvalidDataException("服装网格必须导出为三角面。");
            }
    }

    public static GarmentReturn ValidateReturn(Dictionary<string,byte[]> files,SkeletonDocument baseline,Guid? expectedMod=null)
    {
        if(!files.TryGetValue("parts.json",out var bytes))throw new InvalidDataException("缺少服装返回说明，请更新 Blender 插件。");
        var result=JsonSerializer.Deserialize<GarmentReturn>(bytes,BridgeExchange.Json)??throw new InvalidDataException("无效的服装返回说明。");
        if(result.SchemaVersion!=1 || result.ProjectId!=baseline.ProjectId || result.ModId!=expectedMod || result.Parts is null || result.Parts.Count>32 || result.Parts.Select(p=>p.Id).Distinct().Count()!=result.Parts.Count || result.FullClothing!=(result.Parts.Count>0))throw new InvalidDataException("服装返回项目、模式或部件身份不一致。");
        var expected=BridgeExchange.ReturnNames.ToHashSet(StringComparer.Ordinal);
        foreach(var part in result.Parts)
        {
            ModWorkspace.CheckModelPath(part.GamePath);
            if(part.Id!=ModWorkspace.PartId(part.GamePath) || part.File!="cloth-"+part.Id+".gltf" || !expected.Add(part.File) || !files.TryGetValue(part.File,out var gltf) || Convert.ToHexStringLower(SHA256.HashData(gltf))!=part.Sha256)throw new InvalidDataException("服装部件路径、内容或完成摘要不一致。");
            CheckSelfContained(gltf);
        }
        if(!expected.SetEquals(files.Keys))throw new InvalidDataException("返回文件包含未声明的部件。");
        return result;
    }
}
