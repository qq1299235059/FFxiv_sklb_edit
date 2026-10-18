using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace ClothingSkeleton.Core;

/// <summary>Only model information is saved; character names, IDs and live skeleton data are excluded.</summary>
public sealed record CharacterSkeletonSource(string SelectionKind,int RaceSexId,int EquippedTopSetId)
{
    public int SchemaVersion=>1;
    public string Origin=>"selected-character-original-sqpack";
    public string GamePath=>$"chara/human/c{RaceSexId:D4}/skeleton/base/b0001/skl_c{RaceSexId:D4}b0001.sklb";
    public string DisplayName
    {
        get
        {
            string[] models=["中原之民·男","中原之民·女","高地之民·男","高地之民·女","精灵族·男","精灵族·女",
                "猫魅族·男","猫魅族·女","鲁加族·男","鲁加族·女","拉拉菲尔族·男","拉拉菲尔族·女",
                "敖龙族·男","敖龙族·女","硌狮族·男","硌狮族·女","维埃拉族·男","维埃拉族·女"];
            Validate();return models[RaceSexId/100-1];
        }
    }

    public void Validate()
    {
        if(SelectionKind is not ("self" or "target") || RaceSexId%100!=1 || RaceSexId/100 is <1 or >18 || EquippedTopSetId is <0 or >ushort.MaxValue)
            throw new InvalidDataException("所选角色不是受支持的人物模型，请选择自己或人形角色目标。");
    }

    public void CheckEquipmentPath(string gamePath)
    {
        Validate();
        var match=Regex.Match(gamePath,@"^chara/human/c(?<race>\d{4})/skeleton/top/t(?<id>\d{4})/skl_c\k<race>t\k<id>\.sklb$");
        if(!match.Success || int.Parse(match.Groups["race"].Value)!=RaceSexId)
            throw new InvalidDataException("服装骨架与所选角色的种族性别不一致，请选择该角色适用的服装骨架；不会自动改用预设人物骨架。");
    }

    public string? ResolveOriginalTop(ReadOnlySpan<byte> est)
    {
        Validate();
        if(est.Length<4)throw new InvalidDataException("原版上衣骨架关联表不完整。");
        var count=BinaryPrimitives.ReadInt32LittleEndian(est);
        if(count<0 || 4L+6L*count!=est.Length)throw new InvalidDataException("原版上衣骨架关联表长度无效。");
        int? skeleton=null;
        for(var i=0;i<count;i++)
        {
            var set=BinaryPrimitives.ReadUInt16LittleEndian(est[(4+4*i)..]);
            var race=BinaryPrimitives.ReadUInt16LittleEndian(est[(6+4*i)..]);
            if(set!=EquippedTopSetId || race!=RaceSexId)continue;
            if(skeleton is not null)throw new InvalidDataException("原版上衣骨架关联不唯一。");
            skeleton=BinaryPrimitives.ReadUInt16LittleEndian(est[(4+4*count+2*i)..]);
        }
        return skeleton is >0?$"chara/human/c{RaceSexId:D4}/skeleton/top/t{skeleton:D4}/skl_c{RaceSexId:D4}t{skeleton:D4}.sklb":null;
    }
}
