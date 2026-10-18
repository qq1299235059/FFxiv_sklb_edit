using System.Reflection;
using System.Runtime.Loader;
using ClothingSkeleton.Core;
using ClothingSkeleton.Plugin;
try {
var libraries=new[]{Path.GetFullPath(args[0]),Path.GetFullPath(args[1])};
AssemblyLoadContext.Default.Resolving+=(_,name)=>{var file=libraries.Select(p=>Path.Combine(p,name.Name+".dll")).FirstOrDefault(File.Exists);return file is null?null:AssemblyLoadContext.Default.LoadFromAssemblyPath(file);};
foreach(var name in new[]{"Serilog","Luna","Penumbra.GameData","SharpGLTF.Core","SharpGLTF.Toolkit","Penumbra"}){var file=libraries.Select(p=>Path.Combine(p,name+".dll")).First(File.Exists);AssemblyLoadContext.Default.LoadFromAssemblyPath(file);}
var luna=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="Luna");
var logger=Activator.CreateInstance(luna.GetType("Luna.MainLogger",true)!,["CSW codec probe"])!;
var backend=new PenumbraModelBackend(logger);
var source=Path.GetFullPath(args[2]);var output=Path.GetFullPath(args[3]);Directory.CreateDirectory(output);
var documents=Directory.GetFiles(source,"*snapshot.json").Select(SkeletonDocument.Load).ToArray();
var donor=File.ReadAllBytes(args[4]);
var glb=backend.Export(donor,documents,output)!;File.WriteAllBytes(Path.Combine(output,"reference.glb"),glb);
if(args.Length>5){var mdl=backend.Import(File.ReadAllBytes(args[5]),donor,documents.SelectMany(d=>d.Bones.Select(b=>b.Name)),output);File.WriteAllBytes(Path.Combine(output,"returned.mdl"),mdl);Console.WriteLine($"MDL round trip: {mdl.Length} bytes");}
Console.WriteLine($"Model export: {glb.Length} bytes");

}catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}
