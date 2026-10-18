using System.Buffers.Binary;
using System.Numerics;
using System.Text.RegularExpressions;

namespace ClothingSkeleton.Core;

public sealed record ExistingAnchor(Guid RootId,string RootName,string BoneName,int ReferenceBoneIndex);
public sealed record AnchorRigInfo(Guid ProjectId,string SourceGamePath,string SourceSha256,string ReferenceGamePath,string ReferenceSha256,
    List<ExistingAnchor> ExistingAnchors,List<AttachmentTarget> Targets,int SchemaVersion=1,bool Available=true,int MaxConnections=4,
    CharacterSkeletonSource? CharacterSource=null);

/// <summary>Compiles used authoring anchors into the original game's multi-root equipment format.</summary>
public sealed class EquipmentAnchorPlan
{
    public AnchorRigInfo Info {get;}
    public List<BoneRecord> NativeBones {get;}
    public List<AnchorBinding> AddedAnchors {get;}
    public int[] ParentIndices {get;}
    public IReadOnlyList<SklbLayerRecord> Layers {get;}
    private readonly bool multi;
    private readonly SkeletonDocument baseline;
    public bool OriginalIndicesPreserved=>baseline.Bones.All(b=>NativeBones.FindIndex(n=>n.Id==b.Id)==b.OriginalIndex);

    private EquipmentAnchorPlan(AnchorRigInfo info,SkeletonDocument baseline,List<BoneRecord> native,List<AnchorBinding> anchors,int[] parents,
        IReadOnlyList<SklbLayerRecord> layers,bool multi)
    {Info=info;this.baseline=baseline;NativeBones=native;AddedAnchors=anchors;ParentIndices=parents;Layers=layers;this.multi=multi;}

    public static AnchorRigInfo Describe(SklbContainer original,SkeletonDocument baseline,SkeletonDocument reference)
    {
        var race=Regex.Match(baseline.SourceGamePath,@"^chara/human/c(\d{4})/skeleton/top/t\d{4}/skl_c\1t\d{4}\.sklb$");
        if(!race.Success || reference.SourceGamePath!=$"chara/human/c{race.Groups[1].Value}/skeleton/base/b0001/skl_c{race.Groups[1].Value}b0001.sklb")
            throw new InvalidDataException("挂点必须来自同种族性别的原版人体骨架。");
        var header=original.Header.Span;
        if(original.Sha256!=baseline.SourceSha256 || original.VersionHigh!=0x3133 ||
            BinaryPrimitives.ReadInt32LittleEndian(header[8..])!=48 || BinaryPrimitives.ReadUInt32LittleEndian(header[20..])!=uint.Parse(race.Groups[1].Value))
            throw new InvalidDataException("无法识别该装备骨架的挂接描述。");
        if(baseline.MapperCount!=0 || baseline.ExtraVariantCount!=0 || baseline.LocalFrameCount!=0 || baseline.PartitionCount!=0 ||
            Enumerable.Range(0,4).Any(i=>BinaryPrimitives.ReadInt32LittleEndian(original.Header.Span[(24+4*i)..])!=-1))
            throw new InvalidDataException("本版不改写已有 mapper、分区或未知辅助对象。");
        if(SkeletonValidator.Validate(reference).Count!=0 || reference.Bones.Where((b,i)=>b.OriginalIndex!=i).Any())
            throw new InvalidDataException("人体参考索引或骨骼层级无效。");
        var byId=reference.Bones.ToDictionary(b=>b.Id);
        var cache=new Dictionary<Guid,Matrix4x4>();
        Matrix4x4 World(BoneRecord b)
        {
            if(cache.TryGetValue(b.Id,out var m))return m;
            m=Local(b);if(b.ParentId is{} p)m*=World(byId[p]);return cache[b.Id]=m;
        }
        var targets=reference.Bones.Select(b=>new AttachmentTarget(b.Name,b.OriginalIndex,AttachmentPose.From(World(b)))).ToList();
        var parents=original.VersionLow==0x3031
            ?Enumerable.Range(0,4).Select(i=>(int)BinaryPrimitives.ReadInt16LittleEndian(original.Header.Span[(40+2*i)..])).TakeWhile(i=>i>=0).ToArray()
            :new[]{(int)BinaryPrimitives.ReadInt16LittleEndian(header[16..])};
        var roots=baseline.Bones.Where(b=>b.ParentId is null).OrderBy(b=>b.OriginalIndex).ToArray();
        if(roots.Length!=parents.Length || roots.Length is 0 or >4 || roots.Where((b,i)=>b.OriginalIndex!=i).Any())
            throw new InvalidDataException("原版挂接根节点与描述不匹配，不能猜测重排。");
        var existing=new List<ExistingAnchor>();
        for(var i=0;i<roots.Length;i++)
        {
            if(parents[i]<0 || parents[i]>=targets.Count)throw new InvalidDataException("原版人体挂接索引越界。");
            if(!Near(Local(roots[i]),targets[parents[i]].Pose.Matrix,1e-4f))
                throw new InvalidDataException("原装备根姿态与原人体挂接点不匹配。");
            existing.Add(new(roots[i].Id,roots[i].Name,targets[parents[i]].BoneName,parents[i]));
        }
        return new(baseline.ProjectId,baseline.SourceGamePath,baseline.SourceSha256,reference.SourceGamePath,reference.SourceSha256,existing,targets);
    }

