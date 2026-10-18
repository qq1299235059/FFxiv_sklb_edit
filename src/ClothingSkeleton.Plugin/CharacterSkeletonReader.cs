using ClothingSkeleton.Core;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace ClothingSkeleton.Plugin;

/// <summary>Read on the Framework thread. Copies model identity, never the character's Mod skeleton or pose.</summary>
internal static unsafe class CharacterSkeletonReader
{
    internal sealed record Selection(CharacterSkeletonSource Source,ulong ObjectId,ushort ObjectIndex,nint Address,string? TopGamePath=null)
    {
        public bool SameCharacterAndModel(Selection other)=>ObjectId==other.ObjectId && ObjectIndex==other.ObjectIndex && Address==other.Address && Source==other.Source;
    }

    public static Selection Capture(IGameObject? actor,string selectionKind)
    {
        HavokSession.EnsureSupportedGameBuild();
        if(actor is not ICharacter || actor.Address==0)
            throw new InvalidOperationException(selectionKind=="self"?"未找到自己的角色，请进入场景后读取。":"当前目标不是人物角色，请先选中场景中的人形角色。");
        var gameObject=(GameObject*)actor.Address;
        var draw=(CharacterBase*)gameObject->DrawObject;
        if(draw==null || draw->GetModelType()!=CharacterBase.ModelType.Human)
            throw new InvalidOperationException("角色的人形模型尚未加载，请等待角色显示后重新读取。");
        var human=(Human*)draw;
        var source=new CharacterSkeletonSource(selectionKind,human->RaceSexId,human->Top.Id);
        source.Validate();
        return new(source,actor.GameObjectId,actor.ObjectIndex,actor.Address);
    }
}
