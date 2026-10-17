using System.Buffers.Binary;

namespace ClothingSkeleton.Core;

public sealed record SklbLayerRecord(int Id,int[] BoneIndices);

/// <summary>Checked hpla layer membership for the supported 1300 clothing wrapper.</summary>
public static class SklbLayerTable
{
    private const uint Magic=0x616c7068;

    public static IReadOnlyList<SklbLayerRecord> Read(SklbContainer container,int boneCount) => Decode(container,boneCount).Layers;

    private static (List<SklbLayerRecord> Layers,int Start,int End) Decode(SklbContainer container,int boneCount)
    {
        if(container.VersionHigh!=0x3133)throw new InvalidDataException("Layer-aware append currently requires SKLB 1300.");
        var bytes=container.Header.Span;
        var start=(BinaryPrimitives.ReadInt32LittleEndian(bytes[8..])+15)&~15;
        void Need(int offset,int size)
        {
            if(offset<0 || size<0 || (long)offset+size>container.HavokOffset)throw new InvalidDataException("SKLB layer data is out of bounds.");
        }
        Need(start,6);
        if(BinaryPrimitives.ReadUInt32LittleEndian(bytes[start..])!=Magic)throw new InvalidDataException("SKLB layer marker is missing.");
        var count=BinaryPrimitives.ReadInt16LittleEndian(bytes[(start+4)..]);
        if(count<0)throw new InvalidDataException("Negative SKLB layer count.");
        Need(start+6,count*2);
        var next=6+count*2;
        var layers=new List<SklbLayerRecord>();
        var ids=new HashSet<int>();
        for(var i=0;i<count;i++)
        {
            var offset=BinaryPrimitives.ReadInt16LittleEndian(bytes[(start+6+2*i)..]);
            if(offset!=next)throw new InvalidDataException("Non-contiguous or invalid SKLB layer offset.");
            Need(start+offset,6);
            var id=BinaryPrimitives.ReadInt32LittleEndian(bytes[(start+offset)..]);
            var bones=BinaryPrimitives.ReadInt16LittleEndian(bytes[(start+offset+4)..]);
            if(bones<0 || !ids.Add(id))throw new InvalidDataException("Invalid or duplicate SKLB layer.");
            Need(start+offset+6,bones*2);
            var indices=new int[bones];
            for(var j=0;j<bones;j++)
            {
                indices[j]=BinaryPrimitives.ReadInt16LittleEndian(bytes[(start+offset+6+2*j)..]);
                if(indices[j]<0 || indices[j]>=boneCount)throw new InvalidDataException("SKLB layer bone index is out of range.");
            }
            if(indices.Distinct().Count()!=indices.Length)throw new InvalidDataException("Duplicate bone index within a SKLB layer.");
            layers.Add(new(id,indices));
            next+=6+2*bones;
        }
        return (layers,start,start+next);
    }

    public static byte[] Append(SklbContainer container,ReadOnlySpan<byte> payload,SkeletonDocument baseline,SkeletonDocument candidate)
    {
        var ordered=SkeletonValidator.OrderedForAppend(candidate,baseline);
        if(ordered.Count==baseline.Bones.Count)return container.WithPayload(payload);
        var table=Decode(container,baseline.Bones.Count);
        var originalHeader=container.Header.Span;
        if(originalHeader[table.End..].IndexOfAnyExcept((byte)0)>=0)
            throw new InvalidDataException("Nonzero opaque layer padding cannot be resized safely.");
        var members=table.Layers.Select(l=>l.BoneIndices.ToList()).ToArray();
        var indices=ordered.Select((b,i)=>(b.Id,i)).ToDictionary(p=>p.Id,p=>p.i);
        for(var i=baseline.Bones.Count;i<ordered.Count;i++)
        {
            var parent=ordered[i].ParentId is{} id?indices[id]:-1;
            var parentLayers=Enumerable.Range(0,members.Length).Where(n=>members[n].Contains(parent)).ToArray();
            if(parent<0 || parent>=i || parentLayers.Length!=1)
                throw new InvalidDataException($"New bone {ordered[i].Name} requires a parent in exactly one existing SKLB layer.");
            members[parentLayers[0]].Add(i);
        }
        using var stream=new MemoryStream();
        using var writer=new BinaryWriter(stream);
        writer.Write(Magic);writer.Write(checked((short)members.Length));
        var cursor=6+2*members.Length;
        foreach(var bones in members){writer.Write(checked((short)cursor));cursor+=6+2*bones.Count;}
        for(var i=0;i<members.Length;i++)
        {
            writer.Write(table.Layers[i].Id);writer.Write(checked((short)members[i].Count));
            foreach(var index in members[i])writer.Write(checked((short)index));
        }
        writer.Flush();
        var newOffset=Math.Max(container.HavokOffset,(table.Start+(int)stream.Length+15)&~15);
        if(payload.Length<16 || (long)newOffset+payload.Length>SklbContainer.MaximumFileSize)throw new InvalidDataException("Invalid layered SKLB size.");
        var result=new byte[newOffset+payload.Length];
        originalHeader[..table.Start].CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12),newOffset);
        stream.ToArray().CopyTo(result,table.Start);
        payload.CopyTo(result.AsSpan(newOffset));
        _=Read(SklbContainer.Parse(result),ordered.Count);
        return result;
    }

    public static byte[] Rebuild(SklbContainer container,ReadOnlySpan<byte> payload,IReadOnlyList<SklbLayerRecord> layers,int boneCount)
    {
        var table=Decode(container,short.MaxValue);
        var header=container.Header.Span;
        if(header[table.End..].IndexOfAnyExcept((byte)0)>=0)throw new InvalidDataException("δ֪�ķ������䲻�ܰ�ȫ���š�");
        if(layers.Count>short.MaxValue || layers.Select(l=>l.Id).Distinct().Count()!=layers.Count ||
            layers.Any(l=>l.BoneIndices.Length>short.MaxValue || l.BoneIndices.Distinct().Count()!=l.BoneIndices.Length || l.BoneIndices.Any(i=>i<0 || i>=boneCount)))
            throw new InvalidDataException("���ź�Ĺ�����������Ч��");
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        writer.Write(Magic);writer.Write(checked((short)layers.Count));
        var cursor=6+2*layers.Count;
        foreach(var layer in layers){writer.Write(checked((short)cursor));cursor+=6+2*layer.BoneIndices.Length;}
        foreach(var layer in layers)
        {
            writer.Write(layer.Id);writer.Write(checked((short)layer.BoneIndices.Length));
            foreach(var index in layer.BoneIndices)writer.Write(checked((short)index));
        }
        writer.Flush();
        var offset=Math.Max(container.HavokOffset,(table.Start+(int)stream.Length+15)&~15);
        if(payload.Length<16 || (long)offset+payload.Length>SklbContainer.MaximumFileSize)throw new InvalidDataException("���ŹǼܴ�С��Ч��");
        var result=new byte[offset+payload.Length];header[..table.Start].CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12),offset);stream.ToArray().CopyTo(result,table.Start);payload.CopyTo(result.AsSpan(offset));
        return result;
    }
}
