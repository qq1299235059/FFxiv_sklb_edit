using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using ClothingSkeleton.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;

namespace ClothingSkeleton.Plugin;

public sealed partial class Plugin
{
    private const string DefaultSkeletonPath="chara/human/c0201/skeleton/top/t0100/skl_c0201t0100.sklb";
    private readonly FileDialogManager fileDialogs=new();
    private static readonly Vector4 Accent=new(0.32f,0.74f,0.9f,1);
    private static readonly Vector4 Good=new(0.48f,0.85f,0.62f,1);
    private string settingsPath="";
    private string blenderExecutable="";
    private string? lastPackage;
    private string lastValidatedReturnHash="";
    private string returnedHash="";
    private string returnCaption="尚未收到 Blender 修改。";
    private string observedReturn="";
    private bool returnReady;
    private bool returnExists;
    private bool returnFromOtherProject;
    private bool runtimeVerified;
    private bool operationFailed;
    private bool runtimeCheckForWorkflow;
    private DateTime? runtimeCheckAt;
    private BlenderBridgeServer? blenderBridge;
    private int bridgePort=BridgeExchange.DefaultPort;
    private string bridgeError="";
    private int workflowStep;
    private int sourceMode;
    private bool useChosenCollection;
    private bool HasNewReturn => returnExists && !returnFromOtherProject && (!returnReady || returnedHash!=lastValidatedReturnHash);
    private bool CanUseCandidate => lastCandidate is not null && !HasNewReturn;
    private bool IsOdinProject => document?.SourceGamePath==DefaultSkeletonPath;

    private sealed record WorkflowSettings(string? ProjectDirectory=null,string? CandidatePath=null,string BlenderExecutable="",string ValidatedReturnHash="",string? SelectedSource=null,string? TargetGamePath=null,int SourceMode=0,bool GuideCompleted=false,bool EnableDevelopmentCommands=false,int Version=4,string CharacterSelectionKind="self",int BridgePort=BridgeExchange.DefaultPort);

    private void InitializeWorkflow()
    {
        settingsPath=storage.Settings;
        blenderExecutable="";
        WorkflowSettings? restored=null;
        if(File.Exists(settingsPath))
        {
            try
            {
                var saved=JsonSerializer.Deserialize<WorkflowSettings>(File.ReadAllText(settingsPath));
                if(saved is not null)
                {
                    restored=saved;
                    var project=storage.Resolve(saved.ProjectDirectory);
                    if(project is not null && File.Exists(Path.Combine(project,"snapshot.json")))
                    {
                        activeProjectDirectory=project;
                        document=ClothingSkeleton.Core.SkeletonDocument.Load(Path.Combine(activeProjectDirectory!,"snapshot.json"));
                        lastCandidate=storage.Resolve(saved.CandidatePath);
                        candidateDocument=lastCandidate is not null && File.Exists(lastCandidate)
                            ?ClothingSkeleton.Core.SkeletonDocument.Load(Path.Combine(Path.GetDirectoryName(lastCandidate)!,"validated-exchange.json")):null;
                        if(candidateDocument is null)lastCandidate=null;
                    }
                    blenderExecutable=saved.BlenderExecutable??"";
                    lastValidatedReturnHash=saved.ValidatedReturnHash??"";
                    guideCompleted=saved.Version>=4 && saved.GuideCompleted;
                    bridgePort=saved.BridgePort is >0 and <=65535?saved.BridgePort:BridgeExchange.DefaultPort;
                    developmentMode=saved.EnableDevelopmentCommands;
                    characterSelectionKind=saved.CharacterSelectionKind is "target"?"target":"self";
                }
            }
            catch(Exception e) { log.Warning("Could not restore workflow settings: {Detail}",SafeDetail(e.Message)); }
        }
        if(document is not null && activeProjectDirectory is not null)
        {
            source=document.SourceGamePath;
            localTargetGamePath=source;
            sourceMode=1;
            if(restored?.SelectedSource is{} selected) {sourceMode=Math.Clamp(restored.SourceMode,0,2);if(restored.Version<3 && sourceMode==0)sourceMode=1;source=sourceMode==2?storage.Resolve(selected)??"":selected;localTargetGamePath=restored.TargetGamePath??document.SourceGamePath;}
            if(lastCandidate is not null)
            {
                try { CheckCandidate(); }
                catch(Exception e) { log.Warning("Ignoring an invalid saved candidate: {Detail}",SafeDetail(e.ToString()));lastCandidate=null;candidateDocument=null; }
            }
            workflowStep=lastCandidate is null?1:2;
            status=lastCandidate is null?"已恢复上次项目，可以继续在 Blender 编辑。":"已恢复上次检查通过的骨架，可以继续编辑或预览。";
        }
        ConfigureProjectReturn(false);
        showGuide=!guideCompleted;
        StartBridge();
        previewPriority=2001;
    }

