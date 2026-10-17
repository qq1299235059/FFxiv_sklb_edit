using System.Reflection;

namespace ClothingSkeleton.Plugin;

internal sealed record ModelBoneReport(string GamePath,int BoneNameCount,int MeshCount,string[] MissingNames,string[] ReferencedNewNames)
{
    public bool BoneTablesValid => MissingNames.Length==0;
    public bool VertexWeightsVerified => false;
}

internal static class ModelBoneCheck
{
    public static ModelBoneReport Check(byte[] bytes,string gamePath,IEnumerable<string> knownNames,IEnumerable<string> newNames)
    {
        // Use the already loaded Penumbra reader, which supports the CN client's V6
        // model format. Do not ship another plugin's implementation or fall back to
        // the older Lumina decoder, which misreads variable-sized V6 bone tables.
        var assembly=AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name=="Penumbra.GameData")
            .OrderByDescending(a=>a.GetName().Version).FirstOrDefault(a=>a.GetType("Penumbra.GameData.Files.MdlFile") is not null)
            ?? throw new InvalidOperationException("请先启用 Penumbra，再检查模型骨骼表。");
        var type=assembly.GetType("Penumbra.GameData.Files.MdlFile",throwOnError:true)!;
        var model=Activator.CreateInstance(type,[bytes]) ?? throw new InvalidDataException("Could not read the original MDL.");
        if(!Field<bool>(model,"Valid")) throw new InvalidDataException("Penumbra rejected the original MDL.");
        var names=Field<string[]>(model,"Bones");
        var meshes=Field<Array>(model,"Meshes");
        var tables=Field<Array>(model,"BoneTables");
        var known=knownNames.ToHashSet(StringComparer.Ordinal);
        var used=new HashSet<string>(StringComparer.Ordinal);
        foreach(var mesh in meshes)
        {
            var tableIndex=Field<ushort>(mesh,"BoneTableIndex");
            if(tableIndex==ushort.MaxValue) continue;
            if(tableIndex>=tables.Length) throw new InvalidDataException("MDL mesh bone-table index is out of range.");
            var table=tables.GetValue(tableIndex)!;
            var count=Field<uint>(table,"BoneCount");
            var indices=Field<ushort[]>(table,"BoneIndex");
            if(count>256 || count>indices.Length) throw new InvalidDataException("MDL bone-table capacity is invalid.");
            foreach(var index in indices.Take(checked((int)count)))
            {
                if(index>=names.Length) throw new InvalidDataException("MDL bone-name index is out of range.");
                used.Add(names[index]);
            }
        }
        return new(gamePath,names.Length,meshes.Length,used.Where(n=>!known.Contains(n)).Order().ToArray(),newNames.Where(used.Contains).Order().ToArray());
    }

    private static T Field<T>(object value,string name) =>
        (value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public)?.GetValue(value)
            ?? value.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.Public)?.GetValue(value)) is T result
            ? result : throw new InvalidOperationException($"Installed Penumbra model API is incompatible: {name}.");
}
