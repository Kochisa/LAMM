using System.Buffers.Binary;
using System.Text;

namespace LocalAIModelManager.Core.Models;

/// <summary>
/// The GGUF header fields that decide how much VRAM a model needs.
/// Everything here is read from the file itself; nothing is assumed.
/// </summary>
public sealed record GgufMetadata
{
    public string? Architecture { get; init; }

    /// <summary>The model's trained context length (<c>*.context_length</c>).</summary>
    public int? ContextLength { get; init; }

    public int? BlockCount { get; init; }

    public int? HeadCount { get; init; }

    public int? HeadCountKv { get; init; }

    public int? EmbeddingLength { get; init; }

    public int? KeyLength { get; init; }

    public int? ValueLength { get; init; }

    public long FileSizeBytes { get; init; }

    public int? EffectiveHeadCountKv => HeadCountKv ?? HeadCount;

    public int? ResolvedKeyLength =>
        KeyLength ?? (EmbeddingLength is { } e && HeadCount is { } h && h > 0 ? e / h : null);

    public int? ResolvedValueLength => ValueLength ?? ResolvedKeyLength;

    /// <summary>
    /// Bytes the KV cache costs per token. This is the number that explains why a small
    /// model can fill a big card: llama.cpp reserves the whole thing up front.
    /// </summary>
    public long? KvBytesPerToken(int bytesPerElement = 2)
    {
        if (BlockCount is not { } layers ||
            EffectiveHeadCountKv is not { } kvHeads ||
            ResolvedKeyLength is not { } keyLength ||
            ResolvedValueLength is not { } valueLength ||
            layers <= 0 || kvHeads <= 0 || keyLength <= 0 || valueLength <= 0)
        {
            return null;
        }

        return (long)layers * kvHeads * (keyLength + valueLength) * bytesPerElement;
    }

    public bool IsUsable => BlockCount is > 0 && EffectiveHeadCountKv is > 0 && KvBytesPerToken() is not null;
}

/// <summary>
/// Minimal, tolerant GGUF metadata reader. It only walks the header key/value pairs
/// (which always sit at the start of the file) and seeks over large arrays such as the
/// tokenizer vocabulary, so reading the metadata of a 100 GB model costs a few
/// milliseconds. Any malformed input yields <c>null</c> instead of throwing.
/// </summary>
public static class GgufMetadataReader
{
    private const uint Magic = 0x46554747; // "GGUF" little endian
    private const int MaxKeyValuePairs = 200_000;
    private const long MaxStringLength = 512 * 1024 * 1024;

    private enum ValueType
    {
        UInt8 = 0,
        Int8 = 1,
        UInt16 = 2,
        Int16 = 3,
        UInt32 = 4,
        Int32 = 5,
        Float32 = 6,
        Bool = 7,
        String = 8,
        Array = 9,
        UInt64 = 10,
        Int64 = 11,
        Float64 = 12,
    }

    public static GgufMetadata? TryRead(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            if (stream.Length < 24 || reader.ReadUInt32() != Magic)
            {
                return null;
            }

            var version = reader.ReadUInt32();
            if (version is < 2 or > 3)
            {
                return null;
            }

            _ = reader.ReadUInt64(); // tensor count
            var kvCount = reader.ReadUInt64();
            if (kvCount > MaxKeyValuePairs)
            {
                return null;
            }

            string? architecture = null;
            int? contextLength = null;
            int? blockCount = null;
            int? headCount = null;
            int? headCountKv = null;
            int? embeddingLength = null;
            int? keyLength = null;
            int? valueLength = null;

            for (ulong i = 0; i < kvCount; i++)
            {
                var key = ReadString(reader);
                if (key is null)
                {
                    break;
                }

                var type = (ValueType)reader.ReadUInt32();

                switch (key)
                {
                    case "general.architecture":
                        architecture = ReadStringValue(reader, type);
                        continue;
                    case var k when k.EndsWith(".context_length", StringComparison.Ordinal):
                        contextLength = ReadIntValue(reader, type);
                        continue;
                    case var k when k.EndsWith(".block_count", StringComparison.Ordinal):
                        blockCount = ReadIntValue(reader, type);
                        continue;
                    case var k when k.EndsWith(".attention.head_count_kv", StringComparison.Ordinal):
                        headCountKv = ReadIntValue(reader, type);
                        continue;
                    case var k when k.EndsWith(".attention.head_count", StringComparison.Ordinal):
                        headCount = ReadIntValue(reader, type);
                        continue;
                    case var k when k.EndsWith(".attention.key_length", StringComparison.Ordinal):
                        keyLength = ReadIntValue(reader, type);
                        continue;
                    case var k when k.EndsWith(".attention.value_length", StringComparison.Ordinal):
                        valueLength = ReadIntValue(reader, type);
                        continue;
                    case var k when k.EndsWith(".embedding_length", StringComparison.Ordinal):
                        embeddingLength = ReadIntValue(reader, type);
                        continue;
                    default:
                        SkipValue(reader, type, depth: 0);
                        continue;
                }
            }

            return new GgufMetadata
            {
                Architecture = architecture,
                ContextLength = contextLength,
                BlockCount = blockCount,
                HeadCount = headCount,
                HeadCountKv = headCountKv,
                EmbeddingLength = embeddingLength,
                KeyLength = keyLength,
                ValueLength = valueLength,
                FileSizeBytes = stream.Length,
            };
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        if (length == 0)
        {
            return string.Empty;
        }

        if (length > MaxStringLength || length > (ulong)reader.BaseStream.Length)
        {
            return null;
        }

        var bytes = reader.ReadBytes((int)length);
        return bytes.Length == (int)length ? Encoding.UTF8.GetString(bytes) : null;
    }

