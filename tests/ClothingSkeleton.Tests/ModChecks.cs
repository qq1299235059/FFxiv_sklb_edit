using System.Text.Json;
using System.Security.Cryptography;
using ClothingSkeleton.Core;
internal static class ModChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject,SkeletonDocument baseline,SkeletonDocument candidate,byte[] sklb,string directory)
    {
        const string top="chara/equipment/e0100/model/c0201e0100_top.mdl",glove="chara/equipment/e0100/model/c0201e0100_glv.mdl";
        var workspace=ModWorkspace.Create(Path.Combine(directory,"mods"),"Two parts",top,ReferenceModelGroups.BodyAndHead,true);
        var other=ModWorkspace.Create(Path.Combine(directory,"mods"),"Other",top);
        check(workspace.Root!=other.Root && workspace.ReturnFile!=other.ReturnFile,"Mod projects have independent default files and return directories");
        check(ModWorkspace.Load(workspace.Root).Project==workspace.Project,"Mod workspace reload retains portable identity and options");
        check(ModWorkspace.AddedBoneCount(baseline,baseline)==0 && ModWorkspace.AddedBoneCount(baseline,candidate)==2,"only appended authored bones require SKLB generation");
        var p=workspace.PrimaryPartId;var g=ModWorkspace.PartId(glove);var est=new MidlanderFemaleEst(100,9100,100);
        var withSkeleton=workspace.CommitParts([new(p,top,[1,2],[3,4],est.GamePath,sklb,2,est),new(g,glove,[5,6],[7,8],null,null,0)]);
        using(var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace.Root,"default_mod.json"))))
        {
            var files=json.RootElement.GetProperty("Files");
            check(files.EnumerateObject().Count()==3 && files.GetProperty(top).GetString()!=files.GetProperty(glove).GetString(),"separate equipment models map to separate default Mod files");
            check(!files.TryGetProperty(est.SourceGamePath!,out _) && json.RootElement.GetProperty("Manipulations").GetArrayLength()==1,"custom EST does not replace the original shared equipment skeleton path");
        }
        var withoutSkeleton=workspace.CommitParts([new(p,top,[9],[10],null,null,0),new(g,glove,[11],[12],null,null,0)]);
        check(!withoutSkeleton.SkeletonWritten && !File.Exists(workspace.Skeleton) && withoutSkeleton.RetiredFiles.Length==1,"removing all custom bones retires the previous SKLB output");
        using(var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace.Root,"default_mod.json"))))
            check(json.RootElement.GetProperty("Files").EnumerateObject().Count()==2 && json.RootElement.GetProperty("Manipulations").GetArrayLength()==0,"no-added-bones return removes stale skeleton redirection and EST while preserving models");
        reject(()=>workspace.CommitParts([new(p,top,[1],null,null,null,0)]),"partial glTF and MDL pairs cannot update a Mod");
        reject(()=>workspace.CommitParts([new(p,top,null,null,est.GamePath,sklb,0)]),"zero authored bones cannot generate SKLB");
        reject(()=>workspace.CommitParts([new(p,top,null,null,null,null,2)]),"added bones require a successfully converted skeleton");
        reject(()=>workspace.CommitParts([new(g,top,[1],[2],null,null,0)]),"part identity cannot redirect a different equipment path");
        reject(()=>workspace.CommitParts([new(p,top,null,null,est.GamePath,sklb,2,est),new(g,glove,null,null,est.GamePath,sklb,2,est)]),"different parts cannot overwrite the same skeleton resource separately");
        reject(()=>ModWorkspace.CheckModelPath("../outside.mdl"),"equipment output paths cannot escape virtual game resources");
        File.WriteAllText(Path.Combine(workspace.Work,"generated.json"),"{\"meta.json\":\"bad\"}");
        reject(()=>workspace.CommitParts([]),"tampered ownership cannot retire unrelated Mod metadata");
        const string gltf="""{"asset":{"version":"2.0"},"meshes":[{"primitives":[{"attributes":{"POSITION":0,"JOINTS_0":1,"WEIGHTS_0":2}}]}],"skins":[{}],"buffers":[{"uri":"data:application/octet-stream;base64,AA=="}]}""";
        var bytes=System.Text.Encoding.UTF8.GetBytes(gltf);ModelExchange.CheckSelfContained(bytes);
        check(true,"weighted per-part glTF accepts embedded buffers");
        reject(()=>ModelExchange.CheckSelfContained(System.Text.Encoding.UTF8.GetBytes(gltf.Replace("data:application/octet-stream;base64,AA==","../outside.bin"))),"garment glTF cannot load external local paths");
        reject(()=>ModelExchange.CheckSelfContained(System.Text.Encoding.UTF8.GetBytes(gltf.Replace("\"skins\":[{}]","\"skins\":[{},{}]"))),"a garment part cannot share multiple authoring rigs");
        var part=new GarmentPart(p,"Top",top,"cloth-"+p+".gltf",Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var fileset=BridgeExchange.ReturnNames.ToDictionary(n=>n,_=>Array.Empty<byte>());fileset[part.File]=bytes;
        fileset["parts.json"]=JsonSerializer.SerializeToUtf8Bytes(new GarmentReturn(baseline.ProjectId,workspace.Project.Id,true,[part]),BridgeExchange.Json);
        check(ModelExchange.ValidateReturn(fileset,baseline,workspace.Project.Id).Parts.Count==1,"full garment return verifies project, Mod, part path and content hash");
        reject(()=>ModelExchange.ValidateReturn(fileset,baseline,other.Project.Id),"one Mod cannot receive another Mod's garment bundle");
        fileset[part.File]=[1];reject(()=>ModelExchange.ValidateReturn(fileset,baseline,workspace.Project.Id),"tampered garment contents cannot pass a completed return manifest");
    }
}