    public static EquipmentAnchorPlan Create(SklbContainer original,SkeletonDocument baseline,SkeletonDocument candidate,
        SkeletonDocument reference,SklbContainer referenceContainer)
    {
        if(candidate.AnchorSchemaVersion!=1 || candidate.AttachmentTargetBone is not null)
            throw new InvalidDataException("请使用新版完整挂点骨架返回项目。");
        var logical=SkeletonValidator.OrderedForAppend(candidate,baseline);
        var info=Describe(original,baseline,reference);
        if(referenceContainer.Sha256!=reference.SourceSha256)throw new InvalidDataException("人体挂点参考来源不一致。");
        var anchors=candidate.AnchorBindings;
        if(anchors.Select(a=>a.BoneId).Distinct().Count()!=anchors.Count || anchors.Select(a=>a.BoneName).Distinct().Count()!=anchors.Count)
            throw new InvalidDataException("挂点身份或目标重复。");
        if(info.ExistingAnchors.Count+anchors.Count>4)
            throw new InvalidDataException($"当前装备格式最多保留 4 个直接人体挂点，原装备已占用 {info.ExistingAnchors.Count} 个；请减少不同挂点，同一挂点可挂多条骨链。");
        var anchorIds=anchors.Select(a=>a.BoneId).ToHashSet();
        var byId=logical.ToDictionary(b=>b.Id);
        var canonical=new Dictionary<Guid,BoneRecord>();
        foreach(var a in anchors)
        {
            if(!byId.TryGetValue(a.BoneId,out var b) || b.OriginalIndex!=-1 || b.ParentId is not null || b.Name!=a.BoneName ||
                a.ReferenceBoneIndex<0 || a.ReferenceBoneIndex>=info.Targets.Count || info.Targets[a.ReferenceBoneIndex].BoneName!=a.BoneName)
                throw new InvalidDataException("返回的挂点身份、目标或根关系无效。");
            if(info.ExistingAnchors.Any(e=>e.BoneName==a.BoneName))throw new InvalidDataException("已有挂接点应复用原装备根骨。");
            if(!logical.Any(c=>c.ParentId==a.BoneId && !anchorIds.Contains(c.Id)))throw new InvalidDataException("未使用的挂点不应进入返回文件。");
            var p=info.Targets[a.ReferenceBoneIndex].Pose;
            if(!Near(Local(b),p.Matrix,1e-4f))throw new InvalidDataException("挂点静止姿态被修改，请恢复原版人体挂点。");
            canonical.Add(b.Id,b with{Translation=new(p.Translation),Rotation=new(p.Rotation[0],p.Rotation[1],p.Rotation[2],p.Rotation[3]),Scale=Vector3.One});
        }
        if(logical.Any(b=>b.OriginalIndex<0 && b.ParentId is null && !anchorIds.Contains(b.Id)))
            throw new InvalidDataException("新增骨链没有父级，请在 Blender 中指定服装原骨或人体挂点。");
        var sortedAnchors=anchors.OrderBy(a=>a.ReferenceBoneIndex).ToList();
        var roots=info.ExistingAnchors.Select(e=>byId[e.RootId]).Concat(sortedAnchors.Select(a=>canonical[a.BoneId])).ToList();
        var native=roots.Concat(baseline.Bones.Where(b=>b.ParentId is not null).OrderBy(b=>b.OriginalIndex).Select(b=>byId[b.Id])).ToList();
        var added=native.Select(b=>b.Id).ToHashSet();
        var pending=logical.Where(b=>!added.Contains(b.Id)).ToList();
        while(pending.Count>0)
        {
            var ready=pending.Where(b=>b.ParentId is{} p && added.Contains(p)).ToArray();
            if(ready.Length==0)throw new InvalidDataException("无法排列新增骨链。");
            foreach(var b in ready){native.Add(b);added.Add(b.Id);pending.Remove(b);}
        }
        var index=native.Select((b,i)=>(b.Id,i)).ToDictionary(x=>x.Id,x=>x.i);
        var sourceLayers=SklbLayerTable.Read(original,baseline.Bones.Count);
        var oldByIndex=baseline.Bones.OrderBy(b=>b.OriginalIndex).ToArray();
        var layers=sourceLayers.ToDictionary(l=>l.Id,l=>l.BoneIndices.Select(i=>index[oldByIndex[i].Id]).ToList());
        var bodyLayers=SklbLayerTable.Read(referenceContainer,reference.Bones.Count);
        foreach(var a in sortedAnchors)
        {
            var membership=bodyLayers.Where(l=>l.BoneIndices.Contains(a.ReferenceBoneIndex)).ToArray();
            if(membership.Length!=1)throw new InvalidDataException("人体挂点的骨骼层无法唯一确定。");
            var id=membership[0].Id;if(!layers.TryGetValue(id,out var members))layers.Add(id,members=[]);
            members.Add(index[a.BoneId]);
        }
        var authored=native.Where(b=>b.OriginalIndex<0 && !anchorIds.Contains(b.Id)).ToArray();
        if(authored.Length>0)
        {
            if(!layers.TryGetValue(25,out var clothing))throw new InvalidDataException("该模板缺少已验证的服装骨骼层 25。");
            clothing.AddRange(authored.Select(b=>index[b.Id]));
        }
        var records=layers.OrderBy(p=>p.Key).Select(p=>new SklbLayerRecord(p.Key,p.Value.Distinct().Order().ToArray())).ToArray();
        var connections=info.ExistingAnchors.Select(e=>e.ReferenceBoneIndex).Concat(sortedAnchors.Select(a=>a.ReferenceBoneIndex)).ToArray();
        return new(info,baseline,native,sortedAnchors,connections,records,original.VersionLow==0x3031 || anchors.Count>0);
    }

