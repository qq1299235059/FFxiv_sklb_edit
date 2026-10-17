using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace ClothingSkeleton.Core;

public static class GltfExchange
{
    private static JsonArray Values(params float[] values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    public static void Export(SkeletonDocument skeleton, string path)
    {
        var errors = SkeletonValidator.Validate(skeleton);
        if (errors.Count != 0) throw new InvalidDataException(string.Join("; ", errors.Select(e => e.Message)));
        var bones = skeleton.Bones;
        var indices = bones.Select((b, i) => (b.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var worlds = new Dictionary<Guid, Matrix4x4>();
        Matrix4x4 World(BoneRecord bone)
        {
            if (worlds.TryGetValue(bone.Id, out var cached)) return cached;
            var local = Matrix4x4.CreateScale(bone.Scale) * Matrix4x4.CreateFromQuaternion(bone.Rotation) * Matrix4x4.CreateTranslation(bone.Translation);
            // System.Numerics uses row vectors. The serialized float order represents glTF's transposed column-vector matrix.
            return worlds[bone.Id] = bone.ParentId is { } p ? local * World(bones[indices[p]]) : local;
        }
        var nodes = new JsonArray();
        foreach (var bone in bones)
        {
            var children = bones.Where(b => b.ParentId == bone.Id).Select(b => (JsonNode?)JsonValue.Create(indices[b.Id])).ToArray();
            var node = new JsonObject
            {
                ["name"] = bone.Name,
                ["translation"] = Values(bone.Translation.X, bone.Translation.Y, bone.Translation.Z),
                ["rotation"] = Values(bone.Rotation.X, bone.Rotation.Y, bone.Rotation.Z, bone.Rotation.W),
                ["scale"] = Values(bone.Scale.X, bone.Scale.Y, bone.Scale.Z),
                ["extras"] = new JsonObject { ["cswBoneId"] = bone.Id.ToString(), ["cswOriginalIndex"] = bone.OriginalIndex },
            };
            if (children.Length != 0) node["children"] = new JsonArray(children);
            nodes.Add(node);
        }
        nodes.Add(new JsonObject { ["name"] = "CSW_Armature", ["children"] = new JsonArray(bones.Where(b => b.ParentId is null).Select(b => (JsonNode?)JsonValue.Create(indices[b.Id])).ToArray()) });
        nodes.Add(new JsonObject { ["name"] = "CSW_PROXY_DO_NOT_EXPORT_AS_CLOTHING", ["mesh"] = 0, ["skin"] = 0 });

        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        foreach (var v in new[] { 0f, 0, 0, .000001f, 0, 0, 0, .000001f, 0 }) writer.Write(v);
        for (var vertex = 0; vertex < 3; vertex++) for (var j = 0; j < 4; j++) writer.Write((ushort)0);
        for (var vertex = 0; vertex < 3; vertex++) { writer.Write(1f); writer.Write(0f); writer.Write(0f); writer.Write(0f); }
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2); writer.Write((ushort)0);
        var bindOffset = (int)bytes.Position;
        foreach (var bone in bones)
        {
            if (!Matrix4x4.Invert(World(bone), out var m)) throw new InvalidDataException("Singular bind matrix.");
            foreach (var value in new[] { m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44 }) writer.Write(value);
        }
        writer.Flush();
        var views = new JsonArray();
        void View(int offset, int length) => views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = length });
        View(0,36); View(36,24); View(60,48); View(108,6); View(bindOffset,bones.Count*64);
        var accessors = new JsonArray();
        JsonObject Accessor(int view, int component, int count, string type) => new() { ["bufferView"] = view, ["componentType"] = component, ["count"] = count, ["type"] = type };
        var positions = Accessor(0,5126,3,"VEC3"); positions["min"] = Values(0,0,0); positions["max"] = Values(.000001f,.000001f,0);
        accessors.Add(positions); accessors.Add(Accessor(1,5123,3,"VEC4")); accessors.Add(Accessor(2,5126,3,"VEC4")); accessors.Add(Accessor(3,5123,3,"SCALAR")); accessors.Add(Accessor(4,5126,bones.Count,"MAT4"));
        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "ClothingSkeleton 0.4" },
            ["extras"] = new JsonObject { ["cswProjectId"] = skeleton.ProjectId.ToString(), ["cswSourceSha256"] = skeleton.SourceSha256 },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(bones.Count,bones.Count+1) }),
            ["nodes"] = nodes,
            ["skins"] = new JsonArray(new JsonObject { ["name"] = skeleton.Name, ["skeleton"] = bones.Count, ["joints"] = new JsonArray(Enumerable.Range(0,bones.Count).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()), ["inverseBindMatrices"] = 4 }),
            ["meshes"] = new JsonArray(new JsonObject { ["primitives"] = new JsonArray(new JsonObject { ["attributes"] = new JsonObject { ["POSITION"] = 0, ["JOINTS_0"] = 1, ["WEIGHTS_0"] = 2 }, ["indices"] = 3 }) }),
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bytes.Length, ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes.ToArray()) }),
            ["bufferViews"] = views,
            ["accessors"] = accessors,
        };
        if(skeleton.AttachmentTargetBone is{} attachment)gltf["extras"]!["cswAttachmentBone"]=attachment;
        if(skeleton.AnchorSchemaVersion!=0)
        {
            gltf["extras"]!["cswAnchorSchema"]=skeleton.AnchorSchemaVersion;
            gltf["extras"]!["cswAnchorBindings"]=JsonSerializer.SerializeToNode(skeleton.AnchorBindings,SkeletonDocument.JsonOptions);
        }
        File.WriteAllText(path, gltf.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        skeleton.Save(Path.ChangeExtension(path, ".ffxiv.json"));
        File.WriteAllText(Path.ChangeExtension(path,".complete.json"),JsonSerializer.Serialize(new
        {
            projectId=skeleton.ProjectId,gltfSha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),boneCount=bones.Count,
        }));
    }

    public static SkeletonDocument Import(string path, SkeletonDocument baseline)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("Exchange file is too large.");
        var gltf = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException("Empty glTF.");
        var sidecar = JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(path, ".ffxiv.json")))?.AsObject() ?? throw new InvalidDataException("Missing sidecar.");
        var complete=JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(path,".complete.json"))) ?? throw new InvalidDataException("Export has no completion marker.");
        if(complete["projectId"]?.GetValue<Guid>()!=baseline.ProjectId || complete["gltfSha256"]?.GetValue<string>()!=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))))
            throw new InvalidDataException("Export was interrupted or changed after completion.");
        if (sidecar["projectId"]?.GetValue<Guid>() != baseline.ProjectId || sidecar["sourceSha256"]?.GetValue<string>() != baseline.SourceSha256)
            throw new InvalidDataException("Exchange belongs to a different project.");
        if (gltf["asset"]?["version"]?.GetValue<string>() != "2.0") throw new InvalidDataException("Only glTF 2.0 is supported.");
        var nodes = gltf["nodes"]?.AsArray() ?? throw new InvalidDataException("No glTF nodes.");
        var skins = gltf["skins"]?.AsArray() ?? throw new InvalidDataException("A bone skin is required; unweighted bones must not be omitted.");
        if (skins.Count != 1) throw new InvalidDataException("Select only the clothing armature for export.");
        var joints = skins[0]!["joints"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray();
        if (joints.Length == 0 || joints.Length > short.MaxValue || joints.Distinct().Count() != joints.Length || joints.Any(i => i < 0 || i >= nodes.Count))
            throw new InvalidDataException("Invalid skin joint list.");
        if(complete["boneCount"]?.GetValue<int>()!=joints.Length) throw new InvalidDataException("Completed bone count does not match glTF joints.");
        var nodeIds = new Dictionary<int, Guid>();
        var parents = new Dictionary<int,int>();
        foreach (var (node, index) in nodes.Select((n, i) => (n!, i)))
            if (node["children"] is JsonArray children)
                foreach (var child in children)
                {
                    var childIndex = child!.GetValue<int>();
                    if (childIndex < 0 || childIndex >= nodes.Count || !parents.TryAdd(childIndex,index)) throw new InvalidDataException("Invalid or multiply parented glTF node.");
                }
        foreach (var joint in joints)
        {
            if (!Guid.TryParse(nodes[joint]!["extras"]?["cswBoneId"]?.GetValue<string>(), out var id) || id == Guid.Empty)
                throw new InvalidDataException("Bone identity is missing. Export using the Clothing Skeleton Blender addon.");
            nodeIds.Add(joint,id);
        }
        var original = baseline.Bones.ToDictionary(b => b.Id);
        var bones = new List<BoneRecord>();
        foreach (var joint in joints)
        {
            var node = nodes[joint]!;
            var id = nodeIds[joint];
            var matrix = ReadMatrix(node);
            if (!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var position)) throw new InvalidDataException("Cannot decompose bone matrix.");
            var recomposed = Matrix4x4.CreateScale(scale)*Matrix4x4.CreateFromQuaternion(rotation)*Matrix4x4.CreateTranslation(position);
            if (!Near(matrix,recomposed)) throw new InvalidDataException("Sheared bone matrix is unsupported.");
            Guid? parentId = null;
            if (parents.TryGetValue(joint, out var parent))
            {
                if (nodeIds.TryGetValue(parent,out var mapped)) parentId = mapped;
                else
                {
                    var visited = new HashSet<int>();
                    for (int? ancestor = parent; ancestor is { } a; ancestor = parents.TryGetValue(a,out var p) ? p : null)
                    {
                        if (!visited.Add(a) || nodeIds.ContainsKey(a) || !Near(ReadMatrix(nodes[a]!),Matrix4x4.Identity))
                            throw new InvalidDataException("Non-identity or intermediary armature transforms must be fixed before import.");
                    }
                }
            }
            if (original.TryGetValue(id,out var old))
            {
                var sameRest = Vector3.Distance(position,old.Translation) < 1e-5f && Vector3.Distance(scale,old.Scale) < 1e-5f && 1-MathF.Abs(Quaternion.Dot(rotation,old.Rotation)) < 1e-6f;
                if (!sameRest) throw new InvalidDataException($"Protected rest pose changed: {old.Name}");
                bones.Add(old with { Name = node["name"]!.GetValue<string>(), ParentId = parentId });
            }
            else bones.Add(new BoneRecord { Id=id,Name=node["name"]!.GetValue<string>(),ParentId=parentId,Translation=position,Rotation=Quaternion.Normalize(rotation),Scale=scale });
        }
        var attachment=gltf["extras"]?["cswAttachmentBone"]?.GetValue<string>();
        var schema=gltf["extras"]?["cswAnchorSchema"]?.GetValue<int>()??0;
        var bindings=gltf["extras"]?["cswAnchorBindings"]?.Deserialize<List<AnchorBinding>>(SkeletonDocument.JsonOptions)??[];
        if(schema is not (0 or 1) || schema==0 && bindings.Count>0)throw new InvalidDataException("Unsupported attachment anchor exchange.");
        var candidate = baseline with { Bones = bones,AttachmentTargetBone=attachment,AnchorSchemaVersion=schema,AnchorBindings=bindings };
        var errors = SkeletonValidator.Validate(candidate,baseline);
        if (errors.Count != 0) throw new InvalidDataException(string.Join("\n", errors.Select(e => $"{e.Code}: {e.Message}")));
        return candidate;
    }

    private static Matrix4x4 ReadMatrix(JsonNode node)
    {
        if (node["matrix"] is JsonArray array)
        {
            if (array.Count != 16) throw new InvalidDataException("Expected 16 matrix values.");
            var v = array.Select(n => n!.GetValue<float>()).ToArray();
            return new(v[0],v[1],v[2],v[3],v[4],v[5],v[6],v[7],v[8],v[9],v[10],v[11],v[12],v[13],v[14],v[15]);
        }
        float[] Get(string name, float[] fallback) => node[name] is JsonArray a ? a.Select(n => n!.GetValue<float>()).ToArray() : fallback;
        var t=Get("translation",[0,0,0]); var q=Get("rotation",[0,0,0,1]); var s=Get("scale",[1,1,1]);
        if(t.Length!=3 || q.Length!=4 || s.Length!=3) throw new InvalidDataException("Invalid glTF transform component count.");
        var qLength=q.Sum(v=>v*v);
        if(!float.IsFinite(qLength) || MathF.Abs(qLength-1)>0.001f) throw new InvalidDataException("Invalid glTF quaternion.");
        return Matrix4x4.CreateScale(s[0],s[1],s[2])*Matrix4x4.CreateFromQuaternion(new(q[0],q[1],q[2],q[3]))*Matrix4x4.CreateTranslation(t[0],t[1],t[2]);
    }

    private static bool Near(Matrix4x4 a, Matrix4x4 b)
    {
        var x = new[] { a.M11,a.M12,a.M13,a.M14,a.M21,a.M22,a.M23,a.M24,a.M31,a.M32,a.M33,a.M34,a.M41,a.M42,a.M43,a.M44 };
        var y = new[] { b.M11,b.M12,b.M13,b.M14,b.M21,b.M22,b.M23,b.M24,b.M31,b.M32,b.M33,b.M34,b.M41,b.M42,b.M43,b.M44 };
        return x.Zip(y).All(p => float.IsFinite(p.First) && float.IsFinite(p.Second) && MathF.Abs(p.First-p.Second)<1e-5f);
    }
}