    private void ConfigureProjectReturn(bool resetValidation=true)
    {
        returnedPath=storage.ReturnFile;
        Directory.CreateDirectory(Path.GetDirectoryName(returnedPath)!);
        if(resetValidation)lastValidatedReturnHash="";
        observedReturn="";
        returnReady=false;
        returnExists=false;
        returnFromOtherProject=false;
        returnCaption="在 Blender 完成编辑后，点击“返回游戏”。";
        if(activeProjectDirectory is not null)File.WriteAllText(Path.Combine(activeProjectDirectory,"bridge.json"),JsonSerializer.Serialize(new{schemaVersion=1,pluginRoot=Path.GetRelativePath(activeProjectDirectory,storage.Root).Replace('\\','/'),projectId=document!.ProjectId},new JsonSerializerOptions{WriteIndented=true}));
    }

    private bool SaveWorkflow()
    {
        if(settingsPath.Length==0)return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            var selectedSource=sourceMode==2?storage.Relative(activeProjectDirectory is null?null:Path.Combine(activeProjectDirectory,"original.sklb")):source;
            var state=new WorkflowSettings(storage.Relative(activeProjectDirectory),storage.Relative(lastCandidate),"",lastValidatedReturnHash,selectedSource,localTargetGamePath,sourceMode,guideCompleted,developmentMode,CharacterSelectionKind:characterSelectionKind,BridgePort:bridgePort);
            File.WriteAllText(settingsPath+".tmp",JsonSerializer.Serialize(state,new JsonSerializerOptions{WriteIndented=true}));
            File.Move(settingsPath+".tmp",settingsPath,true);
            return true;
        }
        catch(Exception e) { log.Warning("Could not save workflow settings: {Detail}",SafeDetail(e.Message));return false; }
    }

    private void PollWorkflow()
    {
        if(runtimeCheckAt is{} due && DateTime.UtcNow>=due)
        {
            runtimeCheckAt=null;
            TryUi(CheckWorkflowRuntime);
        }
        RefreshReturnedFile();
    }

    private void RefreshReturnedFile()
    {
        if(document is null || string.IsNullOrWhiteSpace(returnedPath))return;
        try
        {
            var file=new FileInfo(returnedPath);
            var complete=new FileInfo(Path.ChangeExtension(returnedPath,".complete.json"));
            var stamp=$"{returnedPath}|{file.Exists}|{(file.Exists?file.LastWriteTimeUtc.Ticks:0)}|{(file.Exists?file.Length:0)}|{(complete.Exists?complete.LastWriteTimeUtc.Ticks:0)}";
            if(stamp==observedReturn)return;
            observedReturn=stamp;
            returnReady=false;
            returnExists=file.Exists;
            returnFromOtherProject=false;
            returnedHash="";
            if(!returnExists) { returnCaption="尚未收到修改。在 Blender 点击“返回游戏”后，这里会自动识别。";return; }
            returnedHash=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(returnedPath)));
            if(!complete.Exists) { returnCaption="文件尚未导出完成，请使用 Blender 面板的“返回游戏”。";return; }
            using var manifest=JsonDocument.Parse(File.ReadAllText(complete.FullName));
            if(manifest.RootElement.GetProperty("projectId").GetGuid()!=document.ProjectId)
            { returnFromOtherProject=true;returnCaption="固定返回目录中是其他项目的文件。本项目完成编辑后点击“返回游戏”即可更新。";return; }
            if(manifest.RootElement.GetProperty("gltfSha256").GetString()!=returnedHash)
            { returnCaption="文件正在写入或已被修改，请从 Blender 重新返回。";return; }
            returnReady=true;
            returnCaption=HasNewReturn?"已收到 Blender 修改，可以回导检查。":"这份 Blender 修改已经检查通过。";
        }
        catch(Exception e) when(e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or UnauthorizedAccessException)
        { returnReady=false;returnCaption="返回文件暂时无法读取，请完成 Blender 导出后重试。";observedReturn=""; }
    }

    private void DrawWorkflow()
    {
        if(showGuide) {DrawOnboarding();return;}
        if(ImGui.SmallButton("使用指引与 Blender 设置"))ShowOnboarding();
        ImGui.TextColored(Accent,"为服装添加骨骼");
        ImGui.TextWrapped("创建项目，在 Blender 编辑，再回到这里检查和试用。");
        if(document is not null)
            ImGui.TextUnformatted($"当前项目：{(IsOdinProject?"中原女 · 奥丁神铠":document.Name)}  |  原骨 {document.Bones.Count}  ·  新增 {(candidateDocument?.Bones.Count??document.Bones.Count)-document.Bones.Count-(candidateDocument?.AnchorBindings.Count??0)}");
        ImGui.Spacing();
        if(preview.Active)
        {
            ImGui.TextColored(Accent,"临时预览已启用");
            ImGui.SameLine();
            ImGui.BeginDisabled(busy);
            if(ImGui.SmallButton("撤销预览并恢复"))ScheduleWorkflowAction(StopWorkflowPreview);
            ImGui.EndDisabled();
        }
        string[] steps=["1  准备骨架","2  Blender 编辑","3  回导与试用"];
        var width=(ImGui.GetContentRegionAvail().X-16)/3;
        for(var i=0;i<steps.Length;i++)
        {
            if(i>0)ImGui.SameLine();
            ImGui.BeginDisabled(busy || i>0 && document is null);
            var selected=i==workflowStep;
            if(selected)ImGui.PushStyleColor(ImGuiCol.Button,new Vector4(0.12f,0.38f,0.5f,1));
            if(ImGui.Button(steps[i],new Vector2(width,34)))workflowStep=i;
            if(selected)ImGui.PopStyleColor();
            ImGui.EndDisabled();
        }
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.BeginDisabled(busy);
        switch(workflowStep)
        {
            case 0: DrawPrepareStep();break;
            case 1: DrawBlenderStep();break;
            default: DrawReturnStep();break;
        }
        ImGui.EndDisabled();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(operationFailed?new Vector4(1,0.66f,0.38f,1):Accent,busy?"正在处理…":operationFailed?"需要处理":"操作提示");
        ImGui.TextWrapped(SafeDetail(status));
        ImGui.Spacing();
        if(ImGui.CollapsingHeader("高级设置与检查"))DrawAdvancedWorkflow();
    }

    private void DrawPrepareStep()
    {
        DrawCharacterSelection();
        ImGui.TextUnformatted("选择回导到哪套服装骨架");
        string[] sources=["所选角色当前上衣（原版关联）","指定游戏服装骨架","本地服装骨架文件"];
        if(ImGui.BeginCombo("服装骨架来源",sources[sourceMode]))
        {
            for(var i=0;i<sources.Length;i++)if(ImGui.Selectable(sources[i],sourceMode==i))
            {
                sourceMode=i;
                if(i==0)
                {
                    source=characterSelection?.TopGamePath??"";localTargetGamePath=source;
                    if(characterSelection is{} chosen)modelPath=$"chara/equipment/e{chosen.Source.EquippedTopSetId:D4}/model/c{chosen.Source.RaceSexId:D4}e{chosen.Source.EquippedTopSetId:D4}_top.mdl";
                }
                else if(i==2 && !Path.IsPathRooted(source))source="";
            }
            ImGui.EndCombo();
        }
        if(sourceMode==0)ImGui.TextWrapped(characterSelection?.TopGamePath is{} top?$"原版上衣骨架：{Path.GetFileName(top)}":"当前上衣没有可直接使用的原版附加骨架，可改选游戏路径或本地文件。");
        if(sourceMode==1)ImGui.InputText("游戏骨架路径",ref source,1024);
        if(sourceMode==2)
        {
            if(ImGui.Button("选择 SKLB 文件…"))fileDialogs.OpenFileDialog("选择服装骨架",".sklb",(ok,path)=>
            {
                if(!ok)return;
                source=path;
                var name=System.Text.RegularExpressions.Regex.Match(Path.GetFileName(path),@"^skl_c(\d{4})t(\d{4})\.sklb$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if(name.Success)localTargetGamePath=$"chara/human/c{name.Groups[1].Value}/skeleton/top/t{name.Groups[2].Value}/skl_c{name.Groups[1].Value}t{name.Groups[2].Value}.sklb";
            });
            ImGui.SameLine();ImGui.TextUnformatted(string.IsNullOrWhiteSpace(source)?"未选择文件":Path.GetFileName(source));
            ImGui.InputText("要替换的游戏路径",ref localTargetGamePath,1024);
        }
        ImGui.TextWrapped("Blender 人物骨架按上方所选场景角色导出；服装骨架只作为追加与回导目标。当前支持上衣附加骨架（top）。");
        ImGui.Spacing();
        ImGui.BeginDisabled(characterSelection is null || string.IsNullOrWhiteSpace(source) || sourceMode==2 && !File.Exists(source));
        if(PrimaryButton("创建并发送到 Blender"))ScheduleRoundTrip(true);
        if(ImGui.Button("只创建项目"))ScheduleRoundTrip();
        ImGui.EndDisabled();
        if(document is not null)
        {
            ImGui.SameLine();
            if(ImGui.Button("继续当前项目"))workflowStep=1;
        }
    }

    private void DrawBlenderStep()
    {
        ImGui.TextUnformatted("在 Blender 中编辑新增骨骼");
        ImGui.TextWrapped("打开并启用配套 Blender 插件，连接后即可接收骨架。当前工程和已有对象会保留。");
        DrawBridgeStatus();
        if(PrimaryButton("发送当前骨架到 Blender"))TryUi(OpenBlender);
        ImGui.Spacing();
        ImGui.TextWrapped("在右侧“服装骨架”面板添加骨骼或骨链。完成后点击“返回游戏”，回到游戏即可继续。");
        ImGui.TextWrapped("完整人体挂点与服装原骨显示在同一骨架中。选挂点后添加新链，或选新链根骨再选挂点，Ctrl+P → 保持偏移。未用挂点会在返回时自动忽略。");
        ImGui.TextWrapped("原有骨骼需要保留；服装网格与权重仍由模型工具处理。");
        ImGui.Spacing();
        ImGui.TextColored(returnReady?Good:Accent,returnCaption);
        ImGui.BeginDisabled(!returnReady);
        if(ImGui.Button("回导并检查",new Vector2(-1,36)))ScheduleAppend();
        ImGui.EndDisabled();
        if(ImGui.Button("打开项目文件夹"))TryUi(()=>OpenFolder(activeProjectDirectory!));
    }

    private void DrawReturnStep()
    {
        if(HasNewReturn || lastCandidate is null)
        {
            ImGui.TextWrapped(returnCaption);
            ImGui.BeginDisabled(!returnReady);
            if(PrimaryButton("检查 Blender 修改"))ScheduleAppend();
            ImGui.EndDisabled();
            if(!returnReady && ImGui.Button("前往 Blender 编辑"))workflowStep=1;
        }
        if(candidateDocument is null || document is null)return;
        ImGui.Spacing();
        ImGui.TextColored(CanUseCandidate?Good:Accent,CanUseCandidate?"骨架检查通过":"Blender 有新修改，请先重新检查");
        ImGui.TextUnformatted($"保留 {document.Bones.Count} 根原骨  ·  新增 {candidateDocument.Bones.Count-document.Bones.Count-candidateDocument.AnchorBindings.Count} 根骨骼  ·  所需挂点 {candidateDocument.AnchorBindings.Count}");
        if(candidateDocument.AnchorSchemaVersion==1)
        {
            ImGui.TextColored(Accent,"未使用的人体挂点已忽略，原服装挂接保持。");
            if(candidateDocument.AnchorBindings.Count>0)ImGui.TextWrapped("保留挂点："+string.Join("、",candidateDocument.AnchorBindings.Select(a=>a.BoneName)));
            ImGui.TextWrapped("已核对原骨静止姿态、父级和重排后的骨骼层。各条链的实际随动仍需在游戏中测试，服装权重另行制作。");
        }
        else ImGui.TextWrapped("已核对原骨、父级、静止姿态及骨骼层。可以在穿着对应装备的角色上试用。");
        ImGui.BeginDisabled(!CanUseCandidate);
        if(PrimaryButton(preview.Active?"更新当前角色的预览":"在当前角色上预览"))ScheduleWorkflowAction(PreviewCurrentPlayer);
        ImGui.EndDisabled();
        if(ImGui.Button("检查游戏内是否已加载"))ScheduleWorkflowAction(CheckWorkflowRuntime);
        if(runtimeVerified)ImGui.TextColored(Good,"全部新增骨骼已找到，资源文件与当前版本一致。");
        ImGui.TextWrapped(runtimeReport);
        ImGui.Spacing();
        ImGui.BeginDisabled(!CanUseCandidate);
        if(ImGui.Button("发送到 Penumbra"))ScheduleWorkflowAction(SendWorkflowPackage);
        ImGui.SameLine();
        if(ImGui.Button("仅保存 Mod 安装包"))ScheduleWorkflowAction(ExportWorkflowPackage);
        ImGui.EndDisabled();
        ImGui.TextWrapped(IsOdinProject?"安装包自动包含中原女奥丁神铠的骨架关联；在 Penumbra 中启用后可长期保留。":"安装包沿用原有装备骨架路径。若装备没有附加骨架关联，需要先配置关联。");
        ImGui.TextWrapped("临时预览使用当前角色的 Penumbra 集合，该集合中的其他角色也会受到影响。");
    }

    private static bool PrimaryButton(string label)=>ImGui.Button(label,new Vector2(-1,38));

    private void DrawAdvancedWorkflow()
    {
        ImGui.BeginDisabled(busy);
        if(ImGui.Button("设置 Blender"))ShowOnboarding();
        ImGui.TextWrapped("返回文件固定保存于：插件目录 / BlenderReturn / edited.gltf。");
        if(ImGui.Button("打开返回文件夹"))TryUi(()=>OpenFolder(Path.GetDirectoryName(storage.ReturnFile)!));
        if(activeProjectDirectory is not null)
        {
            if(ImGui.Button("打开当前项目文件夹"))TryUi(()=>OpenFolder(activeProjectDirectory));
        }
        ImGui.Separator();
        ImGui.Checkbox("手动指定预览集合",ref useChosenCollection);
        if(useChosenCollection)
        {
            if(ImGui.Button("刷新集合"))ScheduleWorkflowAction(()=>collections=preview.Collections());
            if(ImGui.BeginCombo("集合",collections.GetValueOrDefault(selectedCollection,"请选择集合")))
            {
                foreach(var pair in collections)if(ImGui.Selectable(pair.Value,pair.Key==selectedCollection))selectedCollection=pair.Key;
                ImGui.EndCombo();
            }
        }
        ImGui.InputInt("预览优先级",ref previewPriority);
        ImGui.InputText("服装模型路径",ref modelPath,1024);
        if(ImGui.Button("检查原版模型骨骼表"))ScheduleWorkflowAction(CheckModel);
        ImGui.TextWrapped(modelReport);
        if(ImGui.TreeNode("骨骼明细"))
        {
            if(document is not null)
            {
                var ordered=ClothingSkeleton.Core.SkeletonValidator.OrderedForAppend(candidateDocument??document,document);
                var anchorIds=(candidateDocument?.AnchorBindings??[]).Select(a=>a.BoneId).ToHashSet();
                foreach(var bone in ordered.Take(100))ImGui.TextUnformatted($"{(anchorIds.Contains(bone.Id)?"挂点":bone.OriginalIndex<0?"新增":"原骨")}  {bone.Name}  父级 {ordered.FirstOrDefault(b=>b.Id==bone.ParentId)?.Name??"无"}");
            }
            ImGui.TreePop();
        }
        if(lastPackage is not null && ImGui.Button("打开安装包所在文件夹"))TryUi(()=>OpenFolder(Path.GetDirectoryName(lastPackage)!));
        ImGui.EndDisabled();
    }

    private void StartBridge()
    {
        blenderBridge?.Dispose();blenderBridge=null;bridgeError="";
        try
        {
            blenderBridge=new(storage.ReturnFile,bridgePort);
            if(activeProjectDirectory is not null)
                try {blenderBridge.Publish(BridgeExchange.ReadProject(activeProjectDirectory));}
                catch(Exception e) when(e is IOException or JsonException or InvalidOperationException or ArgumentException)
                {log.Warning("Saved project cannot be sent: {Detail}",SafeDetail(e.Message));}
        }
        catch(System.Net.Sockets.SocketException)
        {bridgeError="本地连接端口不可用。请在两侧设置相同的其他端口后重试。";}
    }

    private void DrawBridgeStatus()
    {
        if(blenderBridge is null) {ImGui.TextWrapped(bridgeError);return;}
        using var state=JsonDocument.Parse(JsonSerializer.Serialize(blenderBridge.Status));
        var connected=state.RootElement.GetProperty("connected").GetBoolean();
        ImGui.TextColored(connected?Good:Accent,$"Blender：{(connected?"已连接":"等待连接")} · 本机端口 {blenderBridge.Port}");
        ImGui.TextWrapped(state.RootElement.GetProperty("message").GetString()??"");
    }

    private void OpenBlender()
    {
        if(activeProjectDirectory is null)throw new InvalidOperationException("请先创建骨架项目。");
        if(blenderBridge is null)StartBridge();
        if(blenderBridge is null)throw new InvalidOperationException(bridgeError);
        blenderBridge.Publish(BridgeExchange.ReadProject(activeProjectDirectory));
        workflowStep=1;
        status="骨架已发送到本地连接。请在已打开的 Blender 中启用配套插件；接收后可直接在当前工程编辑。";
        SaveWorkflow();
        WriteStatus("blender-sent",status);
    }

    private static void OpenFolder(string path)=>Process.Start(new ProcessStartInfo(path){UseShellExecute=true});

    private void ScheduleWorkflowAction(Action action)
    {
        if(busy || disposed)return;
        busy=true;
        _=framework.RunOnFrameworkThread(()=>
        {
            try {if(!disposed)TryUi(action);}
            finally {busy=false;}
        });
    }

    private void PreviewCurrentPlayer()
    {
        RefreshReturnedFile();
        CheckCandidate();
        if(!CanUseCandidate)throw new InvalidOperationException("Blender 有新修改，请先重新检查。");
        var self=CharacterSkeletonReader.Capture(objects.LocalPlayer,"self");
        self.Source.CheckEquipmentPath(document!.SourceGamePath);
        var effective=pluginInterface.GetIpcSubscriber<int,(bool,bool,(Guid,string))>("Penumbra.GetCollectionForObject.V5").InvokeFunc(0);
        if(!effective.Item1 || effective.Item3.Item1==Guid.Empty)throw new InvalidOperationException("没有找到当前角色的 Penumbra 集合，请先进入游戏并启用 Penumbra。");
        var collection=useChosenCollection?selectedCollection:effective.Item3.Item1;
        preview.Start(collection,document!.SourceGamePath,lastCandidate!,previewPriority,IsOdinProject?new ClothingSkeleton.Core.MidlanderFemaleEst(100,9100).GamePath:null);
        pluginInterface.GetIpcSubscriber<int,int,object?>("Penumbra.RedrawObject.V5").InvokeAction(0,0);
        runtimeVerified=false;
        runtimeReport="已请求重绘，稍后自动检查实时骨架。请穿着对应装备。";
        runtimeCheckAt=DateTime.UtcNow.AddSeconds(3);
        status=$"预览已应用到{(useChosenCollection?"所选集合":effective.Item3.Item2)}，可随时撤销。";
    }

    private void StopWorkflowPreview()
    {
        preview.Stop();
        runtimeCheckAt=null;
        runtimeVerified=false;
        pluginInterface.GetIpcSubscriber<int,int,object?>("Penumbra.RedrawObject.V5").InvokeAction(0,0);
        runtimeReport="临时预览已撤销。已安装的 Mod 仍按 Penumbra 设置生效。";
        status="已撤销临时预览并请求重绘角色。";
    }

    private void CheckWorkflowRuntime()
    {
        runtimeCheckForWorkflow=true;
        try { CaptureRuntimeBones(); }
        finally { runtimeCheckForWorkflow=false; }
    }

    private void ExportWorkflowPackage()
    {
        RefreshReturnedFile();
        if(!CanUseCandidate)throw new InvalidOperationException("请先检查最新的 Blender 修改。");
        if(IsOdinProject)ExportFemaleEstPackage();else ExportPackage();
        status="Mod 安装包已保存，可在高级设置中打开所在文件夹。";
    }

    private void SendWorkflowPackage()
    {
        ExportWorkflowPackage();
        var result=pluginInterface.GetIpcSubscriber<string,int>("Penumbra.InstallMod.V5").InvokeFunc(lastPackage!);
        if(result!=0)throw new InvalidOperationException($"安装包已保存，但 Penumbra 未接收（{result}）。请确认 Penumbra 已启用。");
        pluginInterface.GetIpcSubscriber<int,string,string,int>("Penumbra.OpenMainWindow.V5").InvokeFunc(1,"","");
        status="安装包已发送到 Penumbra，请在导入界面处理重名选项，并在需要的集合中启用。";
    }

    private void WriteWorkflowStatus()
    {
        var path=Path.Combine(storage.Root,"Development","workflow-status.json");
        File.WriteAllText(path,JsonSerializer.Serialize(new{step=workflowStep,project=storage.Relative(activeProjectDirectory),candidate=storage.Relative(lastCandidate),returnFile="BlenderReturn/edited.gltf",returnReady,hasNewReturn=HasNewReturn,canUseCandidate=CanUseCandidate,bridge=blenderBridge?.Status,sourceMode,previewActive=preview.Active,runtimeVerified,guideCompleted},new JsonSerializerOptions{WriteIndented=true}));
        WriteStatus("workflow-ready","Development/workflow-status.json");
    }
}
