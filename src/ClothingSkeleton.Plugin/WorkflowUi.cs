using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
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
    private string returnCaption="��δ�յ� Blender �޸ġ�";
    private string observedReturn="";
    private bool returnReady;
    private bool returnExists;
    private bool returnFromOtherProject;
    private bool runtimeVerified;
    private bool operationFailed;
    private bool runtimeCheckForWorkflow;
    private DateTime? runtimeCheckAt;
    private Process? openedBlender;
    private bool blenderLaunchPending;
    private string? openedBlenderProject;
    private int workflowStep;
    private int sourceMode;
    private bool useChosenCollection;
    private bool HasNewReturn => returnExists && !returnFromOtherProject && (!returnReady || returnedHash!=lastValidatedReturnHash);
    private bool CanUseCandidate => lastCandidate is not null && !HasNewReturn;
    private bool IsOdinProject => document?.SourceGamePath==DefaultSkeletonPath;

    private sealed record WorkflowSettings(string? ProjectDirectory=null,string? CandidatePath=null,string BlenderExecutable="",string ValidatedReturnHash="",string? SelectedSource=null,string? TargetGamePath=null,int SourceMode=0,bool GuideCompleted=false,bool EnableDevelopmentCommands=false,int Version=2);

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
                    guideCompleted=saved.GuideCompleted;
                    developmentMode=saved.EnableDevelopmentCommands;
                }
            }
            catch(Exception e) { log.Warning("Could not restore workflow settings: {Detail}",SafeDetail(e.Message)); }
        }
        if(document is not null && activeProjectDirectory is not null)
        {
            source=document.SourceGamePath;
            localTargetGamePath=source;
            sourceMode=IsOdinProject?0:1;
            if(restored?.SelectedSource is{} selected) {sourceMode=Math.Clamp(restored.SourceMode,0,2);source=sourceMode==2?storage.Resolve(selected)??"":selected;localTargetGamePath=restored.TargetGamePath??document.SourceGamePath;}
            if(lastCandidate is not null)
            {
                try { CheckCandidate(); }
                catch(Exception e) { log.Warning("Ignoring an invalid saved candidate: {Detail}",SafeDetail(e.ToString()));lastCandidate=null;candidateDocument=null; }
            }
            workflowStep=lastCandidate is null?1:2;
            status=lastCandidate is null?"�ѻָ��ϴ���Ŀ�����Լ����� Blender �༭��":"�ѻָ��ϴμ��ͨ���ĹǼܣ����Լ����༭��Ԥ����";
        }
        ConfigureProjectReturn(false);
        blenderPathInput=blenderExecutable;
        showGuide=!guideCompleted || !BlenderConfigured;
        if(!BlenderConfigured)RequestBlenderSetup();
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
        blenderLaunchPending=false;
        returnCaption="�� Blender ��ɱ༭�󣬵����������Ϸ����";
        if(activeProjectDirectory is not null)File.WriteAllText(Path.Combine(activeProjectDirectory,"bridge.json"),JsonSerializer.Serialize(new{schemaVersion=1,pluginRoot=Path.GetRelativePath(activeProjectDirectory,storage.Root).Replace('\\','/'),projectId=document!.ProjectId},new JsonSerializerOptions{WriteIndented=true}));
    }

    private bool SaveWorkflow()
    {
        if(settingsPath.Length==0)return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            var selectedSource=sourceMode==2?storage.Relative(activeProjectDirectory is null?null:Path.Combine(activeProjectDirectory,"original.sklb")):source;
            var state=new WorkflowSettings(storage.Relative(activeProjectDirectory),storage.Relative(lastCandidate),blenderExecutable,lastValidatedReturnHash,selectedSource,localTargetGamePath,sourceMode,guideCompleted,developmentMode);
            File.WriteAllText(settingsPath+".tmp",JsonSerializer.Serialize(state,new JsonSerializerOptions{WriteIndented=true}));
            File.Move(settingsPath+".tmp",settingsPath,true);
            return true;
        }
        catch(Exception e) { log.Warning("Could not save workflow settings: {Detail}",SafeDetail(e.Message));return false; }
    }

    private void PollWorkflow()
    {
        if(blenderLaunchPending && activeProjectDirectory is not null)
        {
            try
            {
                using var launch=JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(storage.ReturnFile)!,"blender-launch.json")));
                if(!launch.RootElement.TryGetProperty("projectId",out var id) || id.GetGuid()==document!.ProjectId)
                {
                    var state=launch.RootElement.GetProperty("state").GetString();
                    if(state=="opened") {status="Blender �Ѵ򿪲����뵱ǰ�Ǽܡ��༭��ɺ�����������Ϸ����";blenderLaunchPending=false;}
                    else if(state=="failed") {operationFailed=true;status="Blender ����Ŀʧ�ܣ�"+SafeDetail(launch.RootElement.GetProperty("error").GetString()??"");blenderLaunchPending=false;}
                }
            }
            catch(Exception e) when(e is IOException or JsonException) { /* The child process may still be writing its receipt. */ }
        }
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
            if(!returnExists) { returnCaption="��δ�յ��޸ġ��� Blender �����������Ϸ����������Զ�ʶ��";return; }
            returnedHash=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(returnedPath)));
            if(!complete.Exists) { returnCaption="�ļ���δ������ɣ���ʹ�� Blender ���ġ�������Ϸ����";return; }
            using var manifest=JsonDocument.Parse(File.ReadAllText(complete.FullName));
            if(manifest.RootElement.GetProperty("projectId").GetGuid()!=document.ProjectId)
            { returnFromOtherProject=true;returnCaption="�̶�����Ŀ¼����������Ŀ���ļ�������Ŀ��ɱ༭������������Ϸ�����ɸ��¡�";return; }
            if(manifest.RootElement.GetProperty("gltfSha256").GetString()!=returnedHash)
            { returnCaption="�ļ�����д����ѱ��޸ģ���� Blender ���·��ء�";return; }
            returnReady=true;
            returnCaption=HasNewReturn?"���յ� Blender �޸ģ����Իص���顣":"��� Blender �޸��Ѿ����ͨ����";
        }
        catch(Exception e) when(e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or UnauthorizedAccessException)
        { returnReady=false;returnCaption="�����ļ���ʱ�޷���ȡ������� Blender ���������ԡ�";observedReturn=""; }
    }

    private void DrawWorkflow()
    {
        if(showGuide) {DrawOnboarding();return;}
        if(ImGui.SmallButton("ʹ��ָ���� Blender ����"))ShowOnboarding();
        ImGui.TextColored(Accent,"Ϊ��װ���ӹ���");
        ImGui.TextWrapped("������Ŀ���� Blender �༭���ٻص�����������á�");
        if(document is not null)
            ImGui.TextUnformatted($"��ǰ��Ŀ��{(IsOdinProject?"��ԭŮ �� �¶�����":document.Name)}  |  ԭ�� {document.Bones.Count}  ��  ���� {(candidateDocument?.Bones.Count??document.Bones.Count)-document.Bones.Count-(candidateDocument?.AnchorBindings.Count??0)}");
        ImGui.Spacing();
        if(preview.Active)
        {
            ImGui.TextColored(Accent,"��ʱԤ��������");
            ImGui.SameLine();
            ImGui.BeginDisabled(busy);
            if(ImGui.SmallButton("����Ԥ�����ָ�"))ScheduleWorkflowAction(StopWorkflowPreview);
            ImGui.EndDisabled();
        }
        string[] steps=["1  ׼���Ǽ�","2  Blender �༭","3  �ص�������"];
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
        ImGui.TextColored(operationFailed?new Vector4(1,0.66f,0.38f,1):Accent,busy?"���ڴ�����":operationFailed?"��Ҫ����":"������ʾ");
        ImGui.TextWrapped(SafeDetail(status));
        ImGui.Spacing();
        if(ImGui.CollapsingHeader("�߼���������"))DrawAdvancedWorkflow();
    }

    private void DrawPrepareStep()
    {
        ImGui.TextUnformatted("ѡ��Ҫ�༭�ķ�װ�Ǽ�");
        string[] sources=["ԭ����ԭŮ �� �¶�����","������Ϸ�Ǽ�","���عǼ��ļ�"];
        if(ImGui.BeginCombo("�Ǽ���Դ",sources[sourceMode]))
        {
            for(var i=0;i<sources.Length;i++)if(ImGui.Selectable(sources[i],sourceMode==i))
            {
                sourceMode=i;
                if(i==0) {source=DefaultSkeletonPath;modelPath="chara/equipment/e0100/model/c0201e0100_top.mdl";}
                else if(i==2 && !Path.IsPathRooted(source))source="";
            }
            ImGui.EndCombo();
        }
        if(sourceMode==1)ImGui.InputText("��Ϸ�Ǽ�·��",ref source,1024);
        if(sourceMode==2)
        {
            if(ImGui.Button("ѡ�� SKLB �ļ���"))fileDialogs.OpenFileDialog("ѡ���װ�Ǽ�",".sklb",(ok,path)=>
            {
                if(!ok)return;
                source=path;
                var name=System.Text.RegularExpressions.Regex.Match(Path.GetFileName(path),@"^skl_c(\d{4})t(\d{4})\.sklb$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if(name.Success)localTargetGamePath=$"chara/human/c{name.Groups[1].Value}/skeleton/top/t{name.Groups[2].Value}/skl_c{name.Groups[1].Value}t{name.Groups[2].Value}.sklb";
            });
            ImGui.SameLine();ImGui.TextUnformatted(string.IsNullOrWhiteSpace(source)?"δѡ���ļ�":Path.GetFileName(source));
            ImGui.InputText("Ҫ�滻����Ϸ·��",ref localTargetGamePath,1024);
        }
        ImGui.TextWrapped(sourceMode==0?"��ȡ��Ϸԭ��װ���Ǽܣ������������Ǽ���Ϊ Blender �еĶ�λ�ο���":"��ǰ֧�����µĸ��ӹǼܣ�top���������ο����Զ�����Ϸԭ���ļ���ȡ��");
        ImGui.Spacing();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(source) || sourceMode==2 && !File.Exists(source));
        if(PrimaryButton("������Ŀ���� Blender"))ScheduleRoundTrip(true);
        if(ImGui.Button("ֻ������Ŀ"))ScheduleRoundTrip();
        ImGui.EndDisabled();
        if(document is not null)
        {
            ImGui.SameLine();
            if(ImGui.Button("������ǰ��Ŀ"))workflowStep=1;
        }
    }

    private void DrawBlenderStep()
    {
        ImGui.TextUnformatted("�� Blender �б༭��������");
        ImGui.TextWrapped("�򿪺���Զ������װ�ǼܺͶ�λ�ο������谲װ��չ��Ѱ���ļ���");
        if(PrimaryButton("�򿪵�ǰ��Ŀ�� Blender"))TryUi(OpenBlender);
        ImGui.Spacing();
        ImGui.TextWrapped("���Ҳࡰ��װ�Ǽܡ�������ӹ������������ɺ�����������Ϸ�����ص���Ϸ���ɼ�����");
        ImGui.TextWrapped("��������ҵ����װԭ����ʾ��ͬһ�Ǽ��С�ѡ�ҵ��������������ѡ����������ѡ�ҵ㣬Ctrl+P �� ����ƫ�ơ�δ�ùҵ���ڷ���ʱ�Զ����ԡ�");
        ImGui.TextWrapped("ԭ�й�����Ҫ��������װ������Ȩ������ģ�͹��ߴ�����");
        ImGui.Spacing();
        ImGui.TextColored(returnReady?Good:Accent,returnCaption);
        ImGui.BeginDisabled(!returnReady);
        if(ImGui.Button("�ص������",new Vector2(-1,36)))ScheduleAppend();
        ImGui.EndDisabled();
        if(ImGui.Button("����Ŀ�ļ���"))TryUi(()=>OpenFolder(activeProjectDirectory!));
    }

    private void DrawReturnStep()
    {
        if(HasNewReturn || lastCandidate is null)
        {
            ImGui.TextWrapped(returnCaption);
            ImGui.BeginDisabled(!returnReady);
            if(PrimaryButton("��� Blender �޸�"))ScheduleAppend();
            ImGui.EndDisabled();
            if(!returnReady && ImGui.Button("ǰ�� Blender �༭"))workflowStep=1;
        }
        if(candidateDocument is null || document is null)return;
        ImGui.Spacing();
        ImGui.TextColored(CanUseCandidate?Good:Accent,CanUseCandidate?"�Ǽܼ��ͨ��":"Blender �����޸ģ��������¼��");
        ImGui.TextUnformatted($"���� {document.Bones.Count} ��ԭ��  ��  ���� {candidateDocument.Bones.Count-document.Bones.Count-candidateDocument.AnchorBindings.Count} ������  ��  ����ҵ� {candidateDocument.AnchorBindings.Count}");
        if(candidateDocument.AnchorSchemaVersion==1)
        {
            ImGui.TextColored(Accent,"δʹ�õ�����ҵ��Ѻ��ԣ�ԭ��װ�ҽӱ��֡�");
            if(candidateDocument.AnchorBindings.Count>0)ImGui.TextWrapped("�����ҵ㣺"+string.Join("��",candidateDocument.AnchorBindings.Select(a=>a.BoneName)));
            ImGui.TextWrapped("�Ѻ˶�ԭ�Ǿ�ֹ��̬�����������ź�Ĺ����㡣��������ʵ���涯��������Ϸ�в��ԣ���װȨ������������");
        }
        else ImGui.TextWrapped("�Ѻ˶�ԭ�ǡ���������ֹ��̬�������㡣�����ڴ��Ŷ�Ӧװ���Ľ�ɫ�����á�");
        ImGui.BeginDisabled(!CanUseCandidate);
        if(PrimaryButton(preview.Active?"���µ�ǰ��ɫ��Ԥ��":"�ڵ�ǰ��ɫ��Ԥ��"))ScheduleWorkflowAction(PreviewCurrentPlayer);
        ImGui.EndDisabled();
        if(ImGui.Button("�����Ϸ���Ƿ��Ѽ���"))ScheduleWorkflowAction(CheckWorkflowRuntime);
        if(runtimeVerified)ImGui.TextColored(Good,"ȫ�������������ҵ�����Դ�ļ��뵱ǰ�汾һ�¡�");
        ImGui.TextWrapped(runtimeReport);
        ImGui.Spacing();
        ImGui.BeginDisabled(!CanUseCandidate);
        if(ImGui.Button("���͵� Penumbra"))ScheduleWorkflowAction(SendWorkflowPackage);
        ImGui.SameLine();
        if(ImGui.Button("������ Mod ��װ��"))ScheduleWorkflowAction(ExportWorkflowPackage);
        ImGui.EndDisabled();
        ImGui.TextWrapped(IsOdinProject?"��װ���Զ�������ԭŮ�¶������ĹǼܹ������� Penumbra �����ú�ɳ��ڱ�����":"��װ������ԭ��װ���Ǽ�·������װ��û�и��ӹǼܹ�������Ҫ�����ù�����");
        ImGui.TextWrapped("��ʱԤ��ʹ�õ�ǰ��ɫ�� Penumbra ���ϣ��ü����е�������ɫҲ���ܵ�Ӱ�졣");
    }

    private static bool PrimaryButton(string label)=>ImGui.Button(label,new Vector2(-1,38));

    private void DrawAdvancedWorkflow()
    {
        ImGui.BeginDisabled(busy);
        if(ImGui.Button("���� Blender"))ShowOnboarding();
        ImGui.TextWrapped("�����ļ��̶������ڣ����Ŀ¼ / BlenderReturn / edited.gltf��");
        if(ImGui.Button("�򿪷����ļ���"))TryUi(()=>OpenFolder(Path.GetDirectoryName(storage.ReturnFile)!));
        if(activeProjectDirectory is not null)
        {
            if(ImGui.Button("�򿪵�ǰ��Ŀ�ļ���"))TryUi(()=>OpenFolder(activeProjectDirectory));
        }
        ImGui.Separator();
        ImGui.Checkbox("�ֶ�ָ��Ԥ������",ref useChosenCollection);
        if(useChosenCollection)
        {
            if(ImGui.Button("ˢ�¼���"))ScheduleWorkflowAction(()=>collections=preview.Collections());
            if(ImGui.BeginCombo("����",collections.GetValueOrDefault(selectedCollection,"��ѡ�񼯺�")))
            {
                foreach(var pair in collections)if(ImGui.Selectable(pair.Value,pair.Key==selectedCollection))selectedCollection=pair.Key;
                ImGui.EndCombo();
            }
        }
        ImGui.InputInt("Ԥ�����ȼ�",ref previewPriority);
        ImGui.InputText("��װģ��·��",ref modelPath,1024);
        if(ImGui.Button("���ԭ��ģ�͹�����"))ScheduleWorkflowAction(CheckModel);
        ImGui.TextWrapped(modelReport);
        if(ImGui.TreeNode("������ϸ"))
        {
            if(document is not null)
            {
                var ordered=ClothingSkeleton.Core.SkeletonValidator.OrderedForAppend(candidateDocument??document,document);
                var anchorIds=(candidateDocument?.AnchorBindings??[]).Select(a=>a.BoneId).ToHashSet();
                foreach(var bone in ordered.Take(100))ImGui.TextUnformatted($"{(anchorIds.Contains(bone.Id)?"�ҵ�":bone.OriginalIndex<0?"����":"ԭ��")}  {bone.Name}  ���� {ordered.FirstOrDefault(b=>b.Id==bone.ParentId)?.Name??"��"}");
            }
            ImGui.TreePop();
        }
        if(lastPackage is not null && ImGui.Button("�򿪰�װ�������ļ���"))TryUi(()=>OpenFolder(Path.GetDirectoryName(lastPackage)!));
        ImGui.EndDisabled();
    }

    private void OpenBlender()
    {
        if(activeProjectDirectory is null)throw new InvalidOperationException("���ȴ����Ǽ���Ŀ��");
        if(!EnsureBlenderConfigured(()=>TryUi(OpenBlender)))return;
        if(openedBlender is not null && !openedBlender.HasExited && openedBlenderProject==activeProjectDirectory)
        {status="��ǰ��Ŀ���� Blender �д򿪣����л��� Blender �����༭��";return;}
        var resources=storage.Session;
        var temporary=Path.Combine(resources,"tmp");
        Directory.CreateDirectory(temporary);
        var script=Path.Combine(storage.Bridge,"blender_open_project.py");
        if(!File.Exists(script))throw new FileNotFoundException("���ȱ�� Blender �Žӹ��ߣ������½�ѹ�����������");
        var start=new ProcessStartInfo(blenderExecutable){UseShellExecute=false,WorkingDirectory=storage.Root,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Normal,RedirectStandardOutput=true,RedirectStandardError=true};
        start.Environment["BLENDER_USER_RESOURCES"]=resources;
        start.Environment["TEMP"]=temporary;
        start.Environment["TMP"]=temporary;
        start.ArgumentList.Add("--factory-startup");
        start.ArgumentList.Add("--python");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(Path.Combine(activeProjectDirectory,"skeleton.gltf"));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(storage.ReturnFile)!,"blender-launch.json"),JsonSerializer.Serialize(new{state="opening",projectId=document!.ProjectId}));
        ReleaseBlenderHandle();
        openedBlender=Process.Start(start)??throw new InvalidOperationException("Blender δ��������");
        openedBlender.OutputDataReceived+=OnBlenderOutput;
        openedBlender.ErrorDataReceived+=OnBlenderOutput;
        openedBlender.BeginOutputReadLine();
        openedBlender.BeginErrorReadLine();
        blenderLaunchPending=true;
        openedBlenderProject=activeProjectDirectory;
        workflowStep=1;
        status="Blender ���ڴ򿪵�ǰ��Ŀ���༭��ɺ����Ҳࡰ��װ�Ǽܡ�����еġ�������Ϸ����";
        SaveWorkflow();
        WriteStatus("blender-opened",status);
    }

    private static void OpenFolder(string path)=>Process.Start(new ProcessStartInfo(path){UseShellExecute=true});

    private void OnBlenderOutput(object sender,DataReceivedEventArgs e)
    {
        if(!disposed && e.Data is{} line)log.Debug("Blender: {Detail}",SafeDetail(line));
    }

    private void ReleaseBlenderHandle()
    {
        if(openedBlender is null)return;
        openedBlender.OutputDataReceived-=OnBlenderOutput;
        openedBlender.ErrorDataReceived-=OnBlenderOutput;
        openedBlender.Dispose();
        openedBlender=null;
    }

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
        if(!CanUseCandidate)throw new InvalidOperationException("Blender �����޸ģ��������¼�顣");
        var effective=pluginInterface.GetIpcSubscriber<int,(bool,bool,(Guid,string))>("Penumbra.GetCollectionForObject.V5").InvokeFunc(0);
        if(!effective.Item1 || effective.Item3.Item1==Guid.Empty)throw new InvalidOperationException("û���ҵ���ǰ��ɫ�� Penumbra ���ϣ����Ƚ�����Ϸ������ Penumbra��");
        var collection=useChosenCollection?selectedCollection:effective.Item3.Item1;
        preview.Start(collection,document!.SourceGamePath,lastCandidate!,previewPriority,IsOdinProject?new ClothingSkeleton.Core.MidlanderFemaleEst(100,9100).GamePath:null);
        pluginInterface.GetIpcSubscriber<int,int,object?>("Penumbra.RedrawObject.V5").InvokeAction(0,0);
        runtimeVerified=false;
        runtimeReport="�������ػ棬�Ժ��Զ����ʵʱ�Ǽܡ��봩�Ŷ�Ӧװ����";
        runtimeCheckAt=DateTime.UtcNow.AddSeconds(3);
        status=$"Ԥ����Ӧ�õ�{(useChosenCollection?"��ѡ����":effective.Item3.Item2)}������ʱ������";
    }

    private void StopWorkflowPreview()
    {
        preview.Stop();
        runtimeCheckAt=null;
        runtimeVerified=false;
        pluginInterface.GetIpcSubscriber<int,int,object?>("Penumbra.RedrawObject.V5").InvokeAction(0,0);
        runtimeReport="��ʱԤ���ѳ������Ѱ�װ�� Mod �԰� Penumbra ������Ч��";
        status="�ѳ�����ʱԤ���������ػ��ɫ��";
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
        if(!CanUseCandidate)throw new InvalidOperationException("���ȼ�����µ� Blender �޸ġ�");
        if(IsOdinProject)ExportFemaleEstPackage();else ExportPackage();
        status="Mod ��װ���ѱ��棬���ڸ߼������д������ļ��С�";
    }

    private void SendWorkflowPackage()
    {
        ExportWorkflowPackage();
        var result=pluginInterface.GetIpcSubscriber<string,int>("Penumbra.InstallMod.V5").InvokeFunc(lastPackage!);
        if(result!=0)throw new InvalidOperationException($"��װ���ѱ��棬�� Penumbra δ���գ�{result}������ȷ�� Penumbra �����á�");
        pluginInterface.GetIpcSubscriber<int,string,string,int>("Penumbra.OpenMainWindow.V5").InvokeFunc(1,"","");
        status="��װ���ѷ��͵� Penumbra�����ڵ�����洦������ѡ�������Ҫ�ļ��������á�";
    }

    private void WriteWorkflowStatus()
    {
        var path=Path.Combine(storage.Root,"Development","workflow-status.json");
        File.WriteAllText(path,JsonSerializer.Serialize(new{step=workflowStep,project=storage.Relative(activeProjectDirectory),candidate=storage.Relative(lastCandidate),returnFile="BlenderReturn/edited.gltf",returnReady,hasNewReturn=HasNewReturn,canUseCandidate=CanUseCandidate,blenderConfigured=BlenderConfigured,sourceMode,previewActive=preview.Active,runtimeVerified,guideCompleted},new JsonSerializerOptions{WriteIndented=true}));
        WriteStatus("workflow-ready","Development/workflow-status.json");
    }
}
