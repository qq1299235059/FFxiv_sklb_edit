using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClothingSkeleton.Core;

namespace ClothingSkeleton.Plugin;

public sealed partial class Plugin
{
    private sealed record ModelInput(string GamePath,byte[] Bytes,HashSet<string> SkinMaterials,string Slot);
    private sealed record ModelCapture(List<ModelInput> Models,List<SkeletonDocument> Skeletons);
    private static string ModelSlot(string path)
    {
        if(path.StartsWith("chara/weapon/",StringComparison.Ordinal))return "weapon";
        var match=Regex.Match(path,@"_(top|dwn|glv|sho|met|ear|nek|wrs|rir|ril)\.mdl$");return match.Success?match.Groups[1].Value:"body";
    }
    private static string SlotLabel(string slot)=>slot switch{"top"=>"上衣","dwn"=>"下装","glv"=>"手套","sho"=>"鞋子","met"=>"头饰","ear"=>"耳饰","nek"=>"项链","wrs"=>"手饰","rir"=>"右戒指","ril"=>"左戒指","weapon"=>"武器",_=>"身体与头部"};
    private ModelCapture CaptureModelReferences(ModWorkspace workspace,CharacterSkeletonReader.Selection selected,string directory)
    {
        var skeletons=ReadModelSkeletons(directory).ToList();var models=new List<ModelInput>();
        if(workspace.Project.ReferenceGroups==ReferenceModelGroups.None && !workspace.Project.ExportClothing)return new(models,skeletons);
        var backend=new PenumbraModelBackend();
        var tracked=pluginInterface.GetIpcSubscriber<ushort[],Dictionary<string,HashSet<string>>?[]>("Penumbra.GetGameObjectResourcePaths.V5").InvokeFunc([selected.ObjectIndex]);
        var entries=tracked.FirstOrDefault()??throw new InvalidOperationException("没有读取到所选角色的已加载模型资源，请等待角色显示后重试。");
        var resources=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var entry in entries)
            foreach(var gamePath in entry.Value.Where(p=>p.StartsWith("chara/",StringComparison.Ordinal)))resources.TryAdd(gamePath,entry.Key);
        byte[] Read(string gamePath)
        {
            if(resources.TryGetValue(gamePath,out var actual) && Path.IsPathRooted(actual) && File.Exists(actual))return File.ReadAllBytes(actual);
            return data.GetFile<Lumina.Data.FileResource>(gamePath)?.Data??throw new FileNotFoundException("未找到角色模型的资源文件。");
        }
        foreach(var path in resources.Keys.Where(p=>p.EndsWith(".sklb",StringComparison.Ordinal)).Order(StringComparer.Ordinal).Take(32))
        {
            if(skeletons.Any(s=>s.SourceGamePath==path) || !data.FileExists(path))continue;
            // Additional head/hair/equipment/weapon bones still come from original SqPack.
            var bytes=data.GetFile<Lumina.Data.FileResource>(path)!.Data;var container=SklbContainer.Parse(bytes);
            var file=Path.Combine(directory,"ref-"+ModWorkspace.PartId(path)+".hkx");File.WriteAllBytes(file,container.Payload.ToArray());
            using var session=new HavokSession(file);skeletons.Add(session.Snapshot(container.Sha256,path));
        }
        var paths=resources.Keys.Where(p=>p.EndsWith(".mdl",StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        if(workspace.Project.ExportClothing)paths.Add(workspace.Project.TargetModelGamePath);
        long total=0;
        foreach(var path in paths.Order(StringComparer.Ordinal))
        {
            if(!path.StartsWith("chara/human/",StringComparison.Ordinal) && !path.StartsWith("chara/equipment/",StringComparison.Ordinal) && !path.StartsWith("chara/accessory/",StringComparison.Ordinal) && !path.StartsWith("chara/weapon/",StringComparison.Ordinal))continue;
            var slot=ModelSlot(path);var weapon=slot=="weapon";var equipment=path.StartsWith("chara/equipment/",StringComparison.Ordinal)||path.StartsWith("chara/accessory/",StringComparison.Ordinal);
            if(weapon && !workspace.Project.ReferenceGroups.HasFlag(ReferenceModelGroups.Weapons))continue;
            if(!weapon && !equipment && !workspace.Project.ReferenceGroups.HasFlag(ReferenceModelGroups.BodyAndHead))continue;
            if(equipment && !workspace.Project.ExportClothing && !workspace.Project.ReferenceGroups.HasFlag(ReferenceModelGroups.Equipment) && !workspace.Project.ReferenceGroups.HasFlag(ReferenceModelGroups.BodyAndHead))continue;
            var bytes=Read(path);total+=bytes.Length;
            if(total>128*1024*1024 || models.Count>=32)throw new InvalidDataException("所选模型数据过大，请减少参考组后重试。");
            var skin=new HashSet<string>(StringComparer.Ordinal);
            foreach(var name in backend.Materials(bytes))
            {
                var material=resources.Keys.FirstOrDefault(p=>p.EndsWith(name.TrimStart('/'),StringComparison.Ordinal));
                if(material is not null)
                    try {if(backend.IsSkinMaterial(Read(material)))skin.Add(name);}
                    catch(Exception e) when(e is IOException or TargetInvocationException or InvalidDataException) { }
                if(Regex.IsMatch(name,@"c\d{4}b\d{4}|bibo|gen3",RegexOptions.IgnoreCase))skin.Add(name);
            }
            models.Add(new(path,bytes,skin,slot));
            if(equipment)File.WriteAllBytes(Path.Combine(directory,"donor-"+ModWorkspace.PartId(path)+".mdl"),bytes);
        }
        var current=ReadCharacterSelection(selected.Source.SelectionKind);
        if(!selected.SameCharacterAndModel(current))throw new InvalidOperationException("角色在读取模型期间改变，请重新读取并导出。");
        File.WriteAllText(Path.Combine(directory,"model-skeletons.json"),JsonSerializer.Serialize(skeletons,SkeletonDocument.JsonOptions));
        return new(models,skeletons);
    }
    private void WriteModelReferences(ModelCapture capture,ModWorkspace workspace,string directory)
    {
        var models=new List<ReferenceModel>();var backend=capture.Models.Count>0?new PenumbraModelBackend():null;
        foreach(var input in capture.Models)
        {
            var equipment=input.GamePath.StartsWith("chara/equipment/",StringComparison.Ordinal)||input.GamePath.StartsWith("chara/accessory/",StringComparison.Ordinal);
            void Export(string group,bool editable,HashSet<string>? include=null)
            {
                var file="ref-"+ModWorkspace.PartId(group+":"+input.GamePath)+".glb";
                var bytes=backend!.Export(input.Bytes,capture.Skeletons,Path.Combine(directory,"model-tmp"),include);
                if(bytes is null)return;
                File.WriteAllBytes(Path.Combine(directory,file),bytes);
                models.Add(new(ModWorkspace.PartId(input.GamePath),group,input.Slot,SlotLabel(input.Slot),input.GamePath,file,editable));
            }
            if(equipment)
            {
                if(workspace.Project.ReferenceGroups.HasFlag(ReferenceModelGroups.BodyAndHead) && input.SkinMaterials.Count>0)Export("body",false,input.SkinMaterials);
                if(workspace.Project.ReferenceGroups.HasFlag(ReferenceModelGroups.Equipment))
                {
                    var all=backend!.Materials(input.Bytes).Except(input.SkinMaterials).ToHashSet(StringComparer.Ordinal);if(all.Count>0)Export("equipment",false,all);
                }
                var primary=input.GamePath==workspace.Project.TargetModelGamePath;
                if(workspace.Project.ExportClothing && (primary || input.Slot!=ModelSlot(workspace.Project.TargetModelGamePath)))Export("clothing",true);
            }
            else Export(input.Slot=="weapon"?"weapons":"body",false);
        }
        File.WriteAllText(Path.Combine(directory,"models.json"),JsonSerializer.Serialize(new ReferenceModels(document!.ProjectId,workspace.Project.Id,models),BridgeExchange.Json));
    }
    private static SkeletonDocument[] ReadModelSkeletons(string directory)
    {
        var file=Path.Combine(directory,"model-skeletons.json");
        if(File.Exists(file))return JsonSerializer.Deserialize<SkeletonDocument[]>(File.ReadAllText(file),SkeletonDocument.JsonOptions)!;
        return new[]{SkeletonDocument.Load(Path.Combine(directory,"reference-snapshot.json")),SkeletonDocument.Load(Path.Combine(directory,"snapshot.json"))};
    }
    private byte[] ReadTargetModel(string directory,string gamePath)
    {
        ModWorkspace.CheckModelPath(gamePath);var cached=Path.Combine(directory,"donor-"+ModWorkspace.PartId(gamePath)+".mdl");
        return File.Exists(cached)?File.ReadAllBytes(cached):data.GetFile<Lumina.Data.FileResource>(gamePath)?.Data??throw new FileNotFoundException("未找到目标装备的原版模型。");
    }
}
