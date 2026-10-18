using System.Text.Json;
using ClothingSkeleton.Core;
using Dalamud.Bindings.ImGui;

namespace ClothingSkeleton.Plugin;

public sealed partial class Plugin
{
    private string characterSelectionKind="self";
    private CharacterSkeletonReader.Selection? characterSelection;

    private CharacterSkeletonReader.Selection ReadCharacterSelection(string kind)
    {
        var selection=CharacterSkeletonReader.Capture(kind=="self"?objects.LocalPlayer:targets.Target,kind);
        if(!data.FileExists(selection.Source.GamePath))throw new InvalidDataException("所选角色对应的原版人物骨架不存在，不能导出该人物模型。");
        var est=data.GetFile<Lumina.Data.FileResource>("chara/xls/charadb/extra_top.est")?.Data??throw new FileNotFoundException("未找到原版上衣骨架关联表。");
        var top=selection.Source.ResolveOriginalTop(est);
        if(top is not null && !data.FileExists(top))top=null;
        return selection with{TopGamePath=top};
    }

    private void ApplyCharacterSelection(string kind)
    {
        characterSelectionKind=kind;
        characterSelection=null;
        var chosen=ReadCharacterSelection(kind);
        characterSelection=chosen;
        if(sourceMode==0 || sourceMode==3)
        {
            if(activeMod is not null && chosen.TopGamePath is null)sourceMode=3;
            source=sourceMode==3?chosen.Source.GamePath:chosen.TopGamePath??"";
            localTargetGamePath=source;
            if(activeMod is null)modelPath=$"chara/equipment/e{chosen.Source.EquippedTopSetId:D4}/model/c{chosen.Source.RaceSexId:D4}e{chosen.Source.EquippedTopSetId:D4}_top.mdl";
        }
        status=$"已选择{(kind=="self"?"自己":"当前目标")}：{chosen.Source.DisplayName}。人物骨架将从原版游戏文件导出。";
        if(chosen.TopGamePath is null)status+="当前上衣没有可直接使用的原版附加骨架，请另选服装骨架路径或文件。";
        SaveWorkflow();
        File.WriteAllText(Path.Combine(outputRoot,"character-selection.json"),JsonSerializer.Serialize(new{characterSource=chosen.Source,originalTopGamePath=chosen.TopGamePath,originalSqPackOnly=true,liveSkeletonCopied=false},SkeletonDocument.JsonOptions));
        WriteStatus("character-selected",status);
    }

    private void ScheduleCharacterSelection(string kind)
    {
        if(busy || disposed)return;
        busy=true;
        operationFailed=false;
        _=framework.RunOnFrameworkThread(()=>
        {
            try {ApplyCharacterSelection(kind);}
            catch(Exception e) {operationFailed=true;status=SafeDetail(e.Message);WriteStatus("failed",e.Message);}
            finally {busy=false;}
        });
    }

    private void DrawCharacterSelection()
    {
        ImGui.TextUnformatted("选择人物骨架来源");
        if(ImGui.Button("读取自己"))ScheduleCharacterSelection("self");
        ImGui.SameLine();
        if(ImGui.Button("读取当前目标"))ScheduleCharacterSelection("target");
        if(characterSelection is{} chosen)
        {
            ImGui.TextColored(Accent,$"已选：{(chosen.Source.SelectionKind=="self"?"自己":"当前目标")} · {chosen.Source.DisplayName}");
            ImGui.TextWrapped("按所选角色导出原版人物骨架，不读取人物 Mod 骨架或当前动作姿态。切换角色、外观或上衣后请重新读取。");
        }
        else ImGui.TextWrapped("先读取自己或当前目标，再导出该角色的原版人物骨架；不会使用预设人物骨架。");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private CharacterSkeletonReader.Selection RequireCurrentCharacterSelection()
    {
        var chosen=characterSelection??throw new InvalidOperationException("请先读取自己或当前目标，选择要导出的人物骨架。");
        var current=ReadCharacterSelection(chosen.Source.SelectionKind);
        if(!chosen.SameCharacterAndModel(current))throw new InvalidOperationException("所选角色、模型或上衣已经改变，请重新读取角色后创建项目。");
        return current;
    }
}
