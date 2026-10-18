using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClothingSkeleton.Core;

internal static class BridgeChecks
{
    public static void Run(Action<bool,string> check,SkeletonDocument baseline,SkeletonDocument candidate,string directory)
    {
        var output=Path.Combine(directory,"wire-return","edited.gltf");
        using var server=new BlenderBridgeServer(output,0);
        using var client=new HttpClient{BaseAddress=new Uri($"http://127.0.0.1:{server.Port}"),Timeout=TimeSpan.FromSeconds(5)};
        var id=Guid.NewGuid();var token="";
        (HttpStatusCode,JsonElement) Post(string path,object value,bool auth=true,string? origin=null,string? overrideToken=null)
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,path){Content=new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value,BridgeExchange.Json))};
            request.Content.Headers.ContentType=new("application/json");
            request.Headers.Add("X-CSW-Protocol",BridgeExchange.Protocol.ToString());
            if(auth){request.Headers.Add("X-CSW-Client",id.ToString());request.Headers.Add("X-CSW-Token",overrideToken??token);}
            if(origin is not null)request.Headers.Add("Origin",origin);
            using var response=client.Send(request);
            using var json=JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return(response.StatusCode,json.RootElement.Clone());
        }
        using(var health=JsonDocument.Parse(client.GetStringAsync("/v1/health").GetAwaiter().GetResult()))
            check(server.Port>0 && health.RootElement.GetProperty("protocol").GetInt32()==BridgeExchange.Protocol && !health.RootElement.TryGetProperty("token",out _),"loopback bridge health exposes protocol without credentials or disk paths");
        check(Post("/v1/poll",new{},false).Item1==HttpStatusCode.Unauthorized,"unconnected clients cannot read a project");
        var connected=Post("/v1/connect",new{protocol=BridgeExchange.Protocol,clientId=id},false);
        token=connected.Item2.GetProperty("token").GetString()!;
        check(connected.Item1==HttpStatusCode.OK && token.Length==64,"Blender handshake establishes an ephemeral connection");
        check(Post("/v1/poll",new{},overrideToken:new string('0',64)).Item1==HttpStatusCode.Unauthorized,"a wrong connection token cannot poll the skeleton");
        check(Post("/v1/poll",new{},origin:"https://example.invalid").Item1==HttpStatusCode.Forbidden,"browser-origin requests cannot access the local bridge");
        check(Post("/v1/connect",new{protocol=BridgeExchange.Protocol,clientId=Guid.NewGuid()},false).Item1==HttpStatusCode.Conflict,"another Blender cannot silently take over an active connection");
        var baselineBytes=JsonSerializer.SerializeToUtf8Bytes(baseline,SkeletonDocument.JsonOptions);
        var project=new BridgeProject(baseline.ProjectId,new string('a',64),Guid.NewGuid(),new(){["skeleton.ffxiv.json"]=Convert.ToBase64String(baselineBytes)});
        server.Publish(project);
        var polled=Post("/v1/poll",new{});
        check(polled.Item2.GetProperty("project").GetProperty("projectId").GetGuid()==baseline.ProjectId,"published skeleton is delivered through the connected client");
        check(Post("/v1/ack",new{transferId=project.TransferId}).Item1==HttpStatusCode.OK && Post("/v1/poll",new{}).Item2.GetProperty("project").ValueKind==JsonValueKind.Null,"acknowledged transfers do not import the same skeleton on every poll");
        var fixture=Path.Combine(directory,"wire-fixture","edited.gltf");Directory.CreateDirectory(Path.GetDirectoryName(fixture)!);GltfExchange.Export(candidate,fixture);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture)!,"parts.json"),JsonSerializer.Serialize(new GarmentReturn(baseline.ProjectId,null,false,[]),BridgeExchange.Json));
        var files=BridgeExchange.ReturnNames.ToDictionary(n=>n,n=>Convert.ToBase64String(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(fixture)!,n))));
        var returned=new BridgeReturn(baseline.ProjectId,project.Revision,Guid.NewGuid(),files);
        check(Post("/v1/return",returned with{ProjectId=Guid.NewGuid()}).Item1==HttpStatusCode.Conflict && !File.Exists(output),"another project's return cannot overwrite the fixed return file");
        check(Post("/v1/return",returned with{Revision=new string('b',64)}).Item1==HttpStatusCode.Conflict,"stale skeleton revisions are rejected");
        server.PauseReturns(true);
        check(Post("/v1/return",returned).Item1==HttpStatusCode.Conflict && !File.Exists(output),"native conversion can pause incoming file replacements");
        server.PauseReturns(false);
        check(Post("/v1/return",returned).Item1==HttpStatusCode.OK && GltfExchange.Import(output,baseline).Bones.Count==candidate.Bones.Count,"network return passes core validation and reaches the fixed plugin return file");
        var good=File.ReadAllBytes(output);var written=File.GetLastWriteTimeUtc(output);
        check(Post("/v1/return",returned).Item2.GetProperty("duplicate").GetBoolean() && File.GetLastWriteTimeUtc(output)==written,"retrying the same return is idempotent and preserves the completed file");
        var tampered=new Dictionary<string,string>(files){["edited.gltf"]=Convert.ToBase64String("{}"u8)};
        check(Post("/v1/return",returned with{RequestId=Guid.NewGuid(),Files=tampered}).Item1==HttpStatusCode.BadRequest && File.ReadAllBytes(output).SequenceEqual(good),"invalid glTF over the port preserves the previous valid return");
        var unsafeFiles=new Dictionary<string,string>(files);unsafeFiles.Remove("edited.gltf");unsafeFiles.Add("../outside","");
        check(Post("/v1/return",returned with{RequestId=Guid.NewGuid(),Files=unsafeFiles}).Item1==HttpStatusCode.BadRequest && File.ReadAllBytes(output).SequenceEqual(good),"wire filenames cannot escape the fixed return directory");
        check(Post("/v1/disconnect",new{}).Item1==HttpStatusCode.OK && Post("/v1/poll",new{}).Item1==HttpStatusCode.Unauthorized,"disconnect invalidates the connection immediately");
        check(Post("/v1/connect",new{protocol=99,clientId=id},false).Item1==HttpStatusCode.Conflict,"incompatible bridge protocols require a matching plugin update");
    }
}
