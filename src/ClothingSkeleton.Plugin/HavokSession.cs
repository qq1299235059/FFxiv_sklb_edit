using System.Numerics;
using System.Runtime.InteropServices;
using ClothingSkeleton.Core;
using FFXIVClientStructs.Havok.Animation;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Types;
using FFXIVClientStructs.Havok.Common.Base.System.IO.OStream;
using FFXIVClientStructs.Havok.Common.Serialize.Resource;
using FFXIVClientStructs.Havok.Common.Serialize.Util;

namespace ClothingSkeleton.Plugin;

/// <summary>Owns only a file-loaded Havok object graph, never a live character skeleton.</summary>
internal sealed unsafe class HavokSession : IDisposable
{
    private hkResource* resource;
    private hkRootLevelContainer* root;
    private hkaSkeleton* skeleton;
    public static bool BindingsAvailable =>
        hkSerializeUtil.MemberFunctionPointers.LoadFromBuffer != null &&
        hkSerializeUtil.MemberFunctionPointers.Save != null;

    internal static void EnsureSupportedGameBuild()
    {
        var gameExecutable=System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot identify the current game build.");
        var versionPath=Path.Combine(Path.GetDirectoryName(gameExecutable)!,"ffxivgame.ver");
        if(!File.Exists(versionPath) || File.ReadAllText(versionPath).Trim()!="2026.10.06.0000.0000")
            throw new InvalidOperationException("当前客户端版本尚未验证，请先更新插件适配；本开发版仅支持国服 2026.10.06.0000.0000。");
    }

    public HavokSession(string hkxPath)
    {
        EnsureSupportedGameBuild();
        if (!BindingsAvailable) throw new InvalidOperationException("Required Havok functions are not resolved by this Dalamud build.");
        var registry = hkBuiltinTypeRegistry.Instance();
        if (registry == null) throw new InvalidOperationException("Havok type registry is unavailable.");
        var options = new hkSerializeUtil.LoadOptions
        {
            ClassNameRegistry = registry->GetClassNameRegistry(),
            TypeInfoRegistry = registry->GetTypeInfoRegistry(),
            Flags = new() { Storage = (int)hkSerializeUtil.LoadOptionBits.FailIfVersioning },
        };
        var encodedPath = Marshal.StringToCoTaskMemUTF8(hkxPath);
        try { resource = hkSerializeUtil.LoadFromFile((byte*)encodedPath, null, &options); }
        finally { Marshal.FreeCoTaskMem(encodedPath); }
        if (resource == null) throw new InvalidDataException("Havok rejected the skeleton file.");
        try
        {
            root = (hkRootLevelContainer*)resource->GetContentsPointer("hkRootLevelContainer"u8, registry->GetTypeInfoRegistry());
            if (root == null) throw new InvalidDataException("HKX root container is missing.");
            var animation = (hkaAnimationContainer*)root->findObjectByType("hkaAnimationContainer"u8, null);
            if (animation == null || animation->Skeletons.Length != 1)
                throw new InvalidDataException("This version requires exactly one skeleton in the animation container.");
            skeleton = animation->Skeletons[0].ptr;
            if (skeleton == null || skeleton->Bones.Length is <= 0 or > short.MaxValue ||
                skeleton->Bones.Length != skeleton->ParentIndices.Length || skeleton->Bones.Length != skeleton->ReferencePose.Length)
                throw new InvalidDataException("Havok bone arrays are inconsistent.");
        }
        catch { Dispose(); throw; }
    }

    public SkeletonDocument Snapshot(string sourceHash, string gamePath)
    {
        var count = skeleton->Bones.Length;
        var identities = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        var bones = new List<BoneRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var parent = skeleton->ParentIndices[i];
            if (parent < -1 || parent >= count) throw new InvalidDataException("Parent index is out of bounds.");
            var pose = skeleton->ReferencePose[i];
            bones.Add(new BoneRecord
            {
                Id = identities[i], Name = skeleton->Bones[i].Name.String!, OriginalIndex = i,
                ParentId = parent < 0 ? null : identities[parent],
                Translation = new(pose.Translation.X, pose.Translation.Y, pose.Translation.Z),
                Rotation = new(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W),
                Scale = new(pose.Scale.X, pose.Scale.Y, pose.Scale.Z),
                TranslationW = pose.Translation.W, ScaleW = pose.Scale.W,
                LockTranslation = skeleton->Bones[i].LockTranslation,
            });
        }
        var mapperCount = 0;
        var otherCount = 0;
        for (var i = 0; i < root->NamedVariants.Length; i++)
        {
            var kind = root->NamedVariants[i].ClassName.String;
            if (kind == "hkaSkeletonMapper") mapperCount++;
            else if (kind != "hkaAnimationContainer") otherCount++;
        }
        return new SkeletonDocument
        {
            Name = skeleton->Name.String ?? "Skeleton", SourceSha256 = sourceHash, SourceGamePath = gamePath,
            Bones = bones, MapperCount = mapperCount, ExtraVariantCount = otherCount,
            LocalFrameCount = skeleton->LocalFrames.Length, PartitionCount = skeleton->Partitions.Length,
        };
    }

    public void Save(string path, bool xml = false)
    {
        var registry = hkBuiltinTypeRegistry.Instance();
        var klass = registry->GetClassNameRegistry()->GetClassByName("hkRootLevelContainer"u8);
        if (klass == null) throw new InvalidOperationException("Root container class is not registered.");
        hkOstream stream = default;
        hkResult result = default;
        stream.Ctor(path);
        try
        {
            if (stream.StreamWriter.ptr == null) throw new IOException("Havok output stream could not be opened.");
            var options = new hkSerializeUtil.SaveOptions
            {
                Flags = new() { Storage = xml ? (int)hkSerializeUtil.SaveOptionBits.TextFormat : 0 },
            };
            hkSerializeUtil.Save(&result, root, klass, stream.StreamWriter.ptr, options);
            if ((int)result.Result != 0) throw new IOException($"Havok save failed: {result.Result}");
        }
        finally { stream.Dtor(); }
        if (!File.Exists(path) || new FileInfo(path).Length < 16) throw new IOException("Havok did not produce a valid output file.");
    }

    public void Dispose()
    {
        if (resource == null) return;
        resource->RemoveReference();
        resource = null;
        root = null;
        skeleton = null;
    }
}
