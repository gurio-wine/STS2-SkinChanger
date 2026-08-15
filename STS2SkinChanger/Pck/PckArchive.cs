using System.Security.Cryptography;
using System.Text;

namespace STS2SkinChanger.Pck;

internal sealed class PckArchive : IDisposable
{
    private const uint HeaderMagic = 0x43504447;
    private const uint DirectoryEncrypted = 1;
    private const uint RelativeFileBase = 2;
    private const uint FileEncrypted = 1;
    private const uint FileRemoval = 2;

    private readonly FileStream _stream;
    private readonly Dictionary<string, PckEntry> _entries;

    private PckArchive(string path, FileStream stream, Dictionary<string, PckEntry> entries)
    {
        Path = path;
        _stream = stream;
        _entries = entries;
    }

    public string Path { get; }

    public IReadOnlyCollection<string> Paths => _entries.Keys;

    public static PckArchive Open(string path)
    {
        // 保持源包在整个游戏会话中稳定，避免 Steam Workshop 更新在切换过程中替换 PCK。
        var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var pckStart = stream.Position;
            if (reader.ReadUInt32() != HeaderMagic)
            {
                throw new InvalidDataException($"{path} 不是受支持的 Godot PCK 文件。");
            }

            var formatVersion = reader.ReadUInt32();
            if (formatVersion is < 2 or > 4)
            {
                throw new InvalidDataException($"{path} 使用不支持的 PCK 格式版本 {formatVersion}。");
            }

            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            var packFlags = reader.ReadUInt32();
            if ((packFlags & DirectoryEncrypted) != 0)
            {
                throw new InvalidDataException($"{path} 的 PCK 目录已加密，无法作为皮肤资源读取。");
            }

            var rawFileBase = reader.ReadUInt64();
            var fileBase = ((packFlags & RelativeFileBase) != 0 || formatVersion >= 3)
                ? checked((ulong)pckStart + rawFileBase)
                : rawFileBase;

            if (formatVersion >= 3)
            {
                var directoryOffset = checked((long)((ulong)pckStart + reader.ReadUInt64()));
                stream.Position = directoryOffset;
            }
            else
            {
                stream.Position += 64;
            }

            var fileCount = reader.ReadUInt32();
            const uint MaxFileCount = 2_000_000; // 防御性上限，正常导出远小于此值。
            if (fileCount > MaxFileCount)
            {
                throw new InvalidDataException($"{path} 的 PCK 文件数量异常：{fileCount}。");
            }

            var entries = new Dictionary<string, PckEntry>(checked((int)fileCount), StringComparer.OrdinalIgnoreCase);
            for (var i = 0U; i < fileCount; i++)
            {
                var pathLength = reader.ReadUInt32();
                if (pathLength > 1024 * 1024)
                {
                    throw new InvalidDataException($"{path} 的 PCK 路径长度异常。");
                }

                var rawPath = reader.ReadBytes(checked((int)pathLength));
                if (rawPath.Length != (int)pathLength)
                {
                    throw new InvalidDataException($"{path} 的 PCK 目录在读取路径时被截断。");
                }

                var resourcePath = Encoding.UTF8.GetString(rawPath).TrimEnd('\0');
                var offset = reader.ReadUInt64();
                var size = reader.ReadUInt64();
                var md5 = reader.ReadBytes(16);
                if (md5.Length != 16)
                {
                    throw new InvalidDataException($"{path} 的 PCK 目录在读取 {resourcePath} 校验和时被截断。");
                }

                var fileFlags = reader.ReadUInt32();
                if ((fileFlags & FileRemoval) != 0)
                {
                    entries.Remove(NormalizePath(resourcePath));
                    continue;
                }

                if ((fileFlags & FileEncrypted) != 0)
                {
                    throw new InvalidDataException($"{path} 中的资源 {resourcePath} 已加密。");
                }

                var absoluteOffset = checked(fileBase + offset);
                var fileEnd = checked(absoluteOffset + size);
                if (fileEnd > (ulong)stream.Length)
                {
                    throw new InvalidDataException($"{path} 中的资源 {resourcePath} 超出文件范围。");
                }

                entries[NormalizePath(resourcePath)] = new PckEntry(absoluteOffset, size, md5);
            }

            return new PckArchive(path, stream, entries);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public bool Contains(string resourcePath) => _entries.ContainsKey(NormalizePath(resourcePath));

    public byte[] ReadFile(string resourcePath)
    {
        var normalizedPath = NormalizePath(resourcePath);
        if (!_entries.TryGetValue(normalizedPath, out var entry))
        {
            throw new FileNotFoundException($"PCK 中不存在资源 {normalizedPath}。", normalizedPath);
        }

        if (entry.Size > int.MaxValue)
        {
            throw new InvalidDataException($"资源 {normalizedPath} 过大，无法载入内存。");
        }

        var bytes = new byte[checked((int)entry.Size)];
        lock (_stream)
        {
            _stream.Position = checked((long)entry.Offset);
            _stream.ReadExactly(bytes);
        }

        return bytes;
    }

    public ulong GetFileSize(string resourcePath) => _entries[NormalizePath(resourcePath)].Size;

    public byte[] GetFileMd5(string resourcePath) => _entries[NormalizePath(resourcePath)].Md5;

    public void CopyFileTo(string resourcePath, Stream destination)
    {
        var entry = _entries[NormalizePath(resourcePath)];
        lock (_stream)
        {
            _stream.Position = checked((long)entry.Offset);
            var remaining = entry.Size;
            var buffer = new byte[128 * 1024];
            while (remaining > 0)
            {
                var read = _stream.Read(buffer, 0, checked((int)Math.Min((ulong)buffer.Length, remaining)));
                if (read == 0)
                {
                    throw new EndOfStreamException($"读取 {resourcePath} 时意外到达 PCK 末尾。");
                }

                destination.Write(buffer, 0, read);
                remaining -= checked((ulong)read);
            }
        }
    }

    public void Dispose() => _stream.Dispose();

    public static void Write(string outputPath, IReadOnlyDictionary<string, byte[]> files)
    {
        var orderedFiles = files
            .Select(pair => OutputEntry.FromBytes(NormalizePath(pair.Key), pair.Value))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();

        WriteEntries(outputPath, orderedFiles);
    }

    public static void WriteFromArchives(
        string outputPath,
        IReadOnlyDictionary<string, (PckArchive Archive, string Path)> files)
    {
        var orderedFiles = files
            .Select(pair => OutputEntry.FromArchive(NormalizePath(pair.Key), pair.Value.Archive, pair.Value.Path))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();

        WriteEntries(outputPath, orderedFiles);
    }

    private static void WriteEntries(string outputPath, OutputEntry[] orderedFiles)
    {

        const int fixedHeaderSize = 100;
        var directorySize = orderedFiles.Sum(entry => 4 + Align4(Encoding.UTF8.GetByteCount(entry.Path) + 1) + 8 + 8 + 16 + 4);
        var fileBase = Align(fixedHeaderSize + directorySize, 16);
        var cursor = fileBase;
        foreach (var entry in orderedFiles)
        {
            entry.Offset = cursor - fileBase;
            cursor = Align(cursor + entry.Size, 16);
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);
        using var stream = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(HeaderMagic);
        writer.Write(2U);
        writer.Write(4U);
        writer.Write(5U);
        writer.Write(1U);
        writer.Write(RelativeFileBase);
        writer.Write(checked((ulong)fileBase));
        for (var i = 0; i < 16; i++)
        {
            writer.Write(0U);
        }

        writer.Write(checked((uint)orderedFiles.Length));
        foreach (var entry in orderedFiles)
        {
            var pathBytes = Encoding.UTF8.GetBytes(entry.Path);
            var paddedLength = Align4(pathBytes.Length + 1);
            writer.Write(checked((uint)paddedLength));
            writer.Write(pathBytes);
            writer.Write(new byte[paddedLength - pathBytes.Length]);
            writer.Write(checked((ulong)entry.Offset));
            writer.Write(checked((ulong)entry.Size));
            writer.Write(entry.Md5);
            writer.Write(0U);
        }

        PadTo(writer, fileBase);
        foreach (var entry in orderedFiles)
        {
            PadTo(writer, fileBase + entry.Offset);
            entry.CopyTo(writer.BaseStream);
        }
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[6..];
        }

        // 折叠多余斜杠（res:////a 视作 res://a），与 Godot 的路径简化行为保持一致。
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return "res://" + normalized.Trim('/');
    }