    private static string? ReadStringValue(BinaryReader reader, ValueType type) =>
        type == ValueType.String ? ReadString(reader) : SkipAndReturnNull(reader, type);

    private static string? SkipAndReturnNull(BinaryReader reader, ValueType type)
    {
        SkipValue(reader, type, depth: 0);
        return null;
    }

    private static int? ReadIntValue(BinaryReader reader, ValueType type)
    {
        switch (type)
        {
            case ValueType.UInt8: return reader.ReadByte();
            case ValueType.Int8: return reader.ReadSByte();
            case ValueType.UInt16: return reader.ReadUInt16();
            case ValueType.Int16: return reader.ReadInt16();
            case ValueType.UInt32: return Clamp(reader.ReadUInt32());
            case ValueType.Int32: return reader.ReadInt32();
            case ValueType.UInt64: return Clamp(reader.ReadUInt64());
            case ValueType.Int64: return Clamp(reader.ReadInt64());
            case ValueType.Bool: return reader.ReadBoolean() ? 1 : 0;
            default:
                SkipValue(reader, type, depth: 0);
                return null;
        }
    }

    private static int? Clamp(ulong value) => value > int.MaxValue ? null : (int)value;

    private static int? Clamp(long value) => value is < int.MinValue or > int.MaxValue ? null : (int)value;

    private static void SkipValue(BinaryReader reader, ValueType type, int depth)
    {
        if (depth > 8)
        {
            throw new IOException("GGUF metadata nesting is too deep.");
        }

        switch (type)
        {
            case ValueType.UInt8:
            case ValueType.Int8:
            case ValueType.Bool:
                Seek(reader, 1);
                break;
            case ValueType.UInt16:
            case ValueType.Int16:
                Seek(reader, 2);
                break;
            case ValueType.UInt32:
            case ValueType.Int32:
            case ValueType.Float32:
                Seek(reader, 4);
                break;
            case ValueType.UInt64:
            case ValueType.Int64:
            case ValueType.Float64:
                Seek(reader, 8);
                break;
            case ValueType.String:
                var length = reader.ReadUInt64();
                if (length > MaxStringLength)
                {
                    throw new IOException("GGUF string is implausibly long.");
                }

                Seek(reader, (long)length);
                break;
            case ValueType.Array:
                var elementType = (ValueType)reader.ReadUInt32();
                var count = reader.ReadUInt64();
                if (count > 100_000_000)
                {
                    throw new IOException("GGUF array is implausibly long.");
                }

                // Strings are variable width, so they still have to be walked.
                if (elementType == ValueType.String)
                {
                    for (ulong i = 0; i < count; i++)
                    {
                        var itemLength = reader.ReadUInt64();
                        if (itemLength > MaxStringLength)
                        {
                            throw new IOException("GGUF array element is implausibly long.");
                        }

                        Seek(reader, (long)itemLength);
                    }
                }
                else
                {
                    Seek(reader, (long)count * ElementSize(elementType));
                }

                break;
            default:
                throw new IOException($"Unknown GGUF value type {(int)type}.");
        }
    }

    private static int ElementSize(ValueType type) => type switch
    {
        ValueType.UInt8 or ValueType.Int8 or ValueType.Bool => 1,
        ValueType.UInt16 or ValueType.Int16 => 2,
        ValueType.UInt32 or ValueType.Int32 or ValueType.Float32 => 4,
        ValueType.UInt64 or ValueType.Int64 or ValueType.Float64 => 8,
        _ => throw new IOException($"GGUF array element type {(int)type} has no fixed size."),
    };

    private static void Seek(BinaryReader reader, long count)
    {
        if (count < 0 || reader.BaseStream.Position + count > reader.BaseStream.Length)
        {
            throw new EndOfStreamException("GGUF metadata extends past the end of the file.");
        }

        reader.BaseStream.Seek(count, SeekOrigin.Current);
    }
}
