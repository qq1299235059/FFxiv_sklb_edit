using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ClothingSkeleton.Core;

/// <summary>Preserves every wrapper byte. Editing wrapper references is not supported yet.</summary>
public sealed class SklbContainer
{
    public const int MaximumFileSize = 64 * 1024 * 1024;
    private readonly byte[] header;
    private readonly byte[] payload;
    public ushort VersionLow { get; }
    public ushort VersionHigh { get; }
    public int HavokOffset => header.Length;
    public string Sha256 { get; }
    public ReadOnlyMemory<byte> Header => header.ToArray();
    public ReadOnlyMemory<byte> Payload => payload.ToArray();

    private SklbContainer(byte[] source, int offset, ushort low, ushort high)
    {
        header = source[..offset];
        payload = source[offset..];
        VersionLow = low;
        VersionHigh = high;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(source));
    }

    public static SklbContainer Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < 16 || source.Length > MaximumFileSize)
            throw new InvalidDataException("SKLB size is outside the supported range.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(source) != 0x736B6C62)
            throw new InvalidDataException("Invalid SKLB magic.");
        var low = BinaryPrimitives.ReadUInt16LittleEndian(source[4..]);
        var high = BinaryPrimitives.ReadUInt16LittleEndian(source[6..]);
        if ((low != 0x3030 && !(low==0x3031 && high==0x3133)) || high is not (0x3132 or 0x3133))
            throw new InvalidDataException($"Unsupported SKLB version {high:X4}:{low:X4}.");
        var offset = high == 0x3132
            ? BinaryPrimitives.ReadUInt16LittleEndian(source[10..])
            : BinaryPrimitives.ReadInt32LittleEndian(source[12..]);
        var minimum = high == 0x3132 ? 16 : 24;
        if (offset < minimum || offset > source.Length - 16)
            throw new InvalidDataException("SKLB Havok offset is out of bounds.");
        if (high == 0x3133)
        {
            var headerSize = BinaryPrimitives.ReadInt32LittleEndian(source[8..]);
            if (headerSize < 24 || headerSize > offset || (headerSize - 24) % 4 != 0)
                throw new InvalidDataException("SKLB wrapper descriptor size is invalid.");
            if(low==0x3031)
            {
                if(headerSize!=48 || BinaryPrimitives.ReadInt16LittleEndian(source[16..])!=0)
                    throw new InvalidDataException("Unrecognized multi-attachment SKLB descriptor.");
                var terminated=false;var parents=0;
                for(var i=0;i<4;i++)
                {
                    var parent=BinaryPrimitives.ReadInt16LittleEndian(source[(40+2*i)..]);
                    if(parent==-1)terminated=true;
                    else if(parent<0 || terminated)throw new InvalidDataException("Invalid multi-attachment parent list.");
                    else parents++;
                }
                if(parents==0)throw new InvalidDataException("Multi-attachment SKLB has no parent connection.");
            }
        }
        return new SklbContainer(source.ToArray(), offset, low, high);
    }

    public byte[] WithPayload(ReadOnlySpan<byte> replacement)
    {
        if (replacement.Length < 16 || replacement.Length > MaximumFileSize - header.Length)
            throw new InvalidDataException("Replacement Havok payload size is invalid.");
        var result = new byte[checked(header.Length + replacement.Length)];
        header.CopyTo(result, 0);
        replacement.CopyTo(result.AsSpan(header.Length));
        return result;
    }

    public byte[] OriginalBytes() => WithPayload(payload);
}