    public byte[] Build(SklbContainer original,ReadOnlySpan<byte> payload)
    {
        if(original.Sha256!=Info.SourceSha256)throw new InvalidDataException("原装备骨架已变化。");
        var bytes=SklbLayerTable.Rebuild(original,payload,Layers,NativeBones.Count);
        if(multi)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4),0x3031);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(16),0);
            for(var i=0;i<4;i++)BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(40+2*i),checked((short)(i<ParentIndices.Length?ParentIndices[i]:-1)));
        }
        var parsed=SklbContainer.Parse(bytes);
        _=SklbLayerTable.Read(parsed,NativeBones.Count);
        return bytes;
    }

    private static Matrix4x4 Local(BoneRecord b)=>Matrix4x4.CreateScale(b.Scale)*Matrix4x4.CreateFromQuaternion(b.Rotation)*Matrix4x4.CreateTranslation(b.Translation);
    private static bool Near(Matrix4x4 a,Matrix4x4 b,float epsilon)
    {
        float[] x=[a.M11,a.M12,a.M13,a.M14,a.M21,a.M22,a.M23,a.M24,a.M31,a.M32,a.M33,a.M34,a.M41,a.M42,a.M43,a.M44];
        float[] y=[b.M11,b.M12,b.M13,b.M14,b.M21,b.M22,b.M23,b.M24,b.M31,b.M32,b.M33,b.M34,b.M41,b.M42,b.M43,b.M44];
        return x.Zip(y).All(p=>float.IsFinite(p.First)&&float.IsFinite(p.Second)&&MathF.Abs(p.First-p.Second)<epsilon);
    }
}
