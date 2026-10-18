using System.Numerics;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClothingSkeleton.Core;
using Dalamud.Bindings.ImGui;

namespace ClothingSkeleton.Plugin;

public sealed partial class Plugin
{
    private bool guideCompleted;
    private bool showGuide;
    private bool developmentMode;
    private string guideError="";

    private void OpenWorkshop() {open=true;if(!guideCompleted)showGuide=true;}
    private void ShowOnboarding() {showGuide=true;guideError="";}

    private void DrawOnboarding()
    {
        ImGui.TextColored(Accent,"服装骨架工作台 · Blender 互通");
        ImGui.TextWrapped("保持 Blender 打开，骨架会发送到当前工程。无需填写 Blender 程序或返回文件地址。");
        ImGui.Separator();
        ImGui.TextUnformatted("1  安装并启用 Blender 插件");
        ImGui.TextWrapped("从仓库下载同版本 ClothingSkeleton-Blender ZIP，在 Blender 偏好设置 → 插件 → 从磁盘安装，选择 ZIP 并启用 Clothing Skeleton Bridge。");
        ImGui.TextUnformatted("2  连接游戏");
        ImGui.TextWrapped("Blender 的 3D 视图按 N，打开“服装骨架”面板，点击“连接游戏”。两侧使用相同本机端口；默认自动连接。");
        DrawBridgeStatus();
        ImGui.SetNextItemWidth(150);
        ImGui.InputInt("本机连接端口",ref bridgePort);
        if(ImGui.Button("应用端口并重连"))
        {
            if(bridgePort is <1 or >65535)guideError="端口应在 1 到 65535 之间。";
            else {StartBridge();SaveWorkflow();guideError=bridgeError;}
        }
        if(guideError.Length>0)ImGui.TextWrapped(guideError);
        ImGui.TextUnformatted("3  按 Mod 制作与返回");
        ImGui.TextWrapped("读取自己或当前目标 → 创建独立 Mod → 选择仅骨骼或完整服装及可选参考组 → 创建并发送到 Blender → 编辑对应部件 → 一键发送 → 游戏检查并更新当前 Mod。");
        ImGui.TextWrapped("人物骨架来自所选角色对应的原版文件。完整人体挂点与服装原骨位于同一骨架；未用挂点在返回时自动忽略。每件装备使用独立集合与骨架。服装网格需正确绑定权重；动画和物理需要另行制作。");
        ImGui.TextWrapped("重复发送同一项目会保留已有编辑。新项目会新增骨架对象，不会清空 Blender 场景。请自行保存 .blend 工程。");
        ImGui.TextWrapped("新流程按 Mod 固定保存返回和默认输出，不需填写路径。没有新增服装骨骼时只输出模型，不生成 SKLB；旧版项目继续使用插件内固定返回目录。仅连接本机，不需要开放防火墙或局域网端口。");
        if(PrimaryButton("进入工作台")) {guideCompleted=true;showGuide=false;SaveWorkflow();}
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
