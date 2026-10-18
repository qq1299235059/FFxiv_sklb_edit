using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClothingSkeleton.Core;

/// <summary>A loopback-only, bounded HTTP connection. It never calls game APIs on its worker threads.</summary>
public sealed class BlenderBridgeServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop=new();
    private readonly SemaphoreSlim clients=new(4);
    private readonly object gate=new();
    private readonly string returnFile;
    private readonly string version;
    private readonly string token=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private readonly Guid instance=Guid.NewGuid();
    private Guid? clientId;
    private DateTime seen;
    private Guid? acknowledged;
    private BridgeProject? project;
    private SkeletonDocument? baseline;
    private bool paused;
    private bool disposed;
    private Guid? lastRequest;
    private string lastReturnHash="";
    private string message="等待 Blender 连接";
    private long acceptedReturns;
    public int Port {get;}

    public BlenderBridgeServer(string returnFile,int port=BridgeExchange.DefaultPort,string version="0.6.0")
    {
        if(port is <0 or >65535)throw new ArgumentOutOfRangeException(nameof(port));
        this.returnFile=returnFile;this.version=version;
        listener=new(IPAddress.Loopback,port);listener.Start(8);Port=((IPEndPoint)listener.LocalEndpoint).Port;
        _=Accept();
    }

    public void Publish(BridgeProject value)
    {
        var source=BridgeExchange.Baseline(value);
        lock(gate)
        {
            project=value;baseline=source;acknowledged=null;lastRequest=null;lastReturnHash="";
            message="骨架已准备，等待 Blender 接收";
        }
    }
    public void PauseReturns(bool value){lock(gate)paused=value;}
    public object Status {get {lock(gate)return new{listening=!disposed,port=Port,connected=Connected,projectId=project?.ProjectId,transferId=project?.TransferId,receivedByBlender=project is not null && acknowledged==project.TransferId,acceptedReturns,message};}}
    private bool Connected=>clientId is not null && DateTime.UtcNow-seen<TimeSpan.FromSeconds(10);

    private async Task Accept()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var socket=await listener.AcceptTcpClientAsync(stop.Token);
                if(!await clients.WaitAsync(0,stop.Token)){socket.Dispose();continue;}
                _=Serve(socket);
            }
        }
        catch(Exception e) when(e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private sealed record Request(string Method,string Path,Dictionary<string,string> Headers,byte[] Body);
    private sealed class Problem(int status,string message):Exception(message){public int Status=>status;}
    private static Problem Bad(string message)=>new(400,message);
    private async Task Serve(TcpClient socket)
    {
        using(socket)
        using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var request=await Read(socket.GetStream(),deadline.Token);
                var (status,result)=Handle(request);
                await Write(socket.GetStream(),status,result,deadline.Token);
            }
            catch(Problem e){try {await Write(socket.GetStream(),e.Status,new{error=e.Message},deadline.Token);}catch(Exception x) when(x is IOException or OperationCanceledException) { }}
            catch(Exception e) when(e is JsonException or FormatException or InvalidDataException or KeyNotFoundException or ArgumentException or InvalidOperationException)
            {try {await Write(socket.GetStream(),400,new{error="交换数据无效，请重新发送骨架或返回文件。"},deadline.Token);}catch(Exception x) when(x is IOException or OperationCanceledException) { }}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException)
            {try {await Write(socket.GetStream(),500,new{error="交换文件暂时无法保存，请确认游戏插件目录可写后重试。"},deadline.Token);}catch(Exception x) when(x is IOException or OperationCanceledException) { }}
            catch(Exception e) when(e is OperationCanceledException or ObjectDisposedException) { }
            finally {clients.Release();}
        }
    }

    private async Task<Request> Read(NetworkStream stream,CancellationToken cancel)
    {
        var buffer=new byte[16384];var used=0;var headerEnd=-1;
        while(headerEnd<0)
        {
            if(used==buffer.Length)throw new Problem(413,"请求头过大。");
            var read=await stream.ReadAsync(buffer.AsMemory(used),cancel);if(read==0)throw Bad("请求不完整。");used+=read;
            headerEnd=buffer.AsSpan(0,used).IndexOf("\r\n\r\n"u8);
        }
        var lines=Encoding.ASCII.GetString(buffer,0,headerEnd).Split("\r\n");
        var first=lines[0].Split(' ');
        if(first.Length!=3 || first[2]!="HTTP/1.1")throw Bad("不支持的请求格式。");
        var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var line in lines.Skip(1))
        {
            var colon=line.IndexOf(':');if(colon<=0 || !headers.TryAdd(line[..colon].Trim(),line[(colon+1)..].Trim()))throw Bad("请求头重复或无效。");
        }
        if(headers.ContainsKey("Origin") || headers.ContainsKey("Transfer-Encoding"))throw new Problem(403,"仅接受本机 Blender 插件连接。");
        if(!headers.TryGetValue("Host",out var host) || host!=$"127.0.0.1:{Port}")throw new Problem(403,"连接地址必须是本机回环地址。");
        var length=0;
        if(first[0]=="POST" && (!headers.TryGetValue("Content-Length",out var text) || !int.TryParse(text,out length) || length<0))throw Bad("请求缺少长度。");
        if(length>BridgeExchange.MaxBodyBytes)throw new Problem(413,"交换数据过大。");
        var body=new byte[length];var buffered=Math.Min(length,used-headerEnd-4);buffer.AsSpan(headerEnd+4,buffered).CopyTo(body);
        for(var offset=buffered;offset<length;)
        {
            var n=await stream.ReadAsync(body.AsMemory(offset),cancel);if(n==0)throw Bad("请求内容不完整。");offset+=n;
        }
        return new(first[0],first[1],headers,body);
    }

    private (int,object) Handle(Request request)
    {
        if(request.Method=="GET" && request.Path=="/v1/health")return(200,new{protocol=BridgeExchange.Protocol,version,port=Port});
        if(request.Method!="POST" || request.Headers.GetValueOrDefault("X-CSW-Protocol")!="1" || request.Headers.GetValueOrDefault("Content-Type","").Split(';')[0].Trim()!="application/json")throw new Problem(403,"请使用配套 Blender 插件连接。");
        if(request.Path=="/v1/connect")
        {
            using var json=JsonDocument.Parse(request.Body);
            if(json.RootElement.GetProperty("protocol").GetInt32()!=BridgeExchange.Protocol)throw new Problem(409,"两侧互通版本不兼容，请更新插件。");
            var id=json.RootElement.GetProperty("clientId").GetGuid();if(id==Guid.Empty)throw Bad("无效的客户端身份。");
            lock(gate)
            {
                if(Connected && clientId!=id)throw new Problem(409,"另一个 Blender 已连接，请先断开该连接。");
                if(clientId!=id)acknowledged=null;
                clientId=id;seen=DateTime.UtcNow;
                return(200,new{protocol=BridgeExchange.Protocol,version,instanceId=instance,token});
            }
        }
        lock(gate)
        {
            Authenticate(request);
            switch(request.Path)
            {
                case "/v1/poll":return(200,new{project=project is not null && acknowledged!=project.TransferId?project:null,message,acceptedReturns});
                case "/v1/ack":
                {
                    using var json=JsonDocument.Parse(request.Body);
                    if(project is null || json.RootElement.GetProperty("transferId").GetGuid()!=project.TransferId)throw new Problem(409,"骨架已更新，请接收最新项目。");
                    acknowledged=project.TransferId;message="Blender 已接收骨架，可以继续编辑";return(200,new{received=true});
                }
                case "/v1/disconnect":clientId=null;acknowledged=null;message="Blender 已断开，等待重新连接";return(200,new{disconnected=true});
                case "/v1/return":
                {
                    if(paused)throw new Problem(409,"游戏正在回导，请稍后重试。");
                    var packet=JsonSerializer.Deserialize<BridgeReturn>(request.Body,BridgeExchange.Json)??throw Bad("返回内容为空。");
                    if(project is null || baseline is null || packet.ProjectId!=project.ProjectId || packet.Revision!=project.Revision)throw new Problem(409,"当前游戏项目已经变化，请发送并编辑对应骨架。");
                    var hash=Convert.ToHexStringLower(SHA256.HashData(request.Body));
                    if(lastRequest==packet.RequestId)
                    {
                        if(lastReturnHash!=hash)throw Bad("重复的返回身份内容不一致。");
                        return(200,new{received=true,duplicate=true,requestId=packet.RequestId});
                    }
                    var count=BridgeExchange.PublishReturn(returnFile,baseline,packet);
                    lastRequest=packet.RequestId;lastReturnHash=hash;acceptedReturns++;
                    message=$"已收到 Blender 返回的 {count} 个骨骼节点，请在游戏回导检查";
                    return(200,new{received=true,requestId=packet.RequestId,boneCount=count});
                }
                default:throw new Problem(404,"未知的互通请求。");
            }
        }
    }

    private void Authenticate(Request request)
    {
        var provided=request.Headers.GetValueOrDefault("X-CSW-Token","");
        if(!Connected || !Guid.TryParse(request.Headers.GetValueOrDefault("X-CSW-Client"),out var id) || clientId!=id || provided.Length!=token.Length ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(provided),Encoding.ASCII.GetBytes(token)))throw new Problem(401,"连接已失效，请重新连接游戏插件。");
        seen=DateTime.UtcNow;
    }

    private static async Task Write(NetworkStream stream,int code,object value,CancellationToken cancel)
    {
        var body=JsonSerializer.SerializeToUtf8Bytes(value,BridgeExchange.Json);
        var header=Encoding.ASCII.GetBytes($"HTTP/1.1 {code} Result\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(header,cancel);await stream.WriteAsync(body,cancel);
    }

    public void Dispose()
    {
        if(disposed)return;disposed=true;stop.Cancel();listener.Stop();
        lock(gate){clientId=null;project=null;baseline=null;}
    }
}
