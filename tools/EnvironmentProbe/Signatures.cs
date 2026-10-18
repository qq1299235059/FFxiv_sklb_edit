using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal sealed class Signatures : ISignatureTypeProvider<string,object?>
{
    public static void Print(string path,string[] names)
    {
        using var file=File.OpenRead(path);using var pe=new PEReader(file);var reader=pe.GetMetadataReader();var provider=new Signatures();
        foreach(var handle in reader.TypeDefinitions)
        {
            var type=reader.GetTypeDefinition(handle);var name=provider.GetTypeFromDefinition(reader,handle,0);
            if(!names.Any(n=>name.Contains(n,StringComparison.OrdinalIgnoreCase)))continue;
            Console.WriteLine(name);
            foreach(var h in type.GetFields())
            {
                var f=reader.GetFieldDefinition(h);Console.WriteLine($"  FIELD {f.DecodeSignature(provider,null)} {reader.GetString(f.Name)}");
            }
            foreach(var h in type.GetMethods())
            {
                var m=reader.GetMethodDefinition(h);var s=m.DecodeSignature(provider,null);
                Console.WriteLine($"  {m.Attributes} {s.ReturnType} {reader.GetString(m.Name)}({string.Join(", ",s.ParameterTypes)})");
            }
        }
    }
    public string GetArrayType(string e,ArrayShape s)=>e+"[]";
    public string GetByReferenceType(string e)=>"ref "+e;
    public string GetFunctionPointerType(MethodSignature<string> s)=>"function";
    public string GetGenericInstantiation(string t,ImmutableArray<string> a)=>t+"<"+string.Join(',',a)+">";
    public string GetGenericMethodParameter(object? c,int i)=>"!!"+i;
    public string GetGenericTypeParameter(object? c,int i)=>"!"+i;
    public string GetModifiedType(string m,string t,bool r)=>t;
    public string GetPinnedType(string e)=>e;
    public string GetPointerType(string e)=>e+"*";
    public string GetPrimitiveType(PrimitiveTypeCode c)=>c.ToString();
    public string GetSZArrayType(string e)=>e+"[]";
    public string GetTypeFromDefinition(MetadataReader r,TypeDefinitionHandle h,byte k){var t=r.GetTypeDefinition(h);return t.GetDeclaringType().IsNil?r.GetString(t.Namespace)+"."+r.GetString(t.Name):GetTypeFromDefinition(r,t.GetDeclaringType(),0)+"+"+r.GetString(t.Name);}
    public string GetTypeFromReference(MetadataReader r,TypeReferenceHandle h,byte k){var t=r.GetTypeReference(h);return t.ResolutionScope.Kind==HandleKind.TypeReference?GetTypeFromReference(r,(TypeReferenceHandle)t.ResolutionScope,0)+"+"+r.GetString(t.Name):r.GetString(t.Namespace)+"."+r.GetString(t.Name);}
    public string GetTypeFromSpecification(MetadataReader r,object? c,TypeSpecificationHandle h,byte k)=>r.GetTypeSpecification(h).DecodeSignature(this,c);
}
