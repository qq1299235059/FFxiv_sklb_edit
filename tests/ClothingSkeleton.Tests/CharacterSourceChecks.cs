using System.Buffers.Binary;
using System.Text.Json;
using ClothingSkeleton.Core;

internal static class CharacterSourceChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var self=new CharacterSkeletonSource("self",201,100);
        var target=new CharacterSkeletonSource("target",801,100);
        check(self.GamePath=="chara/human/c0201/skeleton/base/b0001/skl_c0201b0001.sklb" && target.GamePath=="chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb","body resource is resolved from the selected character model instead of a preset");
        check(self.DisplayName=="中原之民·女" && target.DisplayName=="猫魅族·女","selected character model has a non-identifying UI description");
        var json=JsonSerializer.Serialize(target,SkeletonDocument.JsonOptions);
        var restored=JsonSerializer.Deserialize<CharacterSkeletonSource>(json,SkeletonDocument.JsonOptions)!;
        check(restored==target && restored.Origin=="selected-character-original-sqpack" && restored.GamePath==target.GamePath,"selected character provenance survives a portable JSON round trip");
        check(Enumerable.Range(1,18).All(i=>new CharacterSkeletonSource("self",100*i+1,0).DisplayName.Length>0),"all recognized playable model codes have a valid identity mapping");
        reject(()=>new CharacterSkeletonSource("preset",201,100).Validate(),"unknown character selection kinds are rejected");
        reject(()=>new CharacterSkeletonSource("self",0,100).Validate(),"missing character model cannot fall back to a preset");
        reject(()=>new CharacterSkeletonSource("target",202,100).Validate(),"unrecognized NPC model variants cannot guess a base skeleton");
        reject(()=>new CharacterSkeletonSource("target",1901,100).Validate(),"unsupported character model IDs cannot index the model list");
        reject(()=>self.CheckEquipmentPath("chara/human/c0801/skeleton/top/t0100/skl_c0801t0100.sklb"),"equipment from a different race cannot replace the selected body reference");
        reject(()=>self.CheckEquipmentPath("chara/human/c0201/skeleton/top/t0100/skl_c0201t0101.sklb"),"equipment filename and virtual path must describe the same skeleton");
        self.CheckEquipmentPath("chara/human/c0201/skeleton/top/t0100/skl_c0201t0100.sklb");
        var est=new byte[22];BinaryPrimitives.WriteInt32LittleEndian(est,3);
        (ushort set,ushort race,ushort skeleton)[] rows=[(100,201,100),(100,801,377),(200,201,0)];
        for(var i=0;i<rows.Length;i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(est.AsSpan(4+4*i),rows[i].set);
            BinaryPrimitives.WriteUInt16LittleEndian(est.AsSpan(6+4*i),rows[i].race);
            BinaryPrimitives.WriteUInt16LittleEndian(est.AsSpan(16+2*i),rows[i].skeleton);
        }
        check(self.ResolveOriginalTop(est)=="chara/human/c0201/skeleton/top/t0100/skl_c0201t0100.sklb" && target.ResolveOriginalTop(est)=="chara/human/c0801/skeleton/top/t0377/skl_c0801t0377.sklb","original top selection uses both the selected model and equipped set");
        check(new CharacterSkeletonSource("self",101,100).ResolveOriginalTop(est) is null && new CharacterSkeletonSource("self",201,200).ResolveOriginalTop(est) is null,"missing or zero original top entries never fall back to the Odin preset");
        reject(()=>self.ResolveOriginalTop(est.AsSpan(0,21)),"a truncated original EST table is rejected");
        var duplicate=est.ToArray();BinaryPrimitives.WriteUInt16LittleEndian(duplicate.AsSpan(12),100);
        reject(()=>self.ResolveOriginalTop(duplicate),"duplicate original equipment associations cannot be chosen arbitrarily");
    }
}
