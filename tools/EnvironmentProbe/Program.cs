using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection;
using System.Runtime.Loader;
using ClothingSkeleton.Core;

if (args.Length == 0) throw new ArgumentException("Use inspect <dll> or extract <game-dir> <game-path> <output>.");
if (args[0] == "items")
{
    using var data = new Lumina.GameData(args[1]);
    var sheet = data.GetExcelSheet<Lumina.Excel.Sheets.Item>(Lumina.Data.Language.ChineseSimplified) ?? throw new InvalidDataException("Item sheet not found.");
    foreach (var item in sheet)
    {
        var model = Convert.ToUInt64(item.ModelMain);
        if ((model & 0xffff) == ulong.Parse(args[2])) Console.WriteLine($"{item.RowId} | {item.Name} | model={model:X16} | slot={item.EquipSlotCategory.RowId}");
    }
}
else if (args[0] == "gltf")
{
    GltfExchange.Export(SkeletonDocument.Load(args[1]), args[2]);
    Console.WriteLine("Exported skeleton glTF and sidecar.");
}
else if (args[0] == "validate-gltf")
{
    var baseline = SkeletonDocument.Load(args[1]);
    var candidate = GltfExchange.Import(args[2], baseline);
    candidate.Save(args[3]);
    Console.WriteLine($"Exchange validated: {baseline.Bones.Count} -> {candidate.Bones.Count} bones.");
}
else if (args[0] == "raw")
{
    using var data = new Lumina.GameData(args[1]);
    var resource = data.GetFile(args[2]) ?? throw new FileNotFoundException("Game resource not found", args[2]);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3]))!);
    File.WriteAllBytes(args[3], resource.Data);
    Console.WriteLine($"Extracted original SqPack resource: {args[2]} ({resource.Data.Length} bytes)");
}
else if (args[0] == "fields")
{
    var library = Path.GetFullPath(args[1]);
    AssemblyLoadContext.Default.Resolving += (_, name) =>
    {
        var path = Path.Combine(Path.GetDirectoryName(library)!, name.Name + ".dll");
        if(!File.Exists(path)) path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"AppData/Roaming/XIVLauncherCN/addon/Hooks/dev",name.Name+".dll");
        return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
    };
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(library);
    foreach (var name in args.Skip(2))
    {
        var type = assembly.GetType(name) ?? assembly.GetTypes().Single(t => t.Name == name);
        Console.WriteLine(type.FullName);
        Console.WriteLine($"  BASE {type.BaseType}");
        foreach(var constructor in type.GetConstructors()) Console.WriteLine($"  CONSTRUCTOR {constructor}");
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)) Console.WriteLine($"  {f.FieldType} {f.Name} {(f.IsLiteral?f.GetRawConstantValue():"")}");
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)) Console.WriteLine($"  PROPERTY {p.PropertyType} {p.Name}");
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            try { Console.WriteLine($"  {method}"); }
            catch(FileNotFoundException) { Console.WriteLine($"  {method.Name} (signature dependency unavailable)"); }
        }
    }
}
else if (args[0] == "find-top")
{
    using var data = new Lumina.GameData(args[1]);
    var count = 0;
    for (var id = 1; id <= 9999 && count < 4; id++)
    {
        var path = $"chara/human/c{args[2]}/skeleton/top/t{id:D4}/skl_c{args[2]}t{id:D4}.sklb";
        if (data.GetFile(path) is not { } resource) continue;
        var container = SklbContainer.Parse(resource.Data);
        Directory.CreateDirectory("samples/private");
        File.WriteAllBytes($"samples/private/top-c{args[2]}-t{id:D4}.sklb", resource.Data);
        Console.WriteLine($"{path} | {resource.Data.Length} bytes | {container.Sha256}");
        count++;
    }
    if (count == 0) throw new FileNotFoundException("No top skeleton found for this race.");
}
else if (args[0] == "scan-top")
{
    using var data = new Lumina.GameData(args[1]);
    var directory=Path.GetFullPath(args[3]);
    Directory.CreateDirectory(directory);
    for(var id=1;id<=9999;id++)
    {
        var gamePath=$"chara/human/c{args[2]}/skeleton/top/t{id:D4}/skl_c{args[2]}t{id:D4}.sklb";
        if(data.GetFile(gamePath) is not{} file)continue;
        try
        {
            var sklb=SklbContainer.Parse(file.Data);
            var bytes=file.Data;
            var ids=Enumerable.Range(0,4).Select(i=>System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24+i*4))).ToArray();
            var names=System.Text.Encoding.ASCII.GetString(bytes);
            var hands=names.Contains("j_te_") || names.Contains("j_ude_");
            Console.WriteLine($"t{id:D4} | bytes={bytes.Length} | connect={System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(16))} | mappers={string.Join(',',ids)} | hand={hands}");
            if(hands || ids.Any(x=>x>=0))File.WriteAllBytes(Path.Combine(directory,$"skl_c{args[2]}t{id:D4}.sklb"),bytes);
        }
        catch(InvalidDataException e){Console.WriteLine($"t{id:D4} | unsupported: {e.Message}");}
    }
}
else if(args[0]=="find-method")
{
    using var file=File.OpenRead(args[1]);using var pe=new PEReader(file);var metadata=pe.GetMetadataReader();
    foreach(var handle in metadata.MethodDefinitions)
    {
        var method=metadata.GetMethodDefinition(handle);var name=metadata.GetString(method.Name);
        if(!name.Contains(args[2],StringComparison.OrdinalIgnoreCase))continue;
        var type=metadata.GetTypeDefinition(method.GetDeclaringType());
        Console.WriteLine(metadata.GetString(type.Namespace)+"."+metadata.GetString(type.Name)+"."+name+"("+string.Join(",",method.GetParameters().Select(p=>metadata.GetString(metadata.GetParameter(p).Name)))+")");
    }
}
else if (args[0] == "types")
{
    using var file=File.OpenRead(args[1]);
    using var pe=new PEReader(file);
    var metadata=pe.GetMetadataReader();
    foreach(var handle in metadata.TypeDefinitions)
    {
        var definition=metadata.GetTypeDefinition(handle);
        var name=metadata.GetString(definition.Name);
        if(name.Contains(args[2],StringComparison.OrdinalIgnoreCase))Console.WriteLine(metadata.GetString(definition.Namespace)+"."+name);
    }
}
else if (args[0] == "inspect")
{
    using var file = File.OpenRead(args[1]);
    using var pe = new PEReader(file);
    var metadata = pe.GetMetadataReader();
    foreach (var handle in metadata.TypeDefinitions)
    {
        var definition = metadata.GetTypeDefinition(handle);
        var name = metadata.GetString(definition.Name);
        if (!new[] { "hkSerializeUtil", "hkaSkeleton", "hkaBone", "hkResource", "hkRootLevelContainer", "hkaAnimationContainer", "hkOstream", "LoadOptionBits", "SaveOptionBits", "LoadOptions", "SaveOptions", "MemberFunctionPointers", "SkeletonMapper" }.Any(n => name == n || name.Contains("SkeletonMapper"))) continue;
        if (name == "MemberFunctionPointers") continue;
        Console.WriteLine($"TYPE {metadata.GetString(definition.Namespace)}.{name}");
        foreach (var fieldHandle in definition.GetFields())
        {
            var field = metadata.GetFieldDefinition(fieldHandle);
            var constant = field.GetDefaultValue();
            var value = constant.IsNil ? "" : Convert.ToHexString(metadata.GetBlobBytes(metadata.GetConstant(constant).Value));
            Console.WriteLine($"  FIELD {metadata.GetString(field.Name)} {value}");
        }
        foreach (var methodHandle in definition.GetMethods())
        {
            var method = metadata.GetMethodDefinition(methodHandle);
            var parameters = method.GetParameters().Select(p => metadata.GetString(metadata.GetParameter(p).Name));
            Console.WriteLine($"  METHOD {metadata.GetString(method.Name)}({string.Join(",", parameters)})");
        }
    }
}
else if (args[0] == "extract")
{
    using var data = new Lumina.GameData(args[1]);
    var resource = data.GetFile(args[2]) ?? throw new FileNotFoundException("Game resource not found", args[2]);
    var bytes = resource.Data;
    var container = SklbContainer.Parse(bytes);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3]))!);
    File.WriteAllBytes(args[3], bytes);
    Console.WriteLine($"Extracted {bytes.Length} bytes, SKLB {container.VersionHigh:X4}:{container.VersionLow:X4}, HKX offset {container.HavokOffset}, SHA256 {container.Sha256}");
    Console.WriteLine($"HKX signature: {Convert.ToHexString(container.Payload.Span[..16])}");
}
else throw new ArgumentException("Unknown probe operation.");
