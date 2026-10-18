using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClothingSkeleton.Core;

public sealed record BridgeProject(Guid ProjectId,string Revision,Guid TransferId,Dictionary<string,string> Files);
public sealed record BridgeReturn(Guid ProjectId,string Revision,Guid RequestId,Dictionary<string,string> Files);

/// <summary>Portable wire files only. Neither disk paths nor native game files cross the connection.</summary>
public static class BridgeExchange
{
    public const int Protocol=1;
    public const int DefaultPort=43867;
    public const int MaxBodyBytes=48*1024*1024;
    public static JsonSerializerOptions Json {get;}=new(JsonSerializerDefaults.Web);
    public static readonly string[] ProjectNames=["skeleton.gltf","skeleton.ffxiv.json","skeleton.complete.json","reference.gltf","reference.ffxiv.json","reference.complete.json","anchor-rig.json","skeleton.bin","reference.bin"];
    public static readonly string[] ReturnNames=["edited.gltf","edited.ffxiv.json","edited.complete.json"];

    public static BridgeProject ReadProject(string directory)
    {
        var baseline=SkeletonDocument.Load(Path.Combine(directory,"snapshot.json"));
        var info=JsonSerializer.Deserialize<AnchorRigInfo>(File.ReadAllText(Path.Combine(directory,"anchor-rig.json")),SkeletonDocument.JsonOptions);
        if(info?.CharacterSource is null || !info.Available || info.ProjectId!=baseline.ProjectId || info.SourceSha256!=baseline.SourceSha256)
            throw new InvalidDataException("请先读取场景角色并创建新版骨架项目。");
        info.CharacterSource.CheckEquipmentPath(baseline.SourceGamePath);
        if(info.ReferenceGamePath!=info.CharacterSource.GamePath)throw new InvalidDataException("人物骨架来源不一致。");
        var files=new Dictionary<string,string>();var total=0;
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach(var name in ProjectNames)
        {
            var path=Path.Combine(directory,name);
            if(!File.Exists(path))
            {
                if(name.EndsWith(".bin",StringComparison.Ordinal))continue;
                throw new InvalidDataException("骨架项目缺少交换文件，请重新创建。");
            }
            var bytes=File.ReadAllBytes(path);
            total=checked(total+bytes.Length);
            if(total>32*1024*1024)throw new InvalidDataException("骨架交换文件过大。");
            hash.AppendData(Encoding.UTF8.GetBytes(name));hash.AppendData(BitConverter.GetBytes(bytes.Length));hash.AppendData(bytes);
            files.Add(name,Convert.ToBase64String(bytes));
        }
        return new(baseline.ProjectId,Convert.ToHexStringLower(hash.GetHashAndReset()),Guid.NewGuid(),files);
    }

    public static SkeletonDocument Baseline(BridgeProject project)
    {
        if(!project.Files.TryGetValue("skeleton.ffxiv.json",out var encoded))throw new InvalidDataException("缺少骨架身份信息。");
        var baseline=JsonSerializer.Deserialize<SkeletonDocument>(Convert.FromBase64String(encoded),SkeletonDocument.JsonOptions)??throw new InvalidDataException("无效的骨架身份信息。");
        if(baseline.ProjectId!=project.ProjectId)throw new InvalidDataException("骨架项目身份不一致。");
        return baseline;
    }

    public static int PublishReturn(string target,SkeletonDocument baseline,BridgeReturn packet)
    {
        if(packet.ProjectId!=baseline.ProjectId || packet.RequestId==Guid.Empty || packet.Files is null || packet.Files.Count!=ReturnNames.Length || packet.Files.Keys.Except(ReturnNames,StringComparer.Ordinal).Any())
            throw new InvalidDataException("返回文件或项目身份无效。");
        var files=packet.Files.ToDictionary(p=>p.Key,p=>Convert.FromBase64String(p.Value));
        if(files.Values.Any(b=>b.Length>32*1024*1024))throw new InvalidDataException("返回文件过大。");
        var parent=Path.GetDirectoryName(Path.GetFullPath(target))!;Directory.CreateDirectory(parent);
        var stage=Path.Combine(parent,".network-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        var replaced=new List<string>();var preserveRecovery=false;
        try
        {
            foreach(var (name,bytes) in files)File.WriteAllBytes(Path.Combine(stage,name),bytes);
            var candidate=GltfExchange.Import(Path.Combine(stage,"edited.gltf"),baseline);
            foreach(var name in ReturnNames)
            {
                var output=Path.Combine(parent,name);
                if(File.Exists(output))File.Copy(output,Path.Combine(stage,name+".backup"));
            }
            try
            {
                // Completion is last, so an in-progress replacement is never mistaken for a completed return.
                foreach(var name in ReturnNames)
                {
                    File.Move(Path.Combine(stage,name),Path.Combine(parent,name),true);replaced.Add(name);
                }
            }
            catch
            {
                try {foreach(var name in replaced)
                {
                    var backup=Path.Combine(stage,name+".backup");
                    if(File.Exists(backup))File.Move(backup,Path.Combine(parent,name),true);
                    else File.Delete(Path.Combine(parent,name));
                }}catch {preserveRecovery=true;throw;}
                throw;
            }
            return candidate.Bones.Count;
        }
        finally
        {
            // Delete only the exact files created above, never recurse through a user directory.
            if(!preserveRecovery)
            {
                foreach(var name in ReturnNames)
                    foreach(var suffix in new[]{"",".backup"})
                        try {File.Delete(Path.Combine(stage,name+suffix));}catch(Exception e) when(e is IOException or UnauthorizedAccessException) { }
                try {Directory.Delete(stage);}catch(Exception e) when(e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
