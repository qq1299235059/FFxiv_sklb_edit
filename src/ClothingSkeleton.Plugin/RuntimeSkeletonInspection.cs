using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;

namespace ClothingSkeleton.Plugin;

/// <summary>Copies names from the local player's live skeletons; never writes to them.</summary>
internal static unsafe class RuntimeSkeletonInspection
{
    public static object Capture(IObjectTable objects)
    {
        var executable=System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        var version=executable is null?null:Path.Combine(Path.GetDirectoryName(executable)!,"ffxivgame.ver");
        if(version is null || !File.Exists(version) || File.ReadAllText(version).Trim()!="2026.10.06.0000.0000")
            throw new InvalidOperationException("Runtime skeleton inspection requires the verified CN game build.");
        var player=objects.LocalPlayer ?? throw new InvalidOperationException("Local player is unavailable.");
        var actors=new List<object>();
        foreach(var actor in objects.Where(o=>o.ObjectIndex==player.ObjectIndex || (o.ObjectKind==player.ObjectKind && o.Name.TextValue==player.Name.TextValue)).Take(16))
        {
            var gameObject=(GameObject*)actor.Address;
            var draw=(CharacterBase*)gameObject->DrawObject;
            if(draw==null || draw->Skeleton==null)continue;
            var skeleton=draw->Skeleton;
            if(skeleton->PartialSkeletonCount is 0 or >32 || skeleton->PartialSkeletons==null)
                throw new InvalidDataException("Unexpected live partial skeleton array.");
            var partials=new List<object>();
            var basePose=skeleton->PartialSkeletons[0].GetHavokPose(0);
            var baseSkeleton=basePose==null?null:basePose->Skeleton;
            for(var i=0;i<skeleton->PartialSkeletonCount;i++)
            {
                var partial=&skeleton->PartialSkeletons[i];
                var resource=partial->SkeletonResourceHandle;
                var pose=partial->GetHavokPose(0);
                var connections=new List<object>();
                if(resource!=null)
                {
                    if((resource->SkeletonData.SklbVersion&0xffff)==0x3031)
                    {
                        for(var r=0;r<4;r++)
                        {
                            var parent=resource->SkeletonData.ConnectBoneIds[r];
                            if(parent==ushort.MaxValue)break;
                            connections.Add(new{rootIndex=r,referenceBoneIndex=(int)parent,
                                boneName=baseSkeleton!=null && parent<baseSkeleton->Bones.Length?baseSkeleton->Bones[parent].Name.String:null});
                        }
                    }
                    else if(partial->ConnectedParentBoneIndex>=0)
                    {
                        var parent=partial->ConnectedParentBoneIndex;
                        connections.Add(new{rootIndex=(int)partial->ConnectedBoneIndex,referenceBoneIndex=(int)parent,
                            boneName=baseSkeleton!=null && parent<baseSkeleton->Bones.Length?baseSkeleton->Bones[parent].Name.String:null});
                    }
                }
                partials.Add(new{index=i,resourcePath=resource==null?null:resource->FileName.ToString(),
                    resourceBoneCount=resource==null?0:resource->BoneCount,
                    resourceBones=Names(resource==null?null:resource->HavokSkeleton),
                    poseBones=Names(pose==null?null:pose->Skeleton),
                    connectedParentBoneIndex=partial->ConnectedParentBoneIndex,connectedBoneIndex=partial->ConnectedBoneIndex,attachmentConnections=connections,
                    connectedParentBoneName=baseSkeleton!=null && partial->ConnectedParentBoneIndex>=0 && partial->ConnectedParentBoneIndex<baseSkeleton->Bones.Length
                        ?baseSkeleton->Bones[partial->ConnectedParentBoneIndex].Name.String:null});
            }
            actors.Add(new{objectIndex=actor.ObjectIndex,isLocalPlayer=actor.ObjectIndex==player.ObjectIndex,partials});
        }
        return new{timestamp=DateTimeOffset.UtcNow,readOnly=true,actors};
    }

    private static object? Names(hkaSkeleton* skeleton)
    {
        if(skeleton==null)return null;
        if(skeleton->Bones.Length is <=0 or >32767 || skeleton->ParentIndices.Length!=skeleton->Bones.Length)
            throw new InvalidDataException("Unexpected live Havok bone array.");
        var names=new List<string>(skeleton->Bones.Length);
        var custom=new List<object>();
        for(var i=0;i<skeleton->Bones.Length;i++)
        {
            var name=skeleton->Bones[i].Name.String ?? "";
            names.Add(name);
            if(name.StartsWith("csw_",StringComparison.Ordinal))custom.Add(new{index=i,name,parentIndex=skeleton->ParentIndices[i]});
        }
        return new{count=names.Count,names,custom};
    }
}
