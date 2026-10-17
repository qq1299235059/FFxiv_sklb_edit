using System.Numerics;
using System.Text.Json;

namespace ClothingSkeleton.Core;

public sealed record BoneRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public int OriginalIndex { get; init; } = -1;
    public Guid? ParentId { get; init; }
    public Vector3 Translation { get; init; }
    public Quaternion Rotation { get; init; } = Quaternion.Identity;
    public Vector3 Scale { get; init; } = Vector3.One;
    public float TranslationW { get; init; } = 1;
    public float ScaleW { get; init; } = 1;
    public byte LockTranslation { get; init; }
}

public sealed record SkeletonDocument
{
    public int SchemaVersion { get; init; } = 1;
    public Guid ProjectId { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required string SourceSha256 { get; init; }
    public string SourceGamePath { get; init; } = "";
    public string GameVersion { get; init; } = "2026.10.06.0000.0000";
    public List<BoneRecord> Bones { get; init; } = [];
    public int MapperCount { get; init; }
    public int ExtraVariantCount { get; init; }
    public int LocalFrameCount { get; init; }
    public int PartitionCount { get; init; }
    public string? AttachmentTargetBone {get;init;}
    public int AnchorSchemaVersion {get;init;}
    public List<AnchorBinding> AnchorBindings {get;init;}=[];
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    public static SkeletonDocument Load(string path) =>
        JsonSerializer.Deserialize<SkeletonDocument>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException("Empty skeleton document.");
}

public sealed record AnchorBinding(Guid BoneId,string BoneName,int ReferenceBoneIndex);

public sealed record ValidationIssue(string Code, string Message, Guid? BoneId = null);

public static class SkeletonValidator
{
    public static List<ValidationIssue> Validate(SkeletonDocument candidate, SkeletonDocument? baseline = null)
    {
        var issues = new List<ValidationIssue>();
        if(candidate.AttachmentTargetBone is{} target && (target.Length is 0 or >63 || target.Any(c=>c<0x21 || c>0x7e)))
            issues.Add(new("A001","Invalid equipment attachment bone name."));
        if (candidate.SchemaVersion != 1) issues.Add(new("P001", "Unsupported exchange schema."));
        if (candidate.Bones.Count is 0 or > short.MaxValue) issues.Add(new("B007", "Bone count is outside the supported range."));
        var byId = new Dictionary<Guid, BoneRecord>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bone in candidate.Bones)
        {
            if (bone.Id == Guid.Empty || !byId.TryAdd(bone.Id, bone)) issues.Add(new("B004", "Empty or duplicated bone identity.", bone.Id));
            if (string.IsNullOrWhiteSpace(bone.Name) || bone.Name.Length > 63 || bone.Name.Any(c => c < 0x21 || c > 0x7e) || !names.Add(bone.Name))
                issues.Add(new("B005", "Bone name must be unique, printable ASCII, and at most 63 characters.", bone.Id));
            if (!Finite(bone.Translation) || !Finite(bone.Scale) || !Finite(bone.Rotation) || !float.IsFinite(bone.TranslationW) || !float.IsFinite(bone.ScaleW))
                issues.Add(new("T001", "Bone transform contains a non-finite value.", bone.Id));
            if (!float.IsFinite(bone.Rotation.LengthSquared()) || MathF.Abs(bone.Rotation.LengthSquared() - 1) > 0.001f)
                issues.Add(new("T002", "Bone quaternion must have unit length.", bone.Id));
            if (bone.Scale.X <= 0 || bone.Scale.Y <= 0 || bone.Scale.Z <= 0)
                issues.Add(new("T003", "Zero or negative bone scale is unsupported.", bone.Id));
            if (bone.OriginalIndex < 0 && Vector3.Distance(bone.Scale, Vector3.One) > 1e-5f)
                issues.Add(new("T003", "New bones require unit scale in this version.", bone.Id));
        }
        var colors = new Dictionary<Guid, byte>();
        foreach (var start in byId.Keys)
        {
            var chain = new List<Guid>();
            Guid? current = start;
            while (current is { } id && byId.TryGetValue(id, out var bone))
            {
                if (colors.TryGetValue(id, out var color))
                {
                    if (color == 1) issues.Add(new("B002", "Cyclic bone hierarchy.", id));
                    break;
                }
                colors[id] = 1;
                chain.Add(id);
                if (bone.ParentId is { } parent && !byId.ContainsKey(parent))
                    issues.Add(new("B001", "Parent bone is missing from this skeleton.", bone.Id));
                current = bone.ParentId;
            }
            foreach (var id in chain) colors[id] = 2;
        }
        if (baseline is null) return issues;
        if (candidate.ProjectId != baseline.ProjectId || candidate.SourceSha256 != baseline.SourceSha256 || candidate.Name != baseline.Name)
            issues.Add(new("P001", "Candidate belongs to a different baseline or skeleton."));
        var originals = baseline.Bones.ToDictionary(b => b.Id);
        foreach (var original in baseline.Bones)
        {
            if (!byId.TryGetValue(original.Id, out var bone))
            {
                issues.Add(new("B003", $"Original bone '{original.Name}' is missing.", original.Id));
                continue;
            }
            if (bone.Name != original.Name || bone.OriginalIndex != original.OriginalIndex || bone.ParentId != original.ParentId || bone.LockTranslation != original.LockTranslation)
                issues.Add(new("B003", "Original bone identity, index, parent, or lock changed.", bone.Id));
            if (Vector3.Distance(bone.Translation, original.Translation) > 1e-5f || Vector3.Distance(bone.Scale, original.Scale) > 1e-5f ||
                1 - MathF.Abs(Quaternion.Dot(bone.Rotation, original.Rotation)) > 1e-6f || bone.TranslationW != original.TranslationW || bone.ScaleW != original.ScaleW)
                issues.Add(new("B003", "Original rest pose is protected in the append-only workflow.", bone.Id));
        }
        foreach (var bone in candidate.Bones)
            if (!originals.ContainsKey(bone.Id) && bone.OriginalIndex != -1)
                issues.Add(new("B006", "A new bone cannot claim an original index.", bone.Id));
        return issues;
    }

    public static List<BoneRecord> OrderedForAppend(SkeletonDocument candidate, SkeletonDocument baseline)
    {
        var errors = Validate(candidate, baseline);
        if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors.Select(e => $"{e.Code}: {e.Message}")));
        var byId = candidate.Bones.ToDictionary(b => b.Id);
        var ordered = baseline.Bones.OrderBy(b => b.OriginalIndex).Select(b => byId[b.Id]).ToList();
        var added = ordered.Select(b => b.Id).ToHashSet();
        var remaining = candidate.Bones.Where(b => !added.Contains(b.Id)).OrderBy(b => b.Id).ToList();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(b => b.ParentId is null || added.Contains(b.ParentId.Value)).ToList();
            if (ready.Count == 0) throw new InvalidDataException("Cannot order added bones.");
            foreach (var bone in ready) { ordered.Add(bone); added.Add(bone.Id); remaining.Remove(bone); }
        }
        return ordered;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool Finite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);
}