    private static int Align4(int value) => (value + 3) & ~3;

    private static long Align(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

    private static void PadTo(BinaryWriter writer, long position)
    {
        var remaining = position - writer.BaseStream.Position;
        if (remaining < 0)
        {
            throw new InvalidDataException("PCK 写入位置发生重叠。");
        }

        if (remaining > 0)
        {
            writer.Write(new byte[checked((int)remaining)]);
        }
    }

    private sealed record PckEntry(ulong Offset, ulong Size, byte[] Md5);

    private sealed class OutputEntry
    {
        private readonly byte[]? _bytes;
        private readonly PckArchive? _archive;
        private readonly string? _archivePath;

        private OutputEntry(string path, long size, byte[] md5, byte[]? bytes, PckArchive? archive, string? archivePath)
        {
            Path = path;
            Size = size;
            Md5 = md5;
            _bytes = bytes;
            _archive = archive;
            _archivePath = archivePath;
        }

        public string Path { get; }
        public long Size { get; }
        public byte[] Md5 { get; }
        public long Offset { get; set; }

        public static OutputEntry FromBytes(string path, byte[] bytes) =>
            new(path, bytes.LongLength, MD5.HashData(bytes), bytes, null, null);

        public static OutputEntry FromArchive(string path, PckArchive archive, string archivePath) =>
            new(path, checked((long)archive.GetFileSize(archivePath)), archive.GetFileMd5(archivePath), null, archive, archivePath);

        public void CopyTo(Stream destination)
        {
            if (_bytes != null)
            {
                destination.Write(_bytes);
                return;
            }

            _archive!.CopyFileTo(_archivePath!, destination);
        }
    }
}
