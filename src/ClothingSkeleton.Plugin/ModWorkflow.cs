using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClothingSkeleton.Core;
using Dalamud.Bindings.ImGui;

namespace ClothingSkeleton.Plugin;

public sealed partial class Plugin
{
    private ModWorkspace? activeMod;
    private readonly Dictionary<Guid,ModWorkspace> modWorkspaces=[];
    private string newModName="新服装 Mod";
    private bool includeBody,includeEquipment,includeWeapons,exportClothing;
    private bool autoProcessMod=true;
    private string lastAttemptedModReturn="";
    private ModBuildResult? lastModBuild;
    private string modBuildCaption="尚未收到服装或骨架修改。";

    private string? PenumbraRoot()
    {
        try {var root=pluginInterface.GetIpcSubscriber<string>("Penumbra.GetModDirectory").InvokeFunc();return string.IsNullOrWhiteSpace(root)?null:Path.GetFullPath(root);}
        catch(Exception) {return null;}
    }
    private void RefreshMods()
    {
        modWorkspaces.Clear();
        foreach(var root in new[]{storage.Mods,PenumbraRoot()}.Where(p=>p is not null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if(!Directory.Exists(root))continue;
            foreach(var directory in Directory.GetDirectories(root!,"CSW-*"))
                try {var workspace=ModWorkspace.Load(directory);modWorkspaces[workspace.Project.Id]=workspace;}
                catch(Exception e) when(e is IOException or JsonException or ArgumentException) { }
        }
    }
    private ReferenceModelGroups SelectedReferenceGroups=>(includeBody?ReferenceModelGroups.BodyAndHead:0)|(includeEquipment?ReferenceModelGroups.Equipment:0)|(includeWeapons?ReferenceModelGroups.Weapons:0);
    private void CreateMod(bool localOnly=false)
    {
        var root=localOnly?storage.Mods:PenumbraRoot()??storage.Mods;
        var workspace=ModWorkspace.Create(root,newModName,modelPath,SelectedReferenceGroups,exportClothing);
        modWorkspaces[workspace.Project.Id]=workspace;SelectMod(workspace);RegisterMod(false);
        status="已创建独立 Mod 项目。选择人物和服装来源后，即可发送到 Blender。";
    }
    private void SelectMod(ModWorkspace workspace)
    {
        if(preview.Active)StopWorkflowPreview();
        activeMod=workspace;activeProjectDirectory=workspace.SourceDirectory;
        document=activeProjectDirectory is not null && File.Exists(Path.Combine(activeProjectDirectory,"snapshot.json"))?SkeletonDocument.Load(Path.Combine(activeProjectDirectory,"snapshot.json")):null;
        candidateDocument=null;lastCandidate=null;lastModBuild=null;lastValidatedReturnHash="";lastAttemptedModReturn="";
        modelPath=workspace.Project.TargetModelGamePath;exportClothing=workspace.Project.ExportClothing;
        includeBody=workspace.Project.ReferenceGroups.HasFlag(ReferenceModelGroups.BodyAndHead);includeEquipment=workspace.Project.ReferenceGroups.HasFlag(ReferenceModelGroups.Equipment);includeWeapons=workspace.Project.ReferenceGroups.HasFlag(ReferenceModelGroups.Weapons);
        if(File.Exists(workspace.StateFile))
        try {
            using var state=JsonDocument.Parse(File.ReadAllText(workspace.StateFile));
            if(state.RootElement.TryGetProperty("returnHash",out var hash))lastValidatedReturnHash=hash.GetString()??"";
            if(state.RootElement.TryGetProperty("result",out var result))lastModBuild=result.Deserialize<ModBuildResult>(BridgeExchange.Json);
            if(state.RootElement.TryGetProperty("candidate",out var path) && path.ValueKind==JsonValueKind.String)
            {
                var relative=path.GetString()!;
                if(!Path.IsPathRooted(relative) && !relative.Contains("..",StringComparison.Ordinal))lastCandidate=Path.Combine(workspace.Root,relative);
            }
        }
        catch(Exception e) when(e is IOException or JsonException or InvalidOperationException) {log.Warning("Ignoring invalid Mod state: {Detail}",SafeDetail(e.Message));}
        ConfigureProjectReturn(false);workflowStep=document is null?0:1;
        if(document is not null && activeProjectDirectory is not null)blenderBridge?.Publish(BridgeExchange.ReadProject(activeProjectDirectory),workspace.ReturnFile);
        SaveWorkflow();
    }
    private void RestoreMod(Guid? id)
    {
        try {RefreshMods();if(id is{} key && modWorkspaces.TryGetValue(key,out var workspace))SelectMod(workspace);}
        catch(Exception e) {activeMod=null;log.Warning("Could not restore Mod workflow: {Detail}",SafeDetail(e.Message));}
    }
    private void DrawModManager()
    {
        ImGui.BeginDisabled(busy);
        if(ImGui.BeginCombo("工作 Mod",activeMod?.Project.Name??"旧版骨架项目（尚未选择 Mod）"))
        {
            foreach(var item in modWorkspaces.Values)if(ImGui.Selectable(item.Project.Name,item.Project.Id==activeMod?.Project.Id))TryUi(()=>SelectMod(item));
            ImGui.EndCombo();
        }
        ImGui.SameLine();if(ImGui.SmallButton("刷新 Mod"))TryUi(RefreshMods);
        if(ImGui.TreeNode("新建服装 Mod"))
        {
            ImGui.InputText("Mod 名称",ref newModName,256);
            ImGui.InputText("主要替换的装备 MDL",ref modelPath,1024);
            if(ImGui.Button("创建独立 Mod 项目"))TryUi(()=>CreateMod());
            ImGui.TextWrapped("每个 Mod 独立保存各部件的 glTF、MDL 和需要的 SKLB。默认选项由工作台管理，不自动启用到角色。");
            ImGui.TreePop();
        }
        ImGui.EndDisabled();ImGui.Separator();
    }
    private void DrawModExportOptions()
    {
        if(activeMod is null)return;
        ImGui.TextUnformatted("服装导出内容");
        if(ImGui.RadioButton("仅骨骼",!exportClothing))exportClothing=false;
        ImGui.SameLine();if(ImGui.RadioButton("完整服装＋骨骼",exportClothing))exportClothing=true;
        ImGui.TextUnformatted("同时发送的参考模型（可选）");
        ImGui.Checkbox("身体与头部",ref includeBody);ImGui.SameLine();ImGui.Checkbox("穿着装备",ref includeEquipment);ImGui.SameLine();ImGui.Checkbox("武器",ref includeWeapons);
        ImGui.TextWrapped("参考来自所选角色当前加载的模型资源，以制作姿态显示。不同装备使用独立集合及独立骨架；参考模型不会默认随服装回传。");
        ImGui.TextWrapped("模型转换沿用各目标装备的材质路径；自定义贴图与游戏材质需另行制作。未新增服装骨骼时不生成 SKLB。");
        ImGui.Checkbox("收到 Blender 返回后自动检查并更新 Mod",ref autoProcessMod);
    }
    private void ScheduleModExport(bool send)
    {
        if(busy || disposed || activeMod is null)return;
        busy=true;operationFailed=false;var workspace=activeMod;
        _=ExportModAsync(workspace,send);
    }
    private async Task ExportModAsync(ModWorkspace workspace,bool send)
    {
        try
        {
            var snapshot=await framework.RunOnFrameworkThread(()=>
            {
                var selected=RequireCurrentCharacterSelection();
                workspace.Configure(modelPath,SelectedReferenceGroups,exportClothing);
                var chosen=sourceMode==3?selected.Source.GamePath:source.Trim();
                if(preview.Active)StopWorkflowPreview();RoundTrip(chosen,selected.Source);
                return CaptureModelReferences(workspace,selected,activeProjectDirectory!);
            });
            await Task.Run(()=>WriteModelReferences(snapshot,workspace,activeProjectDirectory!));
            await framework.RunOnFrameworkThread(()=>
            {if(!disposed){workspace.BindSource(activeProjectDirectory!);if(send)OpenBlender();else blenderBridge?.Publish(BridgeExchange.ReadProject(activeProjectDirectory!),workspace.ReturnFile);SaveWorkflow();WriteStatus("mod-exported","Mod 项目已导出；装备部件保持独立骨架与替换路径。");}});
        }
        catch(Exception e){operationFailed=true;status=SafeDetail(e.GetBaseException().Message);log.Error("Mod export failed: {Detail}",SafeDetail(e.ToString()));WriteStatus("failed",status);}
        finally {busy=false;}
    }
    private static string BundleHash(string file)
    {
        using var hash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(File.ReadAllBytes(file));var parts=Path.Combine(Path.GetDirectoryName(file)!,"parts.json");if(File.Exists(parts))hash.AppendData(File.ReadAllBytes(parts));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private void ScheduleModReturn()
    {
        if(busy || disposed || activeMod is null || document is null || activeProjectDirectory is null)return;
        busy=true;operationFailed=false;lastAttemptedModReturn=BundleHash(returnedPath);
        _=ImportModAsync(activeMod,activeProjectDirectory,returnedPath);
    }
    private async Task ImportModAsync(ModWorkspace workspace,string directory,string returned)
    {
        var oldCandidate=lastCandidate;var oldDocument=candidateDocument;var oldHash=lastValidatedReturnHash;var committed=false;
        try
        {
            blenderBridge?.PauseReturns(true);
            var decoded=Directory.GetFiles(Path.GetDirectoryName(returned)!).Where(p=>BridgeExchange.ReturnNames.Contains(Path.GetFileName(p)) || ModelExchange.GarmentFile(Path.GetFileName(p))).ToDictionary(p=>Path.GetFileName(p)!,p=>File.ReadAllBytes(p));
            _=ModelExchange.ValidateReturn(decoded!,document!,workspace.Project.Id);
            var candidate=GltfExchange.Import(returned,document!);var added=ModWorkspace.AddedBoneCount(document!,candidate);
            using var metadata=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"anchor-rig.json")));
            if(added>0 && metadata.RootElement.TryGetProperty("allowNewBones",out var allowed) && !allowed.GetBoolean())throw new InvalidDataException("此项目没有装备附加骨架。请先选择可追加骨骼的装备骨架模板，再创建新骨。");
            if(added>0)await framework.RunOnFrameworkThread(()=>Append(directory,returned));
            else {lastCandidate=null;candidateDocument=candidate;}
            var manifest=JsonSerializer.Deserialize<GarmentReturn>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(returned)!,"parts.json")),BridgeExchange.Json)!;
            if(manifest.ProjectId!=document!.ProjectId || manifest.ModId!=workspace.Project.Id)throw new InvalidDataException("返回内容不属于当前 Mod。");
            var partInputs=manifest.Parts.ToDictionary(p=>p.Id,p=>(p.GamePath,Bytes:File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(returned)!,p.File))));
            if(!manifest.FullClothing && File.Exists(Path.Combine(workspace.Root,"default_mod.json")))
            {
                using var current=JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace.Root,"default_mod.json")));
                foreach(var entry in current.RootElement.GetProperty("Files").EnumerateObject().Where(p=>p.Name.EndsWith(".mdl",StringComparison.Ordinal)))
                {
                    var id=ModWorkspace.PartId(entry.Name);var path=workspace.PartFile(id,"clothing.gltf");
                    if(File.Exists(path))partInputs[id]=(entry.Name,File.ReadAllBytes(path));
                }
            }
            var donors=await framework.RunOnFrameworkThread(()=>partInputs.ToDictionary(p=>p.Key,p=>ReadTargetModel(directory,p.Value.GamePath)));
            var known=ReadModelSkeletons(directory).SelectMany(s=>s.Bones.Select(b=>b.Name)).Concat(candidate.Bones.Select(b=>b.Name)).Distinct().ToArray();
            var outputs=await Task.Run(()=>
            {
                var backend=partInputs.Count>0?new PenumbraModelBackend():null;var result=new List<ModPartOutput>();
                foreach(var (id,input) in partInputs)
                {
                    var mdl=backend!.Import(input.Bytes,donors[id],known,Path.Combine(directory,"model-build",id));
                    result.Add(new(id,input.GamePath,mdl,input.Bytes,null,null,0));
                }
                return result;
            });
            if(added>0)
            {
                var target=Regex.Match(workspace.Project.TargetModelGamePath,@"/c(?<race>\d{4})e(?<set>\d{4})_top\.mdl$");
                if(!target.Success)throw new InvalidDataException("新增骨骼需要主要部件为支持装备附加骨架的上衣。");
                var sourceId=ushort.Parse(Regex.Match(document.SourceGamePath,@"t(\d{4})\.sklb$").Groups[1].Value);
                var skeletonId=(ushort)(9000+Convert.ToUInt16(workspace.Project.Id.ToString("N")[..3],16)%999);
                var race=metadata.RootElement.GetProperty("characterSource").GetProperty("raceSexId").GetInt32();
                var est=new MidlanderFemaleEst(ushort.Parse(target.Groups["set"].Value),skeletonId,sourceId,race);
                var primary=outputs.FindIndex(p=>p.PartId==workspace.PrimaryPartId);var bytes=File.ReadAllBytes(lastCandidate!);
                if(primary>=0)outputs[primary]=outputs[primary] with{SkeletonGamePath=est.GamePath,Skeleton=bytes,AuthoredBoneCount=added,Est=est};
                else outputs.Add(new(workspace.PrimaryPartId,workspace.Project.TargetModelGamePath,null,null,est.GamePath,bytes,added,est));
            }
            var build=workspace.CommitParts(outputs);committed=true;
            lastValidatedReturnHash=BundleHash(returned);lastModBuild=build;workflowStep=2;
            modBuildCaption=$"Mod 已更新：{outputs.Count(p=>p.Model is not null)} 个独立服装部件；"+(added==0?"无新增服装骨骼，未生成 SKLB。":$"新增 {added} 根服装骨骼，已生成 SKLB。");
            File.WriteAllText(workspace.StateFile,JsonSerializer.Serialize(new{returnHash=lastValidatedReturnHash,candidate=lastCandidate is null?null:Path.GetRelativePath(workspace.Root,lastCandidate).Replace('\\','/'),result=build},BridgeExchange.Json));
            await framework.RunOnFrameworkThread(()=>{RegisterMod(false);SaveWorkflow();status=modBuildCaption;WriteStatus("mod-import-passed",status);});
        }
        catch(Exception e)
        {if(!committed){lastCandidate=oldCandidate;candidateDocument=oldDocument;lastValidatedReturnHash=oldHash;}operationFailed=true;status=SafeDetail(e.GetBaseException().Message);WriteStatus("failed",status);log.Error("Mod return failed: {Detail}",SafeDetail(e.ToString()));}
        finally {blenderBridge?.PauseReturns(false);busy=false;}
    }
    private void RegisterMod(bool openWindow)
    {
        if(activeMod is null)return;
        var root=PenumbraRoot();var parent=Path.GetDirectoryName(activeMod.Root);
        if(root is null || !string.Equals(root,parent,StringComparison.OrdinalIgnoreCase))return;
        var name=Path.GetFileName(activeMod.Root);var mods=pluginInterface.GetIpcSubscriber<Dictionary<string,string>>("Penumbra.GetModList").InvokeFunc();
        var result=mods.ContainsKey(name)?pluginInterface.GetIpcSubscriber<string,string,int>("Penumbra.ReloadMod.V5").InvokeFunc(name,""):pluginInterface.GetIpcSubscriber<string,int>("Penumbra.AddMod").InvokeFunc(name);
        if(result!=0)throw new InvalidOperationException($"文件已保存，但 Penumbra 未完成登记（{result}），可稍后重试。");
        if(openWindow)pluginInterface.GetIpcSubscriber<int,string,string,int>("Penumbra.OpenMainWindow.V5").InvokeFunc(1,name,"");
    }
    private void DrawModReturnStep()
    {
        ImGui.TextWrapped(modBuildCaption);
        ImGui.TextWrapped(returnCaption);
        ImGui.BeginDisabled(!returnReady);
        if(PrimaryButton("检查并更新当前 Mod"))ScheduleAppend();
        ImGui.EndDisabled();
        if(lastModBuild is{} result)ImGui.TextWrapped($"默认输出：{result.Files.Length} 个文件；"+(result.SkeletonWritten?"含自定义 SKLB":"无新增骨骼，不含 SKLB"));
        if(ImGui.Button("在 Penumbra 查看本 Mod"))TryUi(()=>RegisterMod(true));
        ImGui.SameLine();if(ImGui.Button("打开 Mod 文件夹"))TryUi(()=>OpenFolder(activeMod!.Root));
        if(ImGui.Button("保存 Mod 安装包"))TryUi(ExportModArchive);
        ImGui.TextWrapped("启用到所需角色集合后再检查服装效果。各部件按各自装备路径替换；游戏中的同一个骨架资源只保留一份合并输出，Blender 中仍是独立骨架。");
    }
    private void ExportModArchive()
    {
        if(activeMod is null || lastModBuild is null)throw new InvalidOperationException("请先完成 Mod 回导检查。");
        var directory=Path.Combine(storage.Root,"ModPackages");Directory.CreateDirectory(directory);
        var output=Path.Combine(directory,"CSW-"+activeMod.Project.Id.ToString("N")[..12]+"-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-ffff")+".pmp");
        using var archive=ZipFile.Open(output,ZipArchiveMode.Create);
        foreach(var file in new[]{"meta.json","default_mod.json"}.Concat(lastModBuild.Files))archive.CreateEntryFromFile(Path.Combine(activeMod.Root,file),file,CompressionLevel.Optimal);
        lastPackage=output;status="Mod 安装包已保存，参考模型和内部工作文件不包含在安装包中。";
    }
}
