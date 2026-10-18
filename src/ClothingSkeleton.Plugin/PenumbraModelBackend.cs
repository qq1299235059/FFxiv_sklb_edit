using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using ClothingSkeleton.Core;

namespace ClothingSkeleton.Plugin;

/// <summary>Uses the installed Penumbra model codec; no foreign binaries are distributed.</summary>
internal sealed class PenumbraModelBackend(object? testLogger=null)
{
    private readonly Assembly penumbra=Find("Penumbra");
    private readonly Assembly gameData=Find("Penumbra.GameData");
    private readonly Assembly core=Find("SharpGLTF.Core");
    private readonly Assembly toolkit=Find("SharpGLTF.Toolkit");
    private static Assembly Find(string name)=>AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name==name)??throw new InvalidOperationException("模型功能需要启用兼容的 Penumbra。");
    private Type Type(string name)=>penumbra.GetType(name)??throw new InvalidOperationException("已安装的 Penumbra 模型接口不兼容，请更新工作台适配。");
    private object Notifier()
    {
        var logger=testLogger??Type("Penumbra.Penumbra").GetField("Log",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(null)??throw new InvalidOperationException("Penumbra 模型服务尚未就绪。");
        return Activator.CreateInstance(Type("Penumbra.Import.Models.IoNotifier"),[logger])!;
    }
    internal static object Field(object value,string name)=>value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public)?.GetValue(value)
        ??value.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.Public)?.GetValue(value)
        ??throw new InvalidDataException("模型接口字段不兼容："+name);
    private static void Set(object value,string name,object data)=>(value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public)??throw new InvalidDataException("模型接口字段不兼容："+name)).SetValue(value,data);
    private object Read(byte[] bytes)
    {
        var model=Activator.CreateInstance(gameData.GetType("Penumbra.GameData.Files.MdlFile",true)!,[bytes])!;
        if(!(bool)Field(model,"Valid"))throw new InvalidDataException("Penumbra 未能读取模型。");return model;
    }
    public string[] Materials(byte[] mdl)=>(string[])Field(Read(mdl),"Materials");

    public bool IsSkinMaterial(byte[] bytes)
    {
        var type=gameData.GetType("Penumbra.GameData.Files.MtrlFile",true)!;var material=Activator.CreateInstance(type,[bytes])!;
        var shader=(string)Field(Field(material,"ShaderPackage"),"Name");return shader.Contains("skin",StringComparison.OrdinalIgnoreCase);
    }
    public byte[]? Export(byte[] mdl,IEnumerable<SkeletonDocument> skeletons,string temporaryDirectory,HashSet<string>? includeMaterials=null)
    {
        var model=Read(mdl);var notifier=Notifier();
        var meshes=(Array)Field(model,"Meshes");var lods=(Array)Field(model,"Lods");var lod=lods.GetValue(0)!;
        var start=Convert.ToInt32(Field(lod,"MeshIndex"));var count=Convert.ToInt32(Field(lod,"MeshCount"));var materialNames=(string[])Field(model,"Materials");
        var indices=Enumerable.Range(start,count).Where(i=>includeMaterials is null || includeMaterials.Contains(materialNames[Convert.ToInt32(Field(meshes.GetValue(i)!,"MaterialIndex"))])).ToArray();
        if(indices.Length==0)return null;
        var filtered=Array.CreateInstance(meshes.GetType().GetElementType()!,indices.Length);
        for(var i=0;i<indices.Length;i++)filtered.SetValue(meshes.GetValue(indices[i]),i);
        Set(model,"Meshes",filtered);Set(model,"LodCount",(byte)1);
        foreach(var name in new[]{"MeshIndex","MeshCount"})
        {var field=lod.GetType().GetField(name)!;field.SetValue(lod,Convert.ChangeType(name=="MeshIndex"?0:indices.Length,field.FieldType));}
        lods.SetValue(lod,0);for(var i=1;i<lods.Length;i++)lods.SetValue(Activator.CreateInstance(lod.GetType()),i);
        var skeletonType=Type("Penumbra.Import.Models.Export.XivSkeleton");
        var boneType=skeletonType.GetNestedType("Bone")!;var transformType=skeletonType.GetNestedType("Transform")!;
        var source=skeletons.ToArray();
        var entries=source.SelectMany(s=>s.Bones.Select(b=>(Bone:b,Parent:s.Bones.FirstOrDefault(p=>p.Id==b.ParentId)?.Name))).GroupBy(p=>p.Bone.Name).Select(g=>g.First()).ToArray();
        var byName=entries.Select((e,i)=>(e.Bone.Name,i)).ToDictionary(p=>p.Name,p=>p.i);
        var all=Array.CreateInstance(skeletonType,1);var bones=Array.CreateInstance(boneType,entries.Length+1);
        // One identity scene root keeps all original additional-skeleton roots in
        // their model space while satisfying glTF's common-ancestor requirement.
        var sceneRoot=Activator.CreateInstance(boneType)!;var sceneTransform=Activator.CreateInstance(transformType)!;
        Set(sceneRoot,"Name","csw_model_root");Set(sceneRoot,"ParentIndex",-1);
        Set(sceneTransform,"Scale",System.Numerics.Vector3.One);Set(sceneTransform,"Rotation",System.Numerics.Quaternion.Identity);Set(sceneTransform,"Translation",System.Numerics.Vector3.Zero);
        Set(sceneRoot,"Transform",sceneTransform);bones.SetValue(sceneRoot,0);
        for(var i=0;i<entries.Length;i++)
        {
            var b=entries[i].Bone;var bone=Activator.CreateInstance(boneType)!;var transform=Activator.CreateInstance(transformType)!;
            Set(bone,"Name",b.Name);Set(bone,"ParentIndex",entries[i].Parent is{} parent?byName[parent]+1:0);
            Set(transform,"Translation",b.Translation);Set(transform,"Rotation",b.Rotation);Set(transform,"Scale",b.Scale);Set(bone,"Transform",transform);bones.SetValue(bone,i+1);
        }
        all.SetValue(Activator.CreateInstance(skeletonType,[bones]),0);
        var exporter=Type("Penumbra.Import.Models.Export.ModelExporter").GetMethod("Export",BindingFlags.Public|BindingFlags.Static)!;
        var parameters=exporter.GetParameters();var config=Activator.CreateInstance(parameters[0].ParameterType.GetElementType()!)!;
        var materials=Activator.CreateInstance(parameters[3].ParameterType)!;
        var exported=exporter.Invoke(null,[config,model,all,materials,notifier])??throw new InvalidDataException("模型导出失败。");
        var sceneType=toolkit.GetType("SharpGLTF.Scenes.SceneBuilder",true)!;
        var scene=Activator.CreateInstance(sceneType,["Reference model"])!;
        exported.GetType().GetMethod("AddToScene")!.Invoke(exported,[scene]);
        var gltf=sceneType.GetMethod("ToGltf2",System.Type.EmptyTypes)!.Invoke(scene,null)!;
        Directory.CreateDirectory(temporaryDirectory);var file=Path.Combine(temporaryDirectory,"reference.glb");
        gltf.GetType().GetMethod("SaveGLB",[typeof(string),core.GetType("SharpGLTF.Schema2.WriteSettings",true)!])!.Invoke(gltf,[file,null]);
        return PreserveMaterialSlots(File.ReadAllBytes(file),filtered,materialNames);
    }

    private static byte[] PreserveMaterialSlots(byte[] glb,Array sourceMeshes,string[] names)
    {
        var jsonLength=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(12));
        var root=JsonNode.Parse(glb.AsSpan(20,jsonLength))!.AsObject();
        root["materials"]=new JsonArray(names.Select(name=>(JsonNode)new JsonObject{["name"]=name,["pbrMetallicRoughness"]=new JsonObject{["metallicFactor"]=0,["roughnessFactor"]=1}}).ToArray());
        foreach(var mesh in root["meshes"]!.AsArray())
        {
            var name=mesh!["name"]?.GetValue<string>()??"";
            var match=System.Text.RegularExpressions.Regex.Match(name,@"[ _](\d+)[.-]");
            if(!match.Success || !int.TryParse(match.Groups[1].Value,out var index) || index>=sourceMeshes.Length)throw new InvalidDataException("模型材质分组与已安装的 Penumbra 不兼容。");
            var material=Convert.ToInt32(Field(sourceMeshes.GetValue(index)!,"MaterialIndex"));
            foreach(var primitive in mesh["primitives"]!.AsArray())primitive!["material"]=material;
        }
        var encoded=System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());var aligned=(encoded.Length+3)&~3;
        var tail=glb.AsSpan(20+jsonLength).ToArray();var result=new byte[20+aligned+tail.Length];
        glb.AsSpan(0,20).CopyTo(result);result.AsSpan(20,aligned).Fill(0x20);encoded.CopyTo(result,20);tail.CopyTo(result,20+aligned);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8),result.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12),aligned);return result;
    }

    public byte[] Import(byte[] gltf,byte[] targetMdl,IEnumerable<string> knownNames,string temporaryDirectory)
    {
        ModelExchange.CheckSelfContained(gltf);
        var json=JsonNode.Parse(gltf)!.AsObject();var donor=Read(targetMdl);var donorMaterials=(string[])Field(donor,"Materials");
        if(donorMaterials.Length==0)throw new InvalidDataException("目标装备没有可继承的材质路径。");
        var materials=json["materials"]?.AsArray()??throw new InvalidDataException("服装模型需要至少一个材质槽。");
        if(materials.Count>donorMaterials.Length)throw new InvalidDataException($"此部件最多沿用 {donorMaterials.Length} 个目标装备材质槽，请合并材质或选择其他模板。");
        for(var i=0;i<materials.Count;i++){var existing=materials[i]!["name"]?.GetValue<string>();var canonical=System.Text.RegularExpressions.Regex.Replace(existing??"",@"\.\d{3}$","");materials[i]!["name"]=donorMaterials.Contains(existing)?existing:donorMaterials.Contains(canonical)?canonical:donorMaterials[i];}
        // The codec groups nodes by a numeric mesh/submesh suffix and uses one
        // material per group. Normalize a copy so arbitrary Blender names and
        // multi-material meshes are imported without silently dropping geometry.
        var nodes=json["nodes"]!.AsArray();var meshes=json["meshes"]!.AsArray();var groups=new Dictionary<int,int>();
        var submeshes=new Dictionary<int,int>();
        foreach(var node in nodes.ToArray().OfType<JsonObject>())
        {
            if(node["mesh"] is null)continue;
            var mesh=meshes[node["mesh"]!.GetValue<int>()]!.AsObject();var children=node["children"]?.AsArray()??new JsonArray();
            if(node["children"] is null)node["children"]=children;
            foreach(var primitive in mesh["primitives"]!.AsArray())
            {
                var material=primitive!["material"]?.GetValue<int>()??0;
                if(!groups.ContainsKey(material))groups[material]=groups.Count;
                var group=groups[material];var sub=submeshes.GetValueOrDefault(group);submeshes[group]=sub+1;
                var single=mesh.DeepClone().AsObject();single["primitives"]=new JsonArray(primitive.DeepClone());
                var child=new JsonObject{["name"]=$"Clothing_{group}.{sub}",["mesh"]=meshes.Count,["skin"]=node["skin"]?.DeepClone()};
                if(node["weights"] is{} weights)child["weights"]=weights.DeepClone();
                meshes.Add(single);children.Add(nodes.Count);nodes.Add(child);
            }
            node.Remove("mesh");node.Remove("skin");node.Remove("weights");
        }
        Directory.CreateDirectory(temporaryDirectory);var file=Path.Combine(temporaryDirectory,"clothing.gltf");
        File.WriteAllText(file,json.ToJsonString());
        var rootType=core.GetType("SharpGLTF.Schema2.ModelRoot",true)!;
        var root=rootType.GetMethod("Load",[typeof(string),core.GetType("SharpGLTF.Schema2.ReadSettings",true)!])!.Invoke(null,[file,null])!;
        var model=Type("Penumbra.Import.Models.Import.ModelImporter").GetMethod("Import",BindingFlags.Public|BindingFlags.Static)!.Invoke(null,[root,Notifier()])!;
        if(!(bool)Field(model,"Valid"))throw new InvalidDataException("服装不能转换为有效 MDL。");
        Set(model,"ElementIds",Field(donor,"ElementIds"));
        var bytes=(byte[])model.GetType().GetMethod("Write",System.Type.EmptyTypes)!.Invoke(model,null)!;
        var report=ModelBoneCheck.Check(bytes,"<当前装备模型>",knownNames,[]);
        if(!report.BoneTablesValid || report.MeshCount==0)throw new InvalidDataException("服装模型引用了当前骨架中不存在的骨骼，请检查各部件绑定与骨名："+string.Join("、",report.MissingNames.Take(8)));
        return bytes;
    }
}
