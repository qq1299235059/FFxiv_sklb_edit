using System.Numerics;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClothingSkeleton.Core;
using Dalamud.Bindings.ImGui;

namespace ClothingSkeleton.Plugin;

public sealed partial class Plugin
{
    private const string BlenderPopup="设置 Blender###ClothingSkeletonSetup";
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
        status="请先设置本机的 Blender 程序位置。返回文件位置已由插件固定管理。";
    }

    private bool EnsureBlenderConfigured(Action resume)
    {
        if(BlenderConfigured)return true;
        RequestBlenderSetup(resume);
        return false;
    }

    private void DrawOnboarding()
    {
        ImGui.TextColored(Accent,"欢迎使用服装骨架工作台");
        ImGui.TextWrapped("首次使用只需设置本机的 Blender。返回位置由插件管理，无需填写其他目录。");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("1  设置 Blender");
        ImGui.TextWrapped("选择 Blender 安装目录中的 blender.exe，也可以粘贴程序路径或安装文件夹。");
        DrawBlenderPathInput("guide");
        ImGui.Spacing();
        ImGui.TextUnformatted("2  编辑与返回");
        ImGui.TextWrapped("创建项目并打开 Blender → 在右侧“服装骨架”面板编辑 → 点击“返回游戏” → 切回游戏检查。");
        ImGui.TextWrapped("人体挂点与服装原骨显示在同一骨架。给新骨链手动指定父级，未使用的挂点会在返回时自动忽略。");
        ImGui.TextWrapped("返回文件固定保存在：插件目录 / BlenderReturn / edited.gltf。所有项目共用此位置，插件会识别当前项目，避免导入其他项目的文件。");
        ImGui.Spacing();
        ImGui.TextUnformatted("3  检查与实装");
        ImGui.TextWrapped("检查通过后可预览并随时撤销，也可生成安装包发送到 Penumbra。服装权重、动画和物理需要另行制作。");
        ImGui.Spacing();
        ImGui.TextWrapped("换设备后重新设置该设备的 Blender。对外分发请使用干净的插件 ZIP，不要打包已经使用过的整个插件目录。");
        ImGui.Spacing();
        if(PrimaryButton("保存 Blender 设置并进入工作台"))SaveBlenderSetup();
        if(ImGui.Button("稍后设置，先查看工作台")) {showGuide=false;afterBlenderSetup=null;}
    }

    private void DrawBlenderPathInput(string id)
    {
        ImGui.PushID(id);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##blender-path","选择或粘贴 blender.exe 的位置",ref blenderPathInput,4096);
        if(ImGui.Button("选择 Blender 程序…"))BrowseBlender(false);
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
        ImGui.TextWrapped("尚未设置有效的 Blender 地址。请填写本机 blender.exe 的位置后继续。");
        ImGui.SetNextItemWidth(470);
        ImGui.InputTextWithHint("##popup-blender-path","Blender 程序位置",ref blenderPathInput,4096);
        if(ImGui.Button("选择 Blender 程序…"))BrowseBlender(true);
        if(guideError.Length>0)ImGui.TextWrapped(guideError);
        if(ImGui.Button("保存并继续") && SaveBlenderSetup())ImGui.CloseCurrentPopup();
        ImGui.SameLine();
        if(ImGui.Button("稍后设置")) {afterBlenderSetup=null;blenderPromptVisible=false;ImGui.CloseCurrentPopup();}
        ImGui.EndPopup();
    }

    private void BrowseBlender(bool reopenPopup)
    {
        if(reopenPopup) {blenderPromptVisible=false;ImGui.CloseCurrentPopup();}
        fileDialogs.OpenFileDialog("选择 Blender 程序",".exe",(ok,path)=>
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
            if(string.IsNullOrWhiteSpace(path)) {guideError="Blender 地址不能为空，请填写或选择 blender.exe。";return false;}
            if(!PluginStorage.IsBlenderExecutable(path))
            {guideError="没有找到 blender.exe，请选择实际安装的 Blender 程序。";return false;}
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
                guideError="设置未能保存，请确认插件目录允许写入后重试。";
                return false;
            }
            status="Blender 设置已保存。现在可以创建骨架项目。";
            var resume=afterBlenderSetup;
            afterBlenderSetup=null;
            if(resume is not null)TryUi(resume);
            return true;
        }
        catch(Exception e) when(e is ArgumentException or IOException or UnauthorizedAccessException)
        {guideError="无法保存设置，请确认 Blender 程序和插件目录可访问。";return false;}
    }

    private string SafeDetail(string text)
    {
        var safe=text.Replace(storage.Root,"<插件目录>",StringComparison.OrdinalIgnoreCase)
            .Replace(storage.Root.Replace('\\','/'),"<插件目录>",StringComparison.OrdinalIgnoreCase);
        if(!string.IsNullOrWhiteSpace(blenderExecutable))safe=safe.Replace(blenderExecutable,"<Blender>",StringComparison.OrdinalIgnoreCase);
        safe=Regex.Replace(safe,@"(?i)(?:[a-z]:[\\/]|\\\\)[^\r\n\t\""<>]+","<本地路径>");
        return Regex.Replace(safe,@"/(?:Users|home)/[^\s\""<>]+","<本地路径>");
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
