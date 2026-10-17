using Dalamud.Plugin;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace ClothingSkeleton.Plugin;

/// <summary>Uses the IPC signatures inspected from the installed CN Penumbra.Api assembly.</summary>
internal sealed class PenumbraPreview(IDalamudPluginInterface pluginInterface) : IDisposable
{
    private readonly string tag="ClothingSkeleton-"+Guid.NewGuid().ToString("N");
    private Guid activeCollection;
    private int activePriority;
    public bool Active => activeCollection!=Guid.Empty;

    public Dictionary<Guid,string> Collections() =>
        pluginInterface.GetIpcSubscriber<Dictionary<Guid,string>>("Penumbra.GetCollections.V5").InvokeFunc();

    public void Start(Guid collection,string gamePath,string candidatePath,int priority,string? additionalGamePath=null)
    {
        if(collection==Guid.Empty || !Collections().ContainsKey(collection)) throw new InvalidOperationException("Select an existing Penumbra collection first.");
        if(!Path.IsPathRooted(candidatePath) || !File.Exists(candidatePath)) throw new FileNotFoundException("Candidate skeleton is missing.");
        Stop();
        var paths=new Dictionary<string,string>{[gamePath]=Path.GetFullPath(candidatePath)};
        if(additionalGamePath is not null)paths[additionalGamePath]=Path.GetFullPath(candidatePath);
        var result=pluginInterface.GetIpcSubscriber<string,Guid,Dictionary<string,string>,string,int,int>("Penumbra.AddTemporaryMod.V5")
            .InvokeFunc(tag,collection,paths,string.Empty,priority);
        if(result!=0) throw new InvalidOperationException($"Penumbra rejected temporary paths: {result}");
        activeCollection=collection;
        activePriority=priority;
        try
        {
            foreach(var path in paths.Keys)
            {
                var resolved=pluginInterface.GetIpcSubscriber<Guid,string,(int,string?)>("Penumbra.ResolvePath").InvokeFunc(collection,path);
                if(resolved.Item1!=0 || !string.Equals(resolved.Item2,Path.GetFullPath(candidatePath),StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"�滻��δ��Ч���ѳ�������Ԥ���������ɫ���Ϻͳ�ͻ���ȼ���Penumbra code={resolved.Item1}��");
            }
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        if(activeCollection==Guid.Empty) return;
        var result=pluginInterface.GetIpcSubscriber<string,Guid,int,int>("Penumbra.RemoveTemporaryMod.V5").InvokeFunc(tag,activeCollection,activePriority);
        if(result is not (0 or 1)) throw new InvalidOperationException($"Penumbra could not remove this preview: {result}");
        activeCollection=Guid.Empty;
    }

    public void Dispose() => Stop();

    public string SelfTest(string gamePath,string candidatePath)
    {
        if(Active) throw new InvalidOperationException("Stop the current preview before running the isolated IPC test.");
        var created=CreateCollection();
        if(created.Item1!=0 || created.Item2==Guid.Empty) throw new InvalidOperationException($"Cannot create isolated test collection: {created.Item1}");
        try
        {
            // This collection is never assigned to any game object.
            var result=pluginInterface.GetIpcSubscriber<string,Guid,Dictionary<string,string>,string,int,int>("Penumbra.AddTemporaryMod.V5")
                .InvokeFunc(tag,created.Item2,new Dictionary<string,string>{[gamePath]=Path.GetFullPath(candidatePath)},string.Empty,0);
            if(result!=0) throw new InvalidOperationException($"Isolated path registration failed: {result}");
            activeCollection=created.Item2; activePriority=0;
            var resolve=pluginInterface.GetIpcSubscriber<Guid,string,(int,string?)>("Penumbra.ResolvePath");
            var applied=resolve.InvokeFunc(created.Item2,gamePath);
            // This installed Penumbra's resolver does not expose unassigned temporary
            // collections (CollectionMissing). Do not assign an actor just for a test.
            if(applied.Item1 is not (0 or 2 or 22)) // Success, CollectionMissing, CollectionInactive
                throw new InvalidOperationException($"Unexpected isolated resolve result: {applied.Item1}.");
            if(applied.Item1==0 && !string.Equals(applied.Item2,Path.GetFullPath(candidatePath),StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Isolated collection resolved to another path.");
            Stop();
            var repeated=pluginInterface.GetIpcSubscriber<string,Guid,int,int>("Penumbra.RemoveTemporaryMod.V5").InvokeFunc(tag,created.Item2,0);
            if(repeated!=1) throw new InvalidOperationException("Temporary registration removal was not confirmed."); // NothingChanged
            var removed=resolve.InvokeFunc(created.Item2,gamePath);
            if(string.Equals(removed.Item2,Path.GetFullPath(candidatePath),StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Temporary path was not removed.");
            return $"Penumbra isolated registration/removal passed; unassigned resolver code={applied.Item1}. No actor assignment or in-game path-resolution verification was performed.";
        }
        finally
        {
            Stop();
            var deleted=pluginInterface.GetIpcSubscriber<Guid,int>("Penumbra.DeleteTemporaryCollection.V5").InvokeFunc(created.Item2);
            if(deleted is not (0 or 1)) throw new InvalidOperationException($"Could not delete isolated test collection: {deleted}");
        }
    }

    private (int,Guid) CreateCollection()
    {
        // V6 returns the provider's enum in a tuple. Bind its actual runtime type
        // instead of bundling another copy of Penumbra.Api with this plugin.
        var penumbra=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="Penumbra")
            ?? throw new InvalidOperationException("Penumbra is not loaded.");
        var api=AssemblyLoadContext.GetLoadContext(penumbra)!.Assemblies.FirstOrDefault(a=>a.GetName().Name=="Penumbra.Api")
            ?? throw new InvalidOperationException("Installed Penumbra.Api is unavailable.");
        var enumType=api.GetType("Penumbra.Api.Enums.PenumbraApiEc",throwOnError:true)!;
        var result=(ITuple)typeof(PenumbraPreview).GetMethod(nameof(CreateCollectionTyped),BindingFlags.NonPublic|BindingFlags.Instance)!
            .MakeGenericMethod(enumType).Invoke(this,null)!;
        return (Convert.ToInt32(result[0]),(Guid)result[1]!);
    }

    private (TCode,Guid) CreateCollectionTyped<TCode>() =>
        pluginInterface.GetIpcSubscriber<string,string,(TCode,Guid)>("Penumbra.CreateTemporaryCollection.V6")
            .InvokeFunc("ClothingSkeleton","CSW unassigned selftest "+Guid.NewGuid().ToString("N"));
}
