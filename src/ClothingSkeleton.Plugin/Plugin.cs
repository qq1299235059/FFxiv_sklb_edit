using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClothingSkeleton.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ClothingSkeleton.Plugin;

public sealed partial class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly IDataManager data;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly IObjectTable objects;
    private readonly ITargetManager targets;
    private readonly string outputRoot;
    private readonly PluginStorage storage;
    private bool open = true;
    private bool busy;
    private string source = "";
    private string localTargetGamePath = "";
    private string status = "选择骨架后，创建项目即可开始。";
    private SkeletonDocument? document;
    private SkeletonDocument? candidateDocument;
    private string? activeProjectDirectory;
    private string returnedPath = "";
    private bool initialSizeSet;
    private DateTime nextCommandCheck;
    private readonly string commandFile;
    private readonly PenumbraPreview preview;
    private Dictionary<Guid,string> collections=[];
    private Guid selectedCollection;
    private int previewPriority=100;
    private string? lastCandidate;
    private string modelPath="chara/equipment/e0100/model/c0201e0100_top.mdl";
    private string modelReport="尚未检查服装模型。";
    private string runtimeReport="尚未检查角色实时骨骼。文件校验通过不代表角色已加载。";
    private bool disposed;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IDataManager data, IFramework framework, IPluginLog log,IObjectTable objects,ITargetManager targets)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.data = data;
        this.framework = framework;
        this.log = log;
        this.objects=objects;
        this.targets=targets;
        preview=new(pluginInterface);
        storage=new PluginStorage(pluginInterface.AssemblyLocation.DirectoryName??throw new InvalidOperationException("无法定位插件安装目录。"));
        outputRoot=storage.Projects;
        commandFile=Path.Combine(storage.Root,"Development","runtime-command.json");
        Directory.CreateDirectory(outputRoot);
        activeProjectDirectory = Directory.GetDirectories(outputRoot).OrderDescending().FirstOrDefault(d => File.Exists(Path.Combine(d,"snapshot.json")));
        if (activeProjectDirectory is not null)
        {
            document = SkeletonDocument.Load(Path.Combine(activeProjectDirectory,"snapshot.json"));
            source = document.SourceGamePath;
        }
        if(activeProjectDirectory is not null) lastCandidate=Directory.GetDirectories(activeProjectDirectory,"appended-*").OrderDescending().Select(d=>Path.Combine(d,"candidate.sklb")).FirstOrDefault(File.Exists);
        if(lastCandidate is not null) candidateDocument=SkeletonDocument.Load(Path.Combine(Path.GetDirectoryName(lastCandidate)!,"validated-exchange.json"));
        InitializeWorkflow();
        commands.AddHandler("/csw", new CommandInfo(OnCommand) { HelpMessage = "打开服装骨架工作台。/csw selftest 运行原样往返检查。" });
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenMainUi += Open;
        pluginInterface.UiBuilder.OpenConfigUi += Open;
        framework.Update+=PollDevelopmentCommand;
        WriteStatus("loaded", "Plugin constructed successfully.");
        log.Information("ClothingSkeleton loaded. Runtime files use the installed plugin directory.");
    }

    private void Open() => OpenWorkshop();
    private void PollDevelopmentCommand(IFramework sender)
    {
        if(disposed || busy || DateTime.UtcNow<nextCommandCheck) return;
        nextCommandCheck=DateTime.UtcNow.AddSeconds(1);
        PollWorkflow();
        // Development requests require an explicit opt-in in this installation's local settings.
        try
        {
            using var settings=JsonDocument.Parse(File.ReadAllText(storage.Settings));
            developmentMode=settings.RootElement.TryGetProperty("EnableDevelopmentCommands",out var enabled) && enabled.ValueKind==JsonValueKind.True;
        }
        catch(Exception e) when(e is IOException or JsonException) { developmentMode=false; }
        if(!developmentMode || !File.Exists(commandFile)) return;
        try
        {
            if(new FileInfo(commandFile).Length>4096) throw new InvalidDataException("Development request is too large.");
            var claimed=commandFile+".processing";
            File.Move(commandFile,claimed,true);
            using var command=JsonDocument.Parse(File.ReadAllText(claimed));
            var action=command.RootElement.GetProperty("action").GetString();
            if(action=="append") ScheduleAppend();
            else if(action=="select-character") {ApplyCharacterSelection(command.RootElement.GetProperty("selection").GetString()!);}
            else if(action=="inspect") { sourceMode=1;source=command.RootElement.GetProperty("path").GetString()!; ScheduleRoundTrip(); }
            else if(action=="package") ExportPackage();
            else if(action=="penumbra-status")
            {
                collections=preview.Collections();
                WriteStatus("penumbra-ready",$"Detected {collections.Count} collection(s); no preview was applied.");
            }
            else if(action=="preview-selftest")
            {
                CheckCandidate();
                var detail=preview.SelfTest(document!.SourceGamePath,lastCandidate!);
                File.WriteAllText(Path.Combine(outputRoot,"penumbra-selftest.json"),JsonSerializer.Serialize(new{state="passed",detail,assignedToActor=false,timestamp=DateTimeOffset.UtcNow},new JsonSerializerOptions{WriteIndented=true}));
                WriteStatus("preview-selftest-passed",detail);
            }
            else if(action=="model-check") { if(command.RootElement.TryGetProperty("path",out var requestedModel))modelPath=requestedModel.GetString()!; CheckModel(); }
            else if(action=="est-package") ExportFemaleEstPackage();
            else if(action=="install-est")
            {
                var receipt=Path.Combine(Path.GetDirectoryName(outputRoot)!,"female-est-test","est-assignment.json");
                using var saved=JsonDocument.Parse(File.ReadAllText(receipt));
                var path=storage.Resolve(saved.RootElement.GetProperty("package").GetString())??throw new InvalidDataException("无效的安装包位置。");
                if(!File.Exists(path)) throw new FileNotFoundException("Export the EST test first.");
                var result=pluginInterface.GetIpcSubscriber<string,int>("Penumbra.InstallMod.V5").InvokeFunc(path);
                if(result!=0) throw new InvalidOperationException($"Penumbra install queue rejected the package: {result}");
                WriteStatus("est-install-queued",path);
            }
            else if(action=="verify-est") VerifyFemaleEst(false);
            else if(action=="enable-est") VerifyFemaleEst(true);
            else if(action=="est-effect-test") VerifyEstEffect();
            else if(action=="runtime-bones")
            {
                CaptureRuntimeBones();
            }
            else if(action=="runtime-preview")
            {
                CheckCandidate();
                var effective=pluginInterface.GetIpcSubscriber<int,(bool,bool,(Guid,string))>("Penumbra.GetCollectionForObject.V5").InvokeFunc(0);
                if(!effective.Item1)throw new InvalidOperationException("Local player is unavailable.");
                preview.Start(effective.Item3.Item1,document!.SourceGamePath,lastCandidate!,3000);
                pluginInterface.GetIpcSubscriber<int,int,object?>("Penumbra.RedrawObject.V5").InvokeAction(0,0);
                WriteStatus("runtime-preview-applied","已将验证后的测试骨架临时指向角色实际使用的原版装备路径，并请求重绘本体。");
            }
            else if(action=="stop-preview")
            {
                preview.Stop();
                WriteStatus("preview-stopped","已撤销本插件的临时路径替换。");
            }
            else if(action=="show-ui") { open=true; WriteStatus("ui-opened","服装骨架工作台已打开。"); }
            else if(action=="workflow-status") WriteWorkflowStatus();
            else if(action=="reload-est")
            {
                var result=pluginInterface.GetIpcSubscriber<string,string,int>("Penumbra.ReloadMod.V5").InvokeFunc("CSW Midlander Female Odin EST 9100","");
                if(result!=0)throw new InvalidOperationException($"Cannot reload the installed EST test: {result}");
                pluginInterface.GetIpcSubscriber<int,int,object?>("Penumbra.RedrawObject.V5").InvokeAction(0,0);
                WriteStatus("est-reloaded","已重新加载中原女测试 Mod，并请求重绘当前角色。");
            }
            else throw new InvalidDataException("Unknown development operation.");
        }
        catch(Exception e) { WriteStatus("command-failed",e.ToString()); }
    }
    private void OnCommand(string command, string arguments)
    {
        OpenWorkshop();
        if (arguments.Trim().Equals("selftest", StringComparison.OrdinalIgnoreCase)) ScheduleRoundTrip();
        else if (arguments.Trim().Equals("import",StringComparison.OrdinalIgnoreCase)) ScheduleAppend();
    }

    private void Draw()
    {
        if (!open) return;
        if (!initialSizeSet)
        {
            var viewport=ImGui.GetMainViewport();
            ImGui.SetNextWindowSize(new Vector2(Math.Min(660,viewport.WorkSize.X-24),Math.Min(620,viewport.WorkSize.Y-24)));
            ImGui.SetNextWindowPos(viewport.WorkPos+viewport.WorkSize/2,ImGuiCond.Always,new Vector2(0.5f));
            initialSizeSet=true;
        }
        if (ImGui.Begin("服装骨架工作台###ClothingSkeleton", ref open)) DrawWorkflow();
        ImGui.End();
        DrawBlenderSetupPopup();
        fileDialogs.Draw();
    }

    private void TryUi(Action action)
    {
        try { operationFailed=false;action(); }
        catch(Exception e) { operationFailed=true;status=SafeDetail(e.Message); log.Error("ClothingSkeleton operation failed: {Detail}",SafeDetail(e.ToString())); }
    }

    private void CheckCandidate()
    {
        if(candidateDocument?.AttachmentTargetBone is not null)throw new InvalidOperationException("旧版整套换挂点的候选已停用，请用新版完整挂点骨架重新创建项目。");
        if(lastCandidate is null || document is null) throw new InvalidOperationException("请先回导并检查 Blender 修改。");
        var report=Path.Combine(Path.GetDirectoryName(lastCandidate)!,"validation.json");
        using var verified=JsonDocument.Parse(File.ReadAllText(report));
        var hash=SklbContainer.Parse(File.ReadAllBytes(lastCandidate)).Sha256;
        if(verified.RootElement.GetProperty("candidateSha256").GetString()!=hash) throw new InvalidDataException("骨架文件在检查后发生变化，请重新回导检查。");
        if(verified.RootElement.GetProperty("sourceGamePath").GetString()!=document.SourceGamePath || verified.RootElement.GetProperty("sourceSha256").GetString()!=document.SourceSha256) throw new InvalidDataException("骨架与当前项目不匹配，请重新回导检查。");
    }

    private void ExportPackage()
    {
        CheckCandidate();
        var path=PenumbraModPackage.Export(document!.SourceGamePath,lastCandidate!,Path.Combine(Path.GetDirectoryName(outputRoot)!,"mods","clothing-skeleton-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-ffff")));
        status="测试包已导出："+Path.GetFileName(path);
        lastPackage=path;
        WriteStatus("package-exported",status);
    }

    private void ExportFemaleEstPackage()
    {
        CheckCandidate();
        const string originalPath="chara/human/c0201/skeleton/top/t0100/skl_c0201t0100.sklb";
        if(document!.SourceGamePath!=originalPath || candidateDocument is null || candidateDocument.Bones.Count<=document.Bones.Count)
            throw new InvalidOperationException("先读取中原女原版奥丁骨架 c0201/t0100，并实际追加至少一根骨骼。");
        var assignment=new MidlanderFemaleEst(100,9100,100);
        if(data.FileExists(assignment.GamePath)) throw new InvalidOperationException("Test skeleton ID 9100 already exists in the original game.");
        var est=data.GetFile<Lumina.Data.FileResource>("chara/xls/charadb/extra_top.est")?.Data ?? throw new FileNotFoundException("Original top EST is missing.");
        var count=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(est);
        if(count<0 || 4L+6L*count!=est.Length) throw new InvalidDataException("Unexpected original EST size.");
        var originalEntry=-1;
        for(var i=0;i<count;i++)
        {
            var set=System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(est.AsSpan(4+4*i));
            var race=System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(est.AsSpan(6+4*i));
            if(set==100 && race==201)originalEntry=System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(est.AsSpan(4+4*count+2*i));
        }
        if(originalEntry!=100) throw new InvalidDataException("Original female Odin EST does not match the selected template.");
        var directory=Path.Combine(Path.GetDirectoryName(outputRoot)!,"female-est-test");
        Directory.CreateDirectory(directory);
        var destination=Path.Combine(directory,"CSW-Midlander-Female-Odin-EST9100");
        if(Directory.Exists(destination) || File.Exists(destination+".pmp"))destination+="-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-ffff");
        var path=PenumbraModPackage.Export(assignment.GamePath,lastCandidate!,destination,assignment);
        File.WriteAllText(Path.Combine(directory,"est-assignment.json"),JsonSerializer.Serialize(new{sourceGamePath=originalPath,targetGamePath=assignment.GamePath,actualLoadedPathRedirect=assignment.SourceGamePath,setId=100,race="Midlander",gender="Female",slot="Body",originalSkeletonId=100,newSkeletonId=9100,candidatePath=storage.Relative(lastCandidate),candidateSha256=SklbContainer.Parse(File.ReadAllBytes(lastCandidate!)).Sha256,package=storage.Relative(path),timestamp=DateTimeOffset.UtcNow},new JsonSerializerOptions{WriteIndented=true}));
        status="中原女奥丁 EST 测试包已导出：e0100 的 Body 骨架 100 → 9100。";
        lastPackage=path;
        WriteStatus("est-package-exported",status);
    }

    private void VerifyFemaleEst(bool enable)
    {
        const string modName="CSW Midlander Female Odin EST 9100";
        var mods=pluginInterface.GetIpcSubscriber<Dictionary<string,string>>("Penumbra.GetModList").InvokeFunc();
        var mod=mods.SingleOrDefault(p=>p.Value==modName);
        if(mod.Key is null) throw new InvalidOperationException("The EST test mod is not installed yet.");
        var effective=pluginInterface.GetIpcSubscriber<int,(bool,bool,(Guid,string))>("Penumbra.GetCollectionForObject.V5").InvokeFunc(0);
        if(!effective.Item1 || effective.Item3.Item1==Guid.Empty) throw new InvalidOperationException("Local player has no effective collection.");
        var collection=effective.Item3.Item1;
        var directory=Path.Combine(Path.GetDirectoryName(outputRoot)!,"female-est-test");
        if(enable)
        {
            var before=pluginInterface.GetIpcSubscriber<Guid,string,string,bool,(int,(bool,int,Dictionary<string,List<string>>,bool)?)>("Penumbra.GetCurrentModSettings.V5").InvokeFunc(collection,mod.Key,"",false);
            File.WriteAllText(Path.Combine(directory,"deployment-before.json"),JsonSerializer.Serialize(new{collection,collectionName=effective.Item3.Item2,modDirectory=mod.Key,result=before.Item1,settings=before.Item2},new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
            var priority=pluginInterface.GetIpcSubscriber<Guid,string,string,int,int>("Penumbra.TrySetModPriority.V5").InvokeFunc(collection,mod.Key,"",2000);
            if(priority is not (0 or 1))throw new InvalidOperationException($"Cannot set test priority: {priority}");
            var enabled=pluginInterface.GetIpcSubscriber<Guid,string,string,bool,int>("Penumbra.TrySetMod.V5").InvokeFunc(collection,mod.Key,"",true);
            if(enabled is not (0 or 1))throw new InvalidOperationException($"Cannot enable EST test: {enabled}");
            pluginInterface.GetIpcSubscriber<int,int,object?>("Penumbra.RedrawObject.V5").InvokeAction(0,0);
        }
        var target=new MidlanderFemaleEst(100,9100).GamePath;
        var resolved=pluginInterface.GetIpcSubscriber<Guid,string,(int,string?)>("Penumbra.ResolvePath").InvokeFunc(collection,target);
        var meta=pluginInterface.GetIpcSubscriber<string>("Penumbra.GetPlayerMetaManipulations").InvokeFunc();
        File.WriteAllText(Path.Combine(directory,"effective-meta.base64"),meta);
        var resources=pluginInterface.GetIpcSubscriber<ushort[],Dictionary<string,HashSet<string>>?[]>("Penumbra.GetGameObjectResourcePaths.V5").InvokeFunc([0]);
        var matching=resources[0]?.Where(p=>p.Key.Contains("t9100",StringComparison.OrdinalIgnoreCase) || p.Key.EndsWith("skeleton.sklb",StringComparison.OrdinalIgnoreCase) || p.Value.Any(v=>v.Contains("t9100",StringComparison.OrdinalIgnoreCase))).ToDictionary(p=>p.Key,p=>p.Value);
        var settings=pluginInterface.GetIpcSubscriber<Guid,string,string,bool,(int,(bool,int,Dictionary<string,List<string>>,bool)?)>("Penumbra.GetCurrentModSettings.V5").InvokeFunc(collection,mod.Key,"",false);
        File.WriteAllText(Path.Combine(directory,"deployment.json"),JsonSerializer.Serialize(new{collection,collectionName=effective.Item3.Item2,modDirectory=mod.Key,modName,targetGamePath=target,resolveCode=resolved.Item1,resolvedPath=resolved.Item2,resolvedSha256=resolved.Item2 is{} path && File.Exists(path)?SklbContainer.Parse(File.ReadAllBytes(path)).Sha256:null,settings=settings.Item2,matchingLoadedResources=matching,timestamp=DateTimeOffset.UtcNow},new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
        pluginInterface.GetIpcSubscriber<int,string,string,int>("Penumbra.OpenMainWindow.V5").InvokeFunc(1,mod.Key,"");
        if(enable && (resolved.Item1!=0 || resolved.Item2 is null || !File.Exists(resolved.Item2)))throw new InvalidOperationException("EST test enabled, but its SKLB path did not resolve; see deployment.json.");
        status=$"中原女 EST 测试已核对：集合 {effective.Item3.Item2}，t9100 路径解析 code={resolved.Item1}。";
        WriteStatus(enable?"est-enabled":"est-verified",status);
    }

    private void VerifyEstEffect()
    {
        const string name="CSW Midlander Female Odin EST 9100";
        var mods=pluginInterface.GetIpcSubscriber<Dictionary<string,string>>("Penumbra.GetModList").InvokeFunc();
        var mod=mods.Single(p=>p.Value==name).Key;
        var collection=pluginInterface.GetIpcSubscriber<int,(bool,bool,(Guid,string))>("Penumbra.GetCollectionForObject.V5").InvokeFunc(0);
        if(!collection.Item1)throw new InvalidOperationException("Local player is unavailable.");
        var settings=pluginInterface.GetIpcSubscriber<Guid,string,string,bool,(int,(bool,int,Dictionary<string,List<string>>,bool)?)>("Penumbra.GetCurrentModSettings.V5").InvokeFunc(collection.Item3.Item1,mod,"",false);
        if(settings.Item2?.Item1!=true)throw new InvalidOperationException("Enable the EST test mod before testing its effective metadata.");
        var get=pluginInterface.GetIpcSubscriber<string>("Penumbra.GetPlayerMetaManipulations");
        var set=pluginInterface.GetIpcSubscriber<Guid,string,string,bool,int>("Penumbra.TrySetMod.V5");
        var active=get.InvokeFunc();
        string without;
        try
        {
            var result=set.InvokeFunc(collection.Item3.Item1,mod,"",false);
            if(result is not(0 or 1))throw new InvalidOperationException($"Test metadata toggle rejected: {result}");
            without=get.InvokeFunc();
        }
        finally
        {
            var result=set.InvokeFunc(collection.Item3.Item1,mod,"",true);
            if(result is not(0 or 1))throw new InvalidOperationException($"Could not restore the EST test mod: {result}");
        }
        var restored=get.InvokeFunc();
        byte[] Expand(string text)
        {
            using var stream=new MemoryStream(Convert.FromBase64String(text));
            using var gzip=new System.IO.Compression.GZipStream(stream,System.IO.Compression.CompressionMode.Decompress);
            using var output=new MemoryStream();gzip.CopyTo(output);return output.ToArray();
        }
        var a=Expand(active);var b=Expand(without);var c=Expand(restored);
        if(a.SequenceEqual(b) || !a.SequenceEqual(c))throw new InvalidDataException("Effective EST metadata did not change and restore as expected.");
        var directory=Path.Combine(Path.GetDirectoryName(outputRoot)!,"female-est-test");
        File.WriteAllText(Path.Combine(directory,"metadata-effect-test.json"),JsonSerializer.Serialize(new{state="passed",onlyToggledMod=mod,activeBytes=a.Length,withoutTestBytes=b.Length,restoredBytes=c.Length,metadataChangedWhenDisabled=true,metadataExactlyRestored=true,finalModEnabled=true,timestamp=DateTimeOffset.UtcNow},new JsonSerializerOptions{WriteIndented=true}));
        status="有效元数据实测通过：关闭测试 Mod 时发生变化，恢复启用后逐字节一致；测试 Mod 保持启用。";
        WriteStatus("est-effect-passed",status);
    }

    private void CaptureRuntimeBones()
    {
        var capture=RuntimeSkeletonInspection.Capture(objects);
        var json=JsonSerializer.Serialize(capture,new JsonSerializerOptions{WriteIndented=true});
        var directory=runtimeCheckForWorkflow && activeProjectDirectory is not null?activeProjectDirectory:Path.Combine(Path.GetDirectoryName(outputRoot)!,"chain-test");Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory,"runtime-bones.json"),SafeDiagnosticJson(json));
        var expected=candidateDocument?.Bones.Where(b=>b.OriginalIndex<0).Select(b=>b.Name).ToArray() ?? [];
        var expectedHash=lastCandidate is not null && File.Exists(lastCandidate)?SklbContainer.Parse(File.ReadAllBytes(lastCandidate)).Sha256:null;
        using var readback=JsonDocument.Parse(json);
        var rows=new List<string>();
        runtimeVerified=false;
        foreach(var actor in readback.RootElement.GetProperty("actors").EnumerateArray())
            foreach(var partial in actor.GetProperty("partials").EnumerateArray())
            {
                var pose=partial.GetProperty("poseBones");if(pose.ValueKind==JsonValueKind.Null)continue;
                var names=pose.GetProperty("names").EnumerateArray().Select(v=>v.GetString()!).ToHashSet();
                if(document is null || !names.Contains(document.Bones[0].Name))continue;
                var matched=expected.Count(names.Contains);
                var loadedPath=partial.GetProperty("resourcePath").GetString();
                var currentFile=false;
                if(expectedHash is not null && loadedPath is not null && Path.IsPathRooted(loadedPath) && File.Exists(loadedPath))
                {
                    try {currentFile=SklbContainer.Parse(File.ReadAllBytes(loadedPath)).Sha256==expectedHash;}
                    catch(Exception e) when(e is IOException or InvalidDataException) {log.Warning("Could not verify the loaded clothing skeleton file: {Detail}",SafeDetail(e.ToString()));}
                }
                var desired=candidateDocument?.AnchorBindings??[];
                var actualConnections=partial.GetProperty("attachmentConnections").EnumerateArray().ToArray();
                var connectionMatches=desired.All(a=>actualConnections.Any(c=>c.GetProperty("referenceBoneIndex").GetInt32()==a.ReferenceBoneIndex && c.GetProperty("boneName").GetString()==a.BoneName));
                if(expected.Length>0 && matched==expected.Length && currentFile && connectionMatches)runtimeVerified=true;
                var kind=actor.GetProperty("isLocalPlayer").GetBoolean()?"本体":"GPose/预览";
                rows.Add($"{kind}：{names.Count} 根骨骼，新增 {matched}/{expected.Length}；{(currentFile?"已加载当前版本":"尚未确认当前版本")}。"+
                    (desired.Count==0?"":$"挂点索引：{(connectionMatches?"全部与当前人体骨名一致；运动需另行验收":"存在不一致，请检查人体骨架兼容性")}。"));
            }
        runtimeReport=rows.Count==0?"未发现服装附加骨架。请确认测试装备和所选角色。":string.Join(" ",rows);
        WriteStatus("runtime-bones-captured",runtimeReport);
    }

    private void SchedulePreview()
    {
        _=framework.RunOnFrameworkThread(()=>TryUi(()=>
        {
            CheckCandidate();
            preview.Start(selectedCollection,document!.SourceGamePath,lastCandidate!,previewPriority);
            status="骨架临时替换已应用。请在所选集合中穿戴测试装备并重绘；新增骨骼尚未绑定服装权重。";
        }));
    }

    private void CheckModel()
    {
        CheckCandidate();
        var baseline=SkeletonDocument.Load(Path.Combine(activeProjectDirectory!,"snapshot.json"));
        var candidate=SkeletonDocument.Load(Path.Combine(Path.GetDirectoryName(lastCandidate!)!,"validated-exchange.json"));
        var reference=SkeletonDocument.Load(Path.Combine(activeProjectDirectory!,"reference-snapshot.json"));
        var model=data.GetFile<Lumina.Data.FileResource>(modelPath)?.Data ?? throw new FileNotFoundException("Original model not found",modelPath);
        var originalIds=baseline.Bones.Select(b=>b.Id).ToHashSet();
        var anchorIds=candidate.AnchorBindings.Select(a=>a.BoneId).ToHashSet();
        var report=ModelBoneCheck.Check(model,modelPath,candidate.Bones.Select(b=>b.Name).Concat(reference.Bones.Select(b=>b.Name)),candidate.Bones.Where(b=>!originalIds.Contains(b.Id) && !anchorIds.Contains(b.Id)).Select(b=>b.Name));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(lastCandidate!)!,"model-check.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        modelReport=$"原版模型：{report.MeshCount} 个 mesh，{report.BoneNameCount} 个骨名；未解析骨骼 {report.MissingNames.Length}；引用新增骨 {report.ReferencedNewNames.Length}。顶点权重未验证。";
        WriteStatus(report.BoneTablesValid?"model-bones-passed":"model-bones-failed",modelReport);
    }

    private void ScheduleRoundTrip(bool launchBlender=false)
    {
        if (busy || disposed) return;
        if(launchBlender && !EnsureBlenderConfigured(()=>ScheduleRoundTrip(true)))return;
        busy = true;
        operationFailed=false;
        var selected = source.Trim();
        status = "正在处理私有骨架副本…";
        _ = framework.RunOnFrameworkThread(() =>
        {
            try
            {
                var selectedCharacter=RequireCurrentCharacterSelection();
                if(preview.Active)StopWorkflowPreview();
                RoundTrip(selected,selectedCharacter.Source);
                if(launchBlender)OpenBlender();
            }
            catch (Exception e) { operationFailed=true;status = e.Message; log.Error("Skeleton round-trip failed: {Detail}",SafeDetail(e.ToString())); WriteStatus("failed", e.ToString()); }
            finally { busy = false; }
        });
    }

    private void RoundTrip(string selected,CharacterSkeletonSource selectedCharacter)
    {
        if (disposed) return;
        var local=Path.IsPathRooted(selected);
        var gamePath=local?localTargetGamePath.Trim():selected;
        selectedCharacter.CheckEquipmentPath(gamePath);
        if(!Regex.IsMatch(gamePath,@"^chara/human/c\d{4}/skeleton/top/t\d{4}/skl_c\d{4}t\d{4}\.sklb$"))
            throw new InvalidDataException("本开发版只处理 top 服装附加骨架；请填写对应的 chara/human/.../skeleton/top/...sklb 游戏路径。");
        var bytes = local
            ? File.ReadAllBytes(selected)
            : data.GetFile<Lumina.Data.FileResource>(selected)?.Data ?? throw new FileNotFoundException("未找到骨架资源", selected);
        var container = SklbContainer.Parse(bytes);
        if(container.VersionLow==0x3031)log.Information("Reading a recognized original multi-attachment equipment skeleton.");
        var directory = Path.Combine(outputRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-ffff"));
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "original.sklb"), bytes);
        var input = Path.Combine(directory, "input.hkx");
        File.WriteAllBytes(input, container.Payload.ToArray());
        SkeletonDocument exported;
        using (var session = new HavokSession(input))
        {
            exported = session.Snapshot(container.Sha256, gamePath);
            session.Save(Path.Combine(directory, "roundtrip.hkx"));
            session.Save(Path.Combine(directory, "original.xml"), true);
        }
        var candidateBytes = container.WithPayload(File.ReadAllBytes(Path.Combine(directory, "roundtrip.hkx")));
        _ = SklbContainer.Parse(candidateBytes);
        using (var verify = new HavokSession(Path.Combine(directory, "roundtrip.hkx")))
        {
            var actual = verify.Snapshot(container.Sha256, gamePath);
            if (actual.Name != exported.Name || actual.Bones.Count != exported.Bones.Count || actual.MapperCount != exported.MapperCount ||
                actual.ExtraVariantCount != exported.ExtraVariantCount || actual.LocalFrameCount != exported.LocalFrameCount || actual.PartitionCount != exported.PartitionCount)
                throw new InvalidDataException("Round-trip changed skeleton or auxiliary object counts.");
            for (var i = 0; i < actual.Bones.Count; i++)
            {
                var before = exported.Bones[i]; var after = actual.Bones[i];
                if (before.Name != after.Name || before.Translation != after.Translation || before.Rotation != after.Rotation || before.Scale != after.Scale || before.LockTranslation != after.LockTranslation || before.TranslationW!=after.TranslationW || before.ScaleW!=after.ScaleW ||
                    exported.Bones.FindIndex(b => b.Id == before.ParentId) != actual.Bones.FindIndex(b => b.Id == after.ParentId))
                    throw new InvalidDataException($"Round-trip changed bone {i}.");
            }
            var verifiedXml=Path.Combine(directory,"roundtrip.xml");
            verify.Save(verifiedXml,true);
            if(!XNode.DeepEquals(XDocument.Load(Path.Combine(directory,"original.xml")).Root,XDocument.Load(verifiedXml).Root))
                throw new InvalidDataException("Round-trip changed serialized Havok object data.");
        }
        File.WriteAllBytes(Path.Combine(directory, "candidate.sklb"), candidateBytes);
        GltfExchange.Export(exported,Path.Combine(directory,"skeleton.gltf"));
        var reference=ExportOriginalReference(selectedCharacter,directory);
        try
        {
            var attachment=EquipmentAnchorPlan.Describe(container,exported,reference) with{CharacterSource=selectedCharacter};
            File.WriteAllText(Path.Combine(directory,"anchor-rig.json"),JsonSerializer.Serialize(attachment,SkeletonDocument.JsonOptions));
        }
        catch(InvalidDataException e)
        {
            File.WriteAllText(Path.Combine(directory,"anchor-rig.json"),JsonSerializer.Serialize(new{available=false,reason=SafeDetail(e.Message),characterSource=selectedCharacter},SkeletonDocument.JsonOptions));
            throw new InvalidDataException("所选服装骨架不能与该角色的原版骨架建立受支持的挂接："+e.Message,e);
        }
        File.WriteAllText(Path.Combine(directory,"source-receipt.json"),JsonSerializer.Serialize(new{kind=local?"local-file-unverified":"original-sqpack",selected=local?"<本地骨架>":selected,gamePath,sourceSha256=container.Sha256,penumbraResolutionUsed=false,characterSource=selectedCharacter,referenceGamePath=reference.SourceGamePath,referenceSha256=reference.SourceSha256},SkeletonDocument.JsonOptions));
        exported.Save(Path.Combine(directory,"snapshot.json"));
        document=exported;
        if(local)sourceMode=2;
        activeProjectDirectory=directory;
        lastCandidate=null;
        candidateDocument=null;
        modelReport="尚未检查服装模型。";
        ConfigureProjectReturn();
        workflowStep=1;
        runtimeVerified=false;
        status = $"已导出所选角色的原版人物骨架（{reference.Bones.Count} 根），并载入 {document.Bones.Count} 根服装原骨。接下来在 Blender 中添加骨链并指定父级。";
        SaveWorkflow();
        WriteStatus("roundtrip-passed", status);
    }

    private SkeletonDocument ExportOriginalReference(CharacterSkeletonSource selected,string directory)
    {
        selected.Validate();
        var gamePath=selected.GamePath;
        var bytes=data.GetFile<Lumina.Data.FileResource>(gamePath)?.Data ?? throw new FileNotFoundException("Original base skeleton not found",gamePath);
        var container=SklbContainer.Parse(bytes);
        var hkx=Path.Combine(directory,"reference.hkx");
        File.WriteAllBytes(hkx,container.Payload.ToArray());
        using var reference=new HavokSession(hkx);
        var snapshot=reference.Snapshot(container.Sha256,gamePath);
        reference.Save(Path.Combine(directory,"reference.xml"),true);
        File.WriteAllBytes(Path.Combine(directory,"reference.sklb"),bytes);
        snapshot.Save(Path.Combine(directory,"reference-snapshot.json"));
        GltfExchange.Export(snapshot,Path.Combine(directory,"reference.gltf"));
        return snapshot;
    }

    private void ScheduleAppend()
    {
        if (busy || disposed || activeProjectDirectory is null) return;
        busy=true;
        operationFailed=false;
        status="正在检查 Blender 修改并生成骨架…";
        lastCandidate=null;
        candidateDocument=null;
        runtimeVerified=false;
        var project=activeProjectDirectory;
        var returned=returnedPath.Trim();
        _=framework.RunOnFrameworkThread(() =>
        {
            try { if (!disposed) {if(preview.Active)StopWorkflowPreview();Append(project,returned);} }
            catch(Exception e) { operationFailed=true;status=e.Message; log.Error("Skeleton append failed: {Detail}",SafeDetail(e.ToString())); WriteStatus("failed",e.ToString()); }
            finally { busy=false; }
        });
    }

    private void Append(string project,string returned)
    {
        var baseline=SkeletonDocument.Load(Path.Combine(project,"snapshot.json"));
        var candidate=GltfExchange.Import(returned,baseline);
        var logicalOrdered=SkeletonValidator.OrderedForAppend(candidate,baseline);
        var original=SklbContainer.Parse(File.ReadAllBytes(Path.Combine(project,"original.sklb")));
        if (original.Sha256!=baseline.SourceSha256) throw new InvalidDataException("Original SKLB changed since export.");
        if(candidate.AttachmentTargetBone is not null)throw new InvalidDataException("旧版整套换挂点的返回文件已停用，请在新版完整骨架中手动指定新骨链父级。");
        EquipmentAnchorPlan? anchors=null;
        if(candidate.AnchorSchemaVersion==1)
        {
            // Resolve against fresh original SqPack data, never a character Mod or editable Blender metadata.
            var savedReference=SkeletonDocument.Load(Path.Combine(project,"reference-snapshot.json"));
            var referencePath=savedReference.SourceGamePath;
            var sourceInfo=JsonSerializer.Deserialize<AnchorRigInfo>(File.ReadAllText(Path.Combine(project,"anchor-rig.json")),SkeletonDocument.JsonOptions);
            if(sourceInfo?.CharacterSource is{} characterSource)
            {
                characterSource.CheckEquipmentPath(baseline.SourceGamePath);
                if(characterSource.GamePath!=referencePath)throw new InvalidDataException("项目的人物选择与导出参考不一致，请重新读取角色并创建项目。");
            }
            var expected=Regex.Match(baseline.SourceGamePath,@"^chara/human/c(\d{4})/skeleton/top/");
            if(!expected.Success || referencePath!=$"chara/human/c{expected.Groups[1].Value}/skeleton/base/b0001/skl_c{expected.Groups[1].Value}b0001.sklb")
                throw new InvalidDataException("项目的人物参考路径无效。");
            var referenceBytes=data.GetFile<Lumina.Data.FileResource>(referencePath)?.Data??throw new FileNotFoundException("未找到原版人体骨架。");
            var referenceContainer=SklbContainer.Parse(referenceBytes);
            var referenceHkx=Path.Combine(project,"attachment-reference.hkx");
            File.WriteAllBytes(referenceHkx,referenceContainer.Payload.ToArray());
            using var referenceSession=new HavokSession(referenceHkx);
            var reference=referenceSession.Snapshot(referenceContainer.Sha256,referencePath);
            // A game update or altered local reference must not silently move the Blender bind pose.
            if(savedReference.SourceSha256!=reference.SourceSha256 || savedReference.SourceGamePath!=referencePath)
                throw new InvalidDataException("原版人体骨架与项目参考不一致，请重新创建项目。");
            anchors=EquipmentAnchorPlan.Create(original,baseline,candidate,reference,referenceContainer);
        }
        else if(candidate.AnchorBindings.Count>0)throw new InvalidDataException("挂点交换版本不受支持。");
        var ordered=anchors?.NativeBones??logicalOrdered;
        var directory=Path.Combine(project,"appended-"+DateTime.UtcNow.ToString("HHmmss-ffff"));
        Directory.CreateDirectory(directory);
        var trustedInput=Path.Combine(directory,"original.hkx");
        File.WriteAllBytes(trustedInput,original.Payload.ToArray());
        var templatePath=Path.Combine(directory,"trusted-template.xml");
        using(var template=new HavokSession(trustedInput)) template.Save(templatePath,true);
        var xml=anchors is null?HavokXmlAppend.Apply(File.ReadAllText(templatePath),baseline,candidate):HavokXmlAppend.ApplyAnchors(File.ReadAllText(templatePath),baseline,candidate,anchors);
        var editedXml=Path.Combine(directory,"edited.xml");
        File.WriteAllText(editedXml,xml);
        var hkxPath=Path.Combine(directory,"appended.hkx");
        using(var edited=new HavokSession(editedXml)) edited.Save(hkxPath);
        using(var verify=new HavokSession(hkxPath))
        {
            var actual=verify.Snapshot(baseline.SourceSha256,baseline.SourceGamePath);
            if(actual.Bones.Count!=ordered.Count) throw new InvalidDataException("Native append returned an unexpected bone count.");
            for(var i=0;i<ordered.Count;i++)
            {
                var expected=ordered[i]; var got=actual.Bones[i];
                var expectedParent=ordered.FindIndex(b=>b.Id==expected.ParentId);
                var actualParent=actual.Bones.FindIndex(b=>b.Id==got.ParentId);
                if(expected.Name!=got.Name || expectedParent!=actualParent || Vector3.Distance(expected.Translation,got.Translation)>1e-5f ||
                    Vector3.Distance(expected.Scale,got.Scale)>1e-5f || 1-MathF.Abs(Quaternion.Dot(expected.Rotation,got.Rotation))>1e-6f ||
                    expected.LockTranslation!=got.LockTranslation || expected.TranslationW!=got.TranslationW || expected.ScaleW!=got.ScaleW)
                    throw new InvalidDataException($"Native append changed bone {i}: {expected.Name}");
            }
            actual.Save(Path.Combine(directory,"candidate-snapshot.json"));
        }
        var candidateBytes=anchors is null?SklbLayerTable.Append(original,File.ReadAllBytes(hkxPath),baseline,candidate):anchors.Build(original,File.ReadAllBytes(hkxPath));
        var verifiedLayers=SklbLayerTable.Read(SklbContainer.Parse(candidateBytes),ordered.Count);
        lastCandidate=Path.Combine(directory,"candidate.sklb");
        File.WriteAllBytes(lastCandidate,candidateBytes);
        File.WriteAllText(Path.Combine(directory,"validation.json"),JsonSerializer.Serialize(new
        {
            state="structure-verified",candidateSha256=SklbContainer.Parse(candidateBytes).Sha256,
            sourceSha256=baseline.SourceSha256,sourceGamePath=baseline.SourceGamePath,
            originalBoneCount=baseline.Bones.Count,candidateBoneCount=ordered.Count,
            originalBoneTransformsPreserved=true,originalNonRootTransformsPreserved=true,
            originalNamesAndParentRelationshipsPreserved=true,originalIndicesPreserved=anchors?.OriginalIndicesPreserved??true,
            authoredBoneCount=ordered.Count-baseline.Bones.Count-candidate.AnchorBindings.Count,usedAnchorCount=candidate.AnchorBindings.Count,
            mdlWeightsVerified=false,gameMotionVerified=false,
            anchorRig=anchors is null?null:new{anchors.Info.ReferenceGamePath,anchors.Info.ReferenceSha256,
                existing=anchors.Info.ExistingAnchors,added=anchors.AddedAnchors,connections=anchors.ParentIndices,
                nativeRootsFirst=true,originalLayerIndicesRemapped=true,runtimeConnectionsVerified=false},
            layerMembershipVerified=true,layers=verifiedLayers,
            timestamp=DateTimeOffset.UtcNow,
        },new JsonSerializerOptions{WriteIndented=true}));
        candidate.Save(Path.Combine(directory,"validated-exchange.json"));
        candidateDocument=candidate;
        workflowStep=2;
        lastValidatedReturnHash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(returned)));
        if(baseline.SourceGamePath==DefaultSkeletonPath)
        {
            try { CheckModel(); }
            catch(Exception e) {modelReport="骨架已生成，模型骨骼表暂未检查："+e.Message;log.Warning("Optional model check failed: {Detail}",SafeDetail(e.ToString()));}
        }
        status=$"检查通过：保留 {baseline.Bones.Count} 根原骨，新增 {ordered.Count-baseline.Bones.Count-candidate.AnchorBindings.Count} 根，使用 {candidate.AnchorBindings.Count} 个人体挂点。原有服装挂接保留；实际随动待游戏验证。";
        SaveWorkflow();
        WriteStatus("append-passed",status);
    }

    private void WriteStatus(string state, string detail)
    {
        var path=Path.Combine(outputRoot,"status.json");
        File.WriteAllText(path+".tmp",JsonSerializer.Serialize(new { state, detail=SafeDetail(detail), timestamp = DateTimeOffset.UtcNow, pluginVersion = "0.5.0", gameVersion = "2026.10.06.0000.0000" }, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path+".tmp",path,true);
    }

    public void Dispose()
    {
        disposed = true;
        SaveWorkflow();
        fileDialogs.Reset();
        ReleaseBlenderHandle();
        try { preview.Dispose(); } catch(Exception e) { log.Error("Could not remove temporary skeleton preview during disposal: {Detail}",SafeDetail(e.ToString())); }
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenMainUi -= Open;
        pluginInterface.UiBuilder.OpenConfigUi -= Open;
        commands.RemoveHandler("/csw");
        framework.Update-=PollDevelopmentCommand;
        WriteStatus("unloaded", "Plugin disposed.");
    }
}
