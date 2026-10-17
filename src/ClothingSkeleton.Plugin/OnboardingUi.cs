using System.Numerics;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClothingSkeleton.Core;
using Dalamud.Bindings.ImGui;

namespace ClothingSkeleton.Plugin;

public sealed partial class Plugin
{
    private const string BlenderPopup="���� Blender###ClothingSkeletonSetup";
    private bool guideCompleted;
    private bool showGuide;
    private bool developmentMode;
    private bool blenderPromptRequested;
    private bool blenderPromptVisible;
    private string blenderPathInput="";
    private string guideError="";
    private Action? afterBlenderSetup;
    private bool BlenderConfigured=>PluginStorage.IsBlenderExecutable(blenderExecutable);

    private void OpenWorkshop()
    {
        open=true;
        if(!guideCompleted)showGuide=true;
        if(!BlenderConfigured)RequestBlenderSetup();
    }

    private void ShowOnboarding()
    {
        showGuide=true;
        blenderPathInput=blenderExecutable;
        guideError="";
    }

    private void RequestBlenderSetup(Action? resume=null)
    {
        ShowOnboarding();
        blenderPromptRequested=true;
        if(resume is not null)afterBlenderSetup=resume;
        status="�������ñ����� Blender ����λ�á������ļ�λ�����ɲ���̶�������";
    }

    private bool EnsureBlenderConfigured(Action resume)
    {
        if(BlenderConfigured)return true;
        RequestBlenderSetup(resume);
        return false;
    }

    private void DrawOnboarding()
    {
        ImGui.TextColored(Accent,"��ӭʹ�÷�װ�Ǽܹ���̨");
        ImGui.TextWrapped("�״�ʹ��ֻ�����ñ����� Blender������λ���ɲ��������������д����Ŀ¼��");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("1  ���� Blender");
        ImGui.TextWrapped("ѡ�� Blender ��װĿ¼�е� blender.exe��Ҳ����ճ������·����װ�ļ��С�");
        DrawBlenderPathInput("guide");
        ImGui.Spacing();
        ImGui.TextUnformatted("2  �༭�뷵��");
        ImGui.TextWrapped("������Ŀ���� Blender �� ���Ҳࡰ��װ�Ǽܡ����༭ �� �����������Ϸ�� �� �л���Ϸ��顣");
        ImGui.TextWrapped("����ҵ����װԭ����ʾ��ͬһ�Ǽܡ����¹����ֶ�ָ��������δʹ�õĹҵ���ڷ���ʱ�Զ����ԡ�");
        ImGui.TextWrapped("�����ļ��̶������ڣ����Ŀ¼ / BlenderReturn / edited.gltf��������Ŀ���ô�λ�ã������ʶ��ǰ��Ŀ�����⵼��������Ŀ���ļ���");
        ImGui.Spacing();
        ImGui.TextUnformatted("3  �����ʵװ");
        ImGui.TextWrapped("���ͨ�����Ԥ������ʱ������Ҳ�����ɰ�װ�����͵� Penumbra����װȨ�ء�������������Ҫ����������");
        ImGui.Spacing();
        ImGui.TextWrapped("���豸���������ø��豸�� Blender������ַ���ʹ�øɾ��Ĳ�� ZIP����Ҫ����Ѿ�ʹ�ù����������Ŀ¼��");
        ImGui.Spacing();
        if(PrimaryButton("���� Blender ���ò����빤��̨"))SaveBlenderSetup();
        if(ImGui.Button("�Ժ����ã��Ȳ鿴����̨")) {showGuide=false;afterBlenderSetup=null;}
    }

    private void DrawBlenderPathInput(string id)
    {
        ImGui.PushID(id);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##blender-path","ѡ���ճ�� blender.exe ��λ��",ref blenderPathInput,4096);
        if(ImGui.Button("ѡ�� Blender ����"))BrowseBlender(false);
        if(guideError.Length>0)ImGui.TextWrapped(guideError);
        ImGui.PopID();
    }

