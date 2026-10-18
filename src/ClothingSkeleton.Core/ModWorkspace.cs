using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClothingSkeleton.Core;

[Flags]
public enum ReferenceModelGroups {None=0,BodyAndHead=1,Equipment=2,Weapons=4}
public sealed record ModProject(Guid Id,string Name,string TargetModelGamePath,ReferenceModelGroups ReferenceGroups=ReferenceModelGroups.None,bool ExportClothing=false,int SchemaVersion=1);
public sealed record ModPartOutput(string PartId,string ModelGamePath,byte[]? Model,byte[]? Gltf,string? SkeletonGamePath,byte[]? Skeleton,int AuthoredBoneCount,MidlanderFemaleEst? Est=null);
public sealed record ModBuildResult(bool ModelWritten,bool SkeletonWritten,int AuthoredBoneCount,string[] Files,string[] RetiredFiles);

/// <summary>An owned Penumbra default option with separate authoring and native outputs.</summary>
public sealed class ModWorkspace
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){WriteIndented=true};
    private static readonly JsonSerializerOptions GameJson=new(){WriteIndented=true};
    public string Root {get;}
    public ModProject Project {get;private set;}
    public string Work=>Path.Combine(Root,".csw");
    public string ReturnFile=>Path.Combine(Work,"return","edited.gltf");
    public string PrimaryPartId=>PartId(Project.TargetModelGamePath);
    public string ModelGltf=>PartFile(PrimaryPartId,"clothing.gltf");
    public string ModelMdl=>PartFile(PrimaryPartId,"clothing.mdl");
    public string Skeleton=>PartFile(PrimaryPartId,"skeleton.sklb");
    public string PartFile(string id,string name)=>Path.Combine(Root,"files",id,name);
    public static string PartId(string gamePath)=>Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(gamePath)))[..16];
    public string StateFile=>Path.Combine(Work,"workflow.json");
    private ModWorkspace(string root,ModProject project){Root=Path.GetFullPath(root);Project=project;}

    public static ModWorkspace Create(string parent,string name,string targetModelGamePath,ReferenceModelGroups groups=ReferenceModelGroups.None,bool exportClothing=false)
    {
        name=name.Trim();
        if(name.Length is <1 or >80 || name.Any(char.IsControl) || name.Contains(':') || name.Contains('/') || name.Contains('\\'))throw new InvalidDataException("请输入不含路径的 Mod 名称（1–80 字）。");
        CheckModelPath(targetModelGamePath);
        var project=new ModProject(Guid.NewGuid(),name,targetModelGamePath,groups,exportClothing);
        var result=new ModWorkspace(Path.Combine(parent,"CSW-"+project.Id.ToString("N")[..16]),project);
        if(Directory.Exists(result.Root))throw new IOException("Mod 文件夹已存在。");
        Directory.CreateDirectory(result.Work);result.Save();
        File.WriteAllText(Path.Combine(result.Root,"meta.json"),JsonSerializer.Serialize(new{FileVersion=3,Name=name,Author="ClothingSkeleton contributors",Description="Clothing model and optional appended skeleton managed by Clothing Skeleton Workshop.",Version="0.7.0",Website="",ModTags=new[]{"Clothing","Skeleton"}},GameJson));
        result.WriteOption(new(),[]);
        return result;
    }

    public static ModWorkspace Load(string directory)
    {
        var project=JsonSerializer.Deserialize<ModProject>(File.ReadAllText(Path.Combine(directory,".csw","mod-project.json")),Json)??throw new InvalidDataException("无效的 Mod 项目。");
        if(project.Id==Guid.Empty || project.SchemaVersion!=1)throw new InvalidDataException("不支持的 Mod 项目格式。");
        CheckModelPath(project.TargetModelGamePath);return new(directory,project);
    }
    public void Configure(string modelPath,ReferenceModelGroups groups,bool exportClothing)
    {CheckModelPath(modelPath);Project=Project with{TargetModelGamePath=modelPath,ReferenceGroups=groups,ExportClothing=exportClothing};Save();}
    public void Save()=>AtomicText(Path.Combine(Work,"mod-project.json"),JsonSerializer.Serialize(Project,Json));
    public void BindSource(string sourceDirectory)
    {
        var relative=Path.GetRelativePath(Root,sourceDirectory).Replace('\\','/');CheckRelative(relative);
        AtomicText(Path.Combine(Work,"active-source.json"),JsonSerializer.Serialize(new{directory=relative},Json));
        File.WriteAllText(Path.Combine(sourceDirectory,"mod-project.json"),JsonSerializer.Serialize(Project,Json));
    }
    public string? SourceDirectory
    {
        get
        {
            var path=Path.Combine(Work,"active-source.json");if(!File.Exists(path))return null;
            using var json=JsonDocument.Parse(File.ReadAllText(path));var value=json.RootElement.GetProperty("directory").GetString()!;CheckRelative(value);return Path.GetFullPath(Path.Combine(Root,value));
        }
    }
    public static int AddedBoneCount(SkeletonDocument baseline,SkeletonDocument candidate)
    {
        _=SkeletonValidator.OrderedForAppend(candidate,baseline);
        var original=baseline.Bones.Select(b=>b.Id).ToHashSet();var helpers=candidate.AnchorBindings.Select(b=>b.BoneId).ToHashSet();
        return candidate.Bones.Count(b=>!original.Contains(b.Id) && !helpers.Contains(b.Id));
    }

    public ModBuildResult Commit(string skeletonGamePath,byte[]? mdl,byte[]? gltf,byte[]? sklb,int authoredCount,MidlanderFemaleEst? est=null)
        =>CommitParts([new(PrimaryPartId,Project.TargetModelGamePath,mdl,gltf,skeletonGamePath,sklb,authoredCount,est)]);

    public ModBuildResult CommitParts(IEnumerable<ModPartOutput> outputs)
    {
        var parts=outputs.ToArray();
        if(parts.Length>32 || parts.Select(p=>p.PartId).Distinct().Count()!=parts.Length)throw new InvalidDataException("Mod 部件重复或数量过多。");
        var assets=new Dictionary<string,byte[]>();var mappings=new Dictionary<string,string>();var manipulations=new List<object>();
        foreach(var part in parts)
        {
            CheckModelPath(part.ModelGamePath);
            if(part.PartId!=PartId(part.ModelGamePath))throw new InvalidDataException("Mod 部件与替换路径不一致。");
            if(part.AuthoredBoneCount<0 || (part.AuthoredBoneCount==0 && part.Skeleton is not null) || (part.AuthoredBoneCount>0 && part.Skeleton is null))throw new InvalidDataException("新增服装骨骼数量与 SKLB 输出不一致。");
            if((part.Model is null)!=(part.Gltf is null))throw new InvalidDataException("模型输出必须同时包含 glTF 和 MDL。");
            var prefix="files/"+part.PartId+"/";
            if(part.Model is not null){assets[prefix+"clothing.mdl"]=part.Model;assets[prefix+"clothing.gltf"]=part.Gltf!;mappings.Add(part.ModelGamePath,prefix+"clothing.mdl");}
            if(part.Skeleton is not null)
            {
                if(part.SkeletonGamePath is null)throw new InvalidDataException("缺少骨架替换路径。");
                CheckSkeletonPath(part.SkeletonGamePath);_=SklbContainer.Parse(part.Skeleton);
                if(mappings.ContainsKey(part.SkeletonGamePath))throw new InvalidDataException("多个部件不能各自覆盖同一个 SKLB 路径，请先合并其骨架输出。");
                assets[prefix+"skeleton.sklb"]=part.Skeleton;mappings.Add(part.SkeletonGamePath,prefix+"skeleton.sklb");
                if(part.Est is{} est)
                {
                    if(est.GamePath!=part.SkeletonGamePath)throw new InvalidDataException("骨架关联与本次输出不一致。");
                    
                    manipulations.Add(new{Type="Est",Manipulation=new{Entry=est.SkeletonId,Gender=est.Gender,Race=est.Race,SetId=est.SetId,Slot="Body"}});
                }
            }
            else if(part.Est is not null)throw new InvalidDataException("无新增骨骼时不得生成骨架关联。");
        }
        var owned=new Dictionary<string,string>();var ownership=Path.Combine(Work,"generated.json");
        if(File.Exists(ownership))owned=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(ownership),Json)??new();
        foreach(var name in owned.Keys)
            if(!Regex.IsMatch(name,@"^files/[0-9a-f]{16}/(?:clothing\.gltf|clothing\.mdl|skeleton\.sklb)$"))throw new InvalidDataException("工作文件清单包含不属于本工具的路径。");
        // Refuse to overwrite any pre-existing output that is not owned by this workspace.
        foreach(var (name,_) in assets)
            if(File.Exists(Path.Combine(Root,name)) && !owned.ContainsKey(name))throw new IOException("目标文件不是本工作台生成的，请使用新的 Mod 项目。");
        var retired=owned.Keys.Except(assets.Keys).Where(n=>File.Exists(Path.Combine(Root,n))).ToArray();
        var generation=Path.Combine(Work,"history",Guid.NewGuid().ToString("N")[..12]);Directory.CreateDirectory(generation);
        var written=new List<string>();var backed=new List<string>();var option=Path.Combine(Root,"default_mod.json");
        var previousOption=File.ReadAllBytes(option);var previousOwnership=File.Exists(ownership)?File.ReadAllBytes(ownership):null;
        try
        {
            foreach(var name in owned.Keys.Union(assets.Keys).Distinct())
            {
                var current=Path.Combine(Root,name);if(!File.Exists(current))continue;
                var saved=Path.Combine(generation,name);Directory.CreateDirectory(Path.GetDirectoryName(saved)!);File.Move(current,saved);backed.Add(name);
            }
            foreach(var (name,bytes) in assets)
            {var path=Path.Combine(Root,name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,bytes);written.Add(name);}
            var hashes=assets.ToDictionary(p=>p.Key,p=>Convert.ToHexStringLower(SHA256.HashData(p.Value)));
            AtomicText(ownership,JsonSerializer.Serialize(hashes,Json));
            WriteOption(mappings,manipulations.ToArray());
            return new(parts.Any(p=>p.Model is not null),parts.Any(p=>p.Skeleton is not null),parts.Sum(p=>p.AuthoredBoneCount),assets.Keys.ToArray(),retired);
        }
        catch
        {
            foreach(var name in written)File.Delete(Path.Combine(Root,name));
            foreach(var name in backed)File.Move(Path.Combine(generation,name),Path.Combine(Root,name),true);
            File.WriteAllBytes(option,previousOption);
            if(previousOwnership is not null)File.WriteAllBytes(ownership,previousOwnership);else File.Delete(ownership);
            throw;
        }
    }

    private void WriteOption(Dictionary<string,string> files,object[] manipulations)=>AtomicText(Path.Combine(Root,"default_mod.json"),JsonSerializer.Serialize(new{Name="",Description="",Priority=0,Files=files,FileSwaps=new Dictionary<string,string>(),Manipulations=manipulations},GameJson));
    public static void CheckModelPath(string path)
    {if(!Regex.IsMatch(path,@"^chara/(?:equipment/e\d{4}/model/c\d{4}e\d{4}_(?:top|dwn|glv|sho|met)|accessory/a\d{4}/model/c\d{4}a\d{4}_(?:ear|nek|wrs|rir|ril))\.mdl$"))throw new InvalidDataException("请选择明确的装备或配饰 MDL 游戏路径。");}
    public static void CheckSkeletonPath(string path)
    {if(!Regex.IsMatch(path,@"^chara/human/c\d{4}/skeleton/top/t\d{4}/skl_c\d{4}t\d{4}\.sklb$"))throw new InvalidDataException("无效的上衣骨架游戏路径。");}
    private static void CheckRelative(string path)
    {if(Path.IsPathRooted(path) || path.Contains(':') || path.Contains('\\') || path.Split('/').Any(p=>p is "" or "." or ".."))throw new InvalidDataException("Mod 文件位置必须位于当前项目内。");}
    private static void AtomicText(string path,string content)
    {Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path+".tmp",content);File.Move(path+".tmp",path,true);}
}
