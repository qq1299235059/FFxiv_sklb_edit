using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClothingSkeleton.Core;

public sealed record BridgeProject(Guid ProjectId,string Revision,Guid TransferId,Dictionary<string,string> Files,Guid? ModId=null);
public sealed record BridgeReturn(Guid ProjectId,string Revision,Guid RequestId,Dictionary<string,string> Files,Guid? ModId=null);

/// <summary>Portable wire files only. Neither disk paths nor native game files cross the connection.</summary>
public static class BridgeExchange
{
    public const int Protocol=2;
    public const int DefaultPort=43867;
    public const int MaxBodyBytes=192*1024*1024;
    public static JsonSerializerOptions Json {get;}=new(JsonSerializerDefaults.Web);
    public static readonly string[] ProjectNames=["skeleton.gltf","skeleton.ffxiv.json","skeleton.complete.json","reference.gltf","reference.ffxiv.json","reference.complete.json","anchor-rig.json","skeleton.bin","reference.bin","mod-project.json","models.json"];
    public static readonly string[] ReturnNames=["edited.gltf","edited.ffxiv.json","edited.complete.json","parts.json"];

    public static BridgeProject ReadProject(string directory)
    {
        var baseline=SkeletonDocument.Load(Path.Combine(directory,"snapshot.json"));
        var info=JsonSerializer.Deserialize<AnchorRigInfo>(File.ReadAllText(Path.Combine(directory,"anchor-rig.json")),SkeletonDocument.JsonOptions);
        if(info?.CharacterSource is null || !info.Available || info.ProjectId!=baseline.ProjectId || info.SourceSha256!=baseline.SourceSha256)
            throw new InvalidDataException("请先读取场景角色并创建新版骨架项目。");
        if(info.AllowNewBones)info.CharacterSource.CheckEquipmentPath(baseline.SourceGamePath);
        else if(baseline.SourceGamePath!=info.CharacterSource.GamePath)throw new InvalidDataException("仅模型项目的人物来源不一致。");
        if(info.ReferenceGamePath!=info.CharacterSource.GamePath)throw new InvalidDataException("人物骨架来源不一致。");
        var files=new Dictionary<string,string>();var total=0;
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var modelsFile=Path.Combine(directory,"models.json");
        var modelFiles=File.Exists(modelsFile)?(JsonSerializer.Deserialize<ReferenceModels>(File.ReadAllText(modelsFile),Json)?.Models??[]).Select(m=>m.File).Distinct().Order(StringComparer.Ordinal).ToArray():[];
        if(modelFiles.Length>32 || modelFiles.Any(n=>!ModelExchange.ReferenceFile(n)))throw new InvalidDataException("参考模型文件名或数量无效。");
        foreach(var name in ProjectNames.Concat(modelFiles))
        {
            var path=Path.Combine(directory,name);
            if(!File.Exists(path))
            {
                if(name.EndsWith(".bin",StringComparison.Ordinal) || name is "mod-project.json" or "models.json")continue;
                throw new InvalidDataException("骨架项目缺少交换文件，请重新创建。");
            }
            var bytes=File.ReadAllBytes(path);
            total=checked(total+bytes.Length);
            if(total>128*1024*1024)throw new InvalidDataException("骨架交换文件过大。");
            hash.AppendData(Encoding.UTF8.GetBytes(name));hash.AppendData(BitConverter.GetBytes(bytes.Length));hash.AppendData(bytes);
            files.Add(name,Convert.ToBase64String(bytes));
        }
        var modFile=Path.Combine(directory,"mod-project.json");
        var mod=File.Exists(modFile)?JsonSerializer.Deserialize<ModProject>(File.ReadAllText(modFile),Json):null;
        return new(baseline.ProjectId,Convert.ToHexStringLower(hash.GetHashAndReset()),Guid.NewGuid(),files,mod?.Id);
    }

    public static SkeletonDocument Baseline(BridgeProject project)
    {
        if(!project.Files.TryGetValue("skeleton.ffxiv.json",out var encoded))throw new InvalidDataException("缺少骨架身份信息。");
        var baseline=JsonSerializer.Deserialize<SkeletonDocument>(Convert.FromBase64String(encoded),SkeletonDocument.JsonOptions)??throw new InvalidDataException("无效的骨架身份信息。");
        if(baseline.ProjectId!=project.ProjectId)throw new InvalidDataException("骨架项目身份不一致。");
        return baseline;
    }

    public static int PublishReturn(string target,SkeletonDocument baseline,BridgeReturn packet,Guid? expectedMod=null)
    {
        if(packet.ProjectId!=baseline.ProjectId || packet.ModId!=expectedMod || packet.RequestId==Guid.Empty || packet.Files is null || packet.Files.Count is <4 or >36 || packet.Files.Keys.Any(n=>!ReturnNames.Contains(n,StringComparer.Ordinal) && !ModelExchange.GarmentFile(n)))
            throw new InvalidDataException("返回文件或项目身份无效。");
        var files=packet.Files.ToDictionary(p=>p.Key,p=>Convert.FromBase64String(p.Value));
        if(files.Values.Sum(b=>(long)b.Length)>128*1024*1024)throw new InvalidDataException("返回文件过大。");
        _=ModelExchange.ValidateReturn(files,baseline,expectedMod);
        var parent=Path.GetDirectoryName(Path.GetFullPath(target))!;Directory.CreateDirectory(parent);
        var stage=Path.Combine(parent,".network-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        var previous=Directory.GetFiles(parent).Select(Path.GetFileName).Where(n=>n is not null && (ReturnNames.Contains(n,StringComparer.Ordinal) || ModelExchange.GarmentFile(n))).Cast<string>().ToArray();
        var names=files.Keys.Union(previous).ToArray();var backed=new List<string>();var written=new List<string>();var preserveRecovery=false;
        try
        {
            foreach(var (name,bytes) in files)File.WriteAllBytes(Path.Combine(stage,name),bytes);
            var candidate=GltfExchange.Import(Path.Combine(stage,"edited.gltf"),baseline);
            try
            {
                foreach(var name in names)
                {
                    var output=Path.Combine(parent,name);if(!File.Exists(output))continue;
                    File.Move(output,Path.Combine(stage,name+".backup"));backed.Add(name);
                }
                foreach(var name in files.Keys.OrderBy(n=>n=="edited.complete.json"?1:0))
                {File.Move(Path.Combine(stage,name),Path.Combine(parent,name));written.Add(name);}
            }
            catch
            {
                try
                {
                    foreach(var name in written)File.Delete(Path.Combine(parent,name));
                    foreach(var name in backed)File.Move(Path.Combine(stage,name+".backup"),Path.Combine(parent,name),true);
                }
                catch {preserveRecovery=true;throw;}
                throw;
            }
            return candidate.Bones.Count;
        }
        finally
        {
            if(!preserveRecovery)
            {
                foreach(var name in names)
                    foreach(var suffix in new[]{"",".backup"})
                        try {File.Delete(Path.Combine(stage,name+suffix));}catch(Exception e) when(e is IOException or UnauthorizedAccessException) { }
                try {Directory.Delete(stage);}catch(Exception e) when(e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