    private void DrawBlenderSetupPopup()
    {
        if(blenderPromptRequested)
        {
            ImGui.OpenPopup(BlenderPopup);
            blenderPromptVisible=true;
            blenderPromptRequested=false;
        }
        ImGui.SetNextWindowSize(new Vector2(520,0),ImGuiCond.Appearing);
        if(!ImGui.BeginPopupModal(BlenderPopup,ref blenderPromptVisible,ImGuiWindowFlags.AlwaysAutoResize))return;
        ImGui.TextWrapped("��δ������Ч�� Blender ��ַ������д���� blender.exe ��λ�ú������");
        ImGui.SetNextItemWidth(470);
        ImGui.InputTextWithHint("##popup-blender-path","Blender ����λ��",ref blenderPathInput,4096);
        if(ImGui.Button("ѡ�� Blender ����"))BrowseBlender(true);
        if(guideError.Length>0)ImGui.TextWrapped(guideError);
        if(ImGui.Button("���沢����") && SaveBlenderSetup())ImGui.CloseCurrentPopup();
        ImGui.SameLine();
        if(ImGui.Button("�Ժ�����")) {afterBlenderSetup=null;blenderPromptVisible=false;ImGui.CloseCurrentPopup();}
        ImGui.EndPopup();
    }

    private void BrowseBlender(bool reopenPopup)
    {
        if(reopenPopup) {blenderPromptVisible=false;ImGui.CloseCurrentPopup();}
        fileDialogs.OpenFileDialog("ѡ�� Blender ����",".exe",(ok,path)=>
        {
            if(ok) {blenderPathInput=path;guideError="";}
            if(reopenPopup)blenderPromptRequested=true;
        });
    }

    private bool SaveBlenderSetup()
    {
        try
        {
            var path=PluginStorage.NormalizeBlenderPath(blenderPathInput);
            if(string.IsNullOrWhiteSpace(path)) {guideError="Blender ��ַ����Ϊ�գ�����д��ѡ�� blender.exe��";return false;}
            if(!PluginStorage.IsBlenderExecutable(path))
            {guideError="û���ҵ� blender.exe����ѡ��ʵ�ʰ�װ�� Blender ����";return false;}
            var previousExecutable=blenderExecutable;
            var previousGuide=guideCompleted;
            blenderExecutable=Path.GetFullPath(path);
            blenderPathInput=blenderExecutable;
            guideCompleted=true;
            guideError="";
            showGuide=false;
            if(!SaveWorkflow())
            {
                blenderExecutable=previousExecutable;
                guideCompleted=previousGuide;
                guideError="����δ�ܱ��棬��ȷ�ϲ��Ŀ¼����д������ԡ�";
                return false;
            }
            status="Blender �����ѱ��档���ڿ��Դ����Ǽ���Ŀ��";
            var resume=afterBlenderSetup;
            afterBlenderSetup=null;
            if(resume is not null)TryUi(resume);
            return true;
        }
        catch(Exception e) when(e is ArgumentException or IOException or UnauthorizedAccessException)
        {guideError="�޷��������ã���ȷ�� Blender ����Ͳ��Ŀ¼�ɷ��ʡ�";return false;}
    }

    private string SafeDetail(string text)
    {
        var safe=text.Replace(storage.Root,"<���Ŀ¼>",StringComparison.OrdinalIgnoreCase)
            .Replace(storage.Root.Replace('\\','/'),"<���Ŀ¼>",StringComparison.OrdinalIgnoreCase);
        if(!string.IsNullOrWhiteSpace(blenderExecutable))safe=safe.Replace(blenderExecutable,"<Blender>",StringComparison.OrdinalIgnoreCase);
        safe=Regex.Replace(safe,@"(?i)(?:[a-z]:[\\/]|\\\\)[^\r\n\t\""<>]+","<����·��>");
        return Regex.Replace(safe,@"/(?:Users|home)/[^\s\""<>]+","<����·��>");
    }

    private string SafeDiagnosticJson(string json)
    {
        JsonNode? Clean(JsonNode? node)
        {
            if(node is JsonObject obj)
            {
                var result=new JsonObject();
                foreach(var pair in obj)
                {
                    var key=SafeDetail(pair.Key);
                    while(result.ContainsKey(key))key+="_";
                    result[key]=Clean(pair.Value);
                }
                return result;
            }
            if(node is JsonArray array)return new JsonArray(array.Select(Clean).ToArray());
            return node is JsonValue value && value.TryGetValue<string>(out var text)?JsonValue.Create(SafeDetail(text)):node?.DeepClone();
        }
        return Clean(JsonNode.Parse(json))!.ToJsonString(new JsonSerializerOptions{WriteIndented=true});
    }
}
