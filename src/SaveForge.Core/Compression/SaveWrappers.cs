using System.IO.Compression;
using ICSharpCode.SharpZipLib.BZip2;
using K4os.Compression.LZ4.Streams;
using ZstdSharp;

namespace SaveForge.Core.Compression;

/// <summary>
/// 存档外层包裹（压缩 / 编码）。很多游戏的存档是"压缩后的 JSON/XML/NBT"，
/// 这一层负责先剥掉包裹，让格式适配器只面对明文结构。
/// </summary>
public interface ISaveWrapper
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>识别置信度 0-1。</summary>
    float Detect(ReadOnlySpan<byte> data, string? fileName);

    /// <summary>剥掉包裹，返回明文。</summary>
    byte[] Unwrap(byte[] data);

    /// <summary>重新加上包裹（保存时用）。</summary>
    byte[] Wrap(byte[] payload);
}

/// <summary>GZip 包裹。魔数 1F 8B，最常见。</summary>
public sealed class GzipWrapper : ISaveWrapper
{
    public string Id => "gzip";
    public string DisplayName => "GZip 压缩";

    public float Detect(ReadOnlySpan<byte> data, string? fileName)
    {
        if (data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B) return 0.95f;
        return fileName?.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) == true ? 0.5f : 0f;
    }

    public byte[] Unwrap(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    public byte[] Wrap(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(payload, 0, payload.Length);
        return output.ToArray();
    }
}

/// <summary>Zlib 包裹。头部通常以 78 开头（78 01 / 78 9C / 78 DA）。</summary>
public sealed class ZlibWrapper : ISaveWrapper
{
    public string Id => "zlib";
    public string DisplayName => "Zlib 压缩";

    public float Detect(ReadOnlySpan<byte> data, string? fileName)
    {
        if (data.Length >= 2 && data[0] == 0x78 && data[1] is 0x01 or 0x9C or 0xDA or 0x5E)
            return 0.85f;
        return 0f;
    }

    public byte[] Unwrap(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    public byte[] Wrap(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(payload, 0, payload.Length);
        return output.ToArray();
    }
}

/// <summary>Brotli 包裹。没有魔数，只能靠尝试解压并验证。</summary>
public sealed class BrotliWrapper : ISaveWrapper
{
    public string Id => "brotli";
    public string DisplayName => "Brotli 压缩";

    public float Detect(ReadOnlySpan<byte> data, string? fileName) =>
        fileName?.EndsWith(".br", StringComparison.OrdinalIgnoreCase) == true ? 0.8f : 0.25f;

    public byte[] Unwrap(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    public byte[] Wrap(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
            brotli.Write(payload, 0, payload.Length);
        return output.ToArray();
    }
}

/// <summary>Base64 编码包裹（含 Base64URL 变体）。</summary>
public sealed class Base64Wrapper : ISaveWrapper
{
    public string Id => "base64";
    public string DisplayName => "Base64 编码";

    public float Detect(ReadOnlySpan<byte> data, string? fileName)
    {
        if (data.Length < 64) return 0f;

        // 抽样：必须全是 base64 字符（或 URL 安全变体、换行）
        int sample = Math.Min(data.Length, 4096);
        for (int i = 0; i < sample; i++)
        {
            byte b = data[i];
            bool allowed = b is >= (byte)'A' and <= (byte)'Z'
                or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9'
                or (byte)'+' or (byte)'/' or (byte)'=' or (byte)'-' or (byte)'_'
                or (byte)'\r' or (byte)'\n';

            if (!allowed) return 0f;
        }

        return 0.45f;
    }

    public byte[] Unwrap(byte[] data)
    {
        string text = Encoding.ASCII.GetString(data).Replace("\r", "").Replace("\n", "");
        text = text.Replace('-', '+').Replace('_', '/');

        // 补齐 padding
        int pad = text.Length % 4;
        if (pad == 2) text += "==";
        else if (pad == 3) text += "=";
        else if (pad == 1) throw new InvalidDataException("Base64 长度非法");

        return Convert.FromBase64String(text);
    }

    public byte[] Wrap(byte[] payload) =>
        Encoding.ASCII.GetBytes(Convert.ToBase64String(payload));
}

/// <summary>Zstandard 压缩包裹。魔数 28 B5 2F FD（小端 0xFD2FB528）。</summary>
public sealed class ZstdWrapper : ISaveWrapper
{
    public string Id => "zstd";
    public string DisplayName => "Zstd 压缩";

    public float Detect(ReadOnlySpan<byte> data, string? fileName)
    {
        if (data.Length >= 4 && data[0] == 0x28 && data[1] == 0xB5 && data[2] == 0x2F && data[3] == 0xFD)
            return 0.95f;

        return fileName?.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) == true ? 0.5f : 0f;
    }

    public byte[] Unwrap(byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        using var zstd = new ZstdSharp.DecompressionStream(input);
        using var output = new MemoryStream();
        zstd.CopyTo(output);
        return output.ToArray();
    }

    public byte[] Wrap(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var zstd = new ZstdSharp.CompressionStream(output, 3, leaveOpen: true))
            zstd.Write(payload, 0, payload.Length);
        return output.ToArray();
    }
}

/// <summary>LZ4 帧格式包裹。魔数 04 22 4D 18。</summary>
public sealed class Lz4Wrapper : ISaveWrapper
{
    public string Id => "lz4";
    public string DisplayName => "LZ4 压缩";

    public float Detect(ReadOnlySpan<byte> data, string? fileName)
    {
        if (data.Length >= 4 && data[0] == 0x04 && data[1] == 0x22 && data[2] == 0x4D && data[3] == 0x18)
            return 0.95f;

        return fileName?.EndsWith(".lz4", StringComparison.OrdinalIgnoreCase) == true ? 0.5f : 0f;
    }

    public byte[] Unwrap(byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        using var lz4 = LZ4Stream.Decode(input);
        using var output = new MemoryStream();
        lz4.CopyTo(output);
        return output.ToArray();
    }

    public byte[] Wrap(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var lz4 = LZ4Stream.Encode(output, leaveOpen: true))
            lz4.Write(payload, 0, payload.Length);
        return output.ToArray();
    }
}

/// <summary>BZip2 压缩包裹。魔数 "BZ"（42 5A）。</summary>
public sealed class Bzip2Wrapper : ISaveWrapper
{
    public string Id => "bzip2";
    public string DisplayName => "BZip2 压缩";

    public float Detect(ReadOnlySpan<byte> data, string? fileName)
    {
        if (data.Length >= 3 && data[0] == (byte)'B' && data[1] == (byte)'Z' && data[2] == (byte)'h')
            return 0.95f;

        return fileName?.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase) == true ? 0.5f : 0f;
    }

    public byte[] Unwrap(byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        using var output = new MemoryStream();
        using (var bz = new BZip2InputStream(input) { IsStreamOwner = false })
            bz.CopyTo(output);
        return output.ToArray();
    }

    public byte[] Wrap(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var bz = new BZip2OutputStream(output, 9) { IsStreamOwner = false })
            bz.Write(payload, 0, payload.Length);
        return output.ToArray();
    }
}
