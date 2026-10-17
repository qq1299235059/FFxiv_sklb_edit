using System.Buffers.Binary;
using System.Numerics;
using System.Text.RegularExpressions;

namespace ClothingSkeleton.Core;

public sealed record AttachmentPose(float[] Translation,float[] Rotation,float[] Scale)
{
    public static AttachmentPose From(Matrix4x4 matrix)
    {
        if(!Matrix4x4.Decompose(matrix,out var scale,out var rotation,out var position) ||
            Vector3.Distance(scale,Vector3.One)>1e-4f)
            throw new InvalidDataException("当前挂接仅支持单位缩放的人体骨骼。");
        rotation=Quaternion.Normalize(rotation);
        return new([position.X,position.Y,position.Z],[rotation.X,rotation.Y,rotation.Z,rotation.W],[1,1,1]);
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public Matrix4x4 Matrix => Matrix4x4.CreateScale(Scale[0],Scale[1],Scale[2])*
        Matrix4x4.CreateFromQuaternion(new(Rotation[0],Rotation[1],Rotation[2],Rotation[3]))*
        Matrix4x4.CreateTranslation(Translation[0],Translation[1],Translation[2]);
}

public sealed record AttachmentTarget(string BoneName,int BoneIndex,AttachmentPose Pose);
public sealed record EquipmentAttachmentInfo(Guid ProjectId,string SourceGamePath,string SourceSha256,string ReferenceGamePath,string ReferenceSha256,
    string OriginalParentBone,int OriginalParentIndex,Guid RootId,AttachmentPose SourceRootPose,List<AttachmentTarget> Targets,
    int SchemaVersion=1,bool Available=true,bool WholeSkeleton=true);

/// <summary>A single native SKLB connection moves the complete equipment partial skeleton.</summary>
public sealed class EquipmentAttachment
{
    public EquipmentAttachmentInfo Info {get;}
    public AttachmentTarget Target {get;}
    public bool Changed => Target.BoneIndex!=Info.OriginalParentIndex;
    private EquipmentAttachment(EquipmentAttachmentInfo info,AttachmentTarget target) {Info=info;Target=target;}

    public static EquipmentAttachmentInfo Describe(SklbContainer original,SkeletonDocument baseline,SkeletonDocument reference)
    {
        var race=Regex.Match(baseline.SourceGamePath,@"^chara/human/c(\d{4})/skeleton/top/t\d{4}/skl_c\1t\d{4}\.sklb$");
        if(!race.Success || reference.SourceGamePath!=$"chara/human/c{race.Groups[1].Value}/skeleton/base/b0001/skl_c{race.Groups[1].Value}b0001.sklb")
            throw new InvalidDataException("装备挂接必须使用同种族性别的原版基础骨架。");
        var header=original.Header.Span;
        if(original.Sha256!=baseline.SourceSha256 || original.VersionHigh!=0x3133 ||
            BinaryPrimitives.ReadInt32LittleEndian(header[8..])!=48 || header.Length<48)
            throw new InvalidDataException("当前人体挂接仅支持已识别的 1300 装备骨架描述。");
        if(BinaryPrimitives.ReadUInt32LittleEndian(header[20..])!=uint.Parse(race.Groups[1].Value))
            throw new InvalidDataException("装备骨架的种族编号与目标资源不一致。");
        if(Enumerable.Range(0,4).Any(i=>BinaryPrimitives.ReadInt32LittleEndian(original.Header.Span[(24+4*i)..])!=-1) ||
            baseline.MapperCount!=0 || baseline.ExtraVariantCount!=0 || baseline.LocalFrameCount!=0 || baseline.PartitionCount!=0)
            throw new InvalidDataException("当前挂接不改写已有 mapper、分区或其他辅助对象。");
        if(SkeletonValidator.Validate(reference).Count!=0 || reference.Bones.Where((b,i)=>b.OriginalIndex!=i).Any())
            throw new InvalidDataException("人体参考骨架的索引或层级无效。");
        var roots=baseline.Bones.Where(b=>b.ParentId is null).ToArray();
        if(roots.Length!=1 || roots[0].OriginalIndex!=0)
            throw new InvalidDataException("当前人体挂接要求装备骨架只有一个位于索引 0 的根骨。");
        var parent=BinaryPrimitives.ReadInt16LittleEndian(header[16..]);
        if(parent<0 || parent>=reference.Bones.Count)throw new InvalidDataException("原装备挂接索引超出人体骨架范围。");
        var byId=reference.Bones.ToDictionary(b=>b.Id);
        var cache=new Dictionary<Guid,Matrix4x4>();
        Matrix4x4 World(BoneRecord bone)
        {
            if(cache.TryGetValue(bone.Id,out var world))return world;
            world=Local(bone);
            if(bone.ParentId is{} id)world*=World(byId[id]);
            cache.Add(bone.Id,world);
            return world;
        }
        var sourcePose=AttachmentPose.From(Local(roots[0]));
        var parentPose=AttachmentPose.From(World(reference.Bones[parent]));
        if(Vector3.Distance(new(sourcePose.Translation),new Vector3(parentPose.Translation))>1e-4f ||
            1-MathF.Abs(Quaternion.Dot(Rotation(sourcePose),Rotation(parentPose)))>1e-5f)
            throw new InvalidDataException("装备根骨与原挂接点的静止姿态不匹配，不能自动改换挂接点。");
        var targets=reference.Bones.Select(b=>new AttachmentTarget(b.Name,b.OriginalIndex,AttachmentPose.From(World(b)))).ToList();
        return new(baseline.ProjectId,baseline.SourceGamePath,baseline.SourceSha256,reference.SourceGamePath,reference.SourceSha256,
            reference.Bones[parent].Name,parent,roots[0].Id,sourcePose,targets);
    }

    public static EquipmentAttachment Create(SklbContainer original,SkeletonDocument baseline,SkeletonDocument reference,string targetName)
    {
        var info=Describe(original,baseline,reference);
        var target=info.Targets.SingleOrDefault(t=>t.BoneName==targetName)
            ??throw new InvalidDataException("所选人体挂接骨不存在，请重新选择挂接点。");
        return new(info,target);
    }

    public List<BoneRecord> Materialize(IReadOnlyList<BoneRecord> ordered)
    {
        if(ordered.Count==0 || ordered[0].Id!=Info.RootId)throw new InvalidDataException("挂接计划与装备根骨不匹配。");
        var result=ordered.ToList();
        if(Changed)
        {
            var p=Target.Pose;
            result[0]=ordered[0] with {Translation=new(p.Translation),Rotation=Rotation(p),Scale=Vector3.One};
        }
        return result;
    }

    public byte[] ApplyWrapper(ReadOnlySpan<byte> bytes)
    {
        var container=SklbContainer.Parse(bytes);
        if(container.VersionHigh!=0x3133 || BinaryPrimitives.ReadInt16LittleEndian(container.Header.Span[16..])!=Info.OriginalParentIndex)
            throw new InvalidDataException("候选文件的原挂接信息发生变化。");
        var result=bytes.ToArray();
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(16),checked((short)Target.BoneIndex));
        return result;
    }

    private static Matrix4x4 Local(BoneRecord bone)=>Matrix4x4.CreateScale(bone.Scale)*
        Matrix4x4.CreateFromQuaternion(bone.Rotation)*Matrix4x4.CreateTranslation(bone.Translation);
    private static Quaternion Rotation(AttachmentPose p)=>new(p.Rotation[0],p.Rotation[1],p.Rotation[2],p.Rotation[3]);
}
