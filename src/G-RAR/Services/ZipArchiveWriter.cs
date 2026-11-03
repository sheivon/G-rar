using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using GRAR.Helpers;

namespace GRAR.Services;

public sealed class ZipWriterOptions
{
    public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Optimal;

    /// <summary>Legacy PKWARE encryption password; null/empty means no encryption.</summary>
    public string? Password { get; set; }
}

/// <summary>
/// Minimal, dependency free ZIP writer: streaming, store/deflate, unix permissions and
/// optional legacy password protection. ZIP64 is deliberately not supported.
/// </summary>
public sealed class ZipArchiveWriter : IDisposable
{
    private const uint LocalSignature = 0x04034b50;
    private const uint CentralSignature = 0x02014b50;
    private const uint EndSignature = 0x06054b50;
    private const ushort VersionNeeded = 20;
    private const ushort FlagEncrypted = 0x0001;
    private const ushort FlagUtf8 = 0x0800;
    private const int LocalHeaderSize = 30;
    private const int CentralHeaderSize = 46;
    private const int MaxEntries = ushort.MaxValue;
    private const long MaxArchiveSize = 0xE0000000;
    private const ushort MethodStore = 0;
    private const ushort MethodDeflate = 8;

    private readonly Stream _stream;
    private readonly ZipWriterOptions _options;
    private readonly bool _leaveOpen;
    private readonly List<CentralEntry> _entries = new();
    private bool _disposed;

    public ZipArchiveWriter(Stream stream, ZipWriterOptions options, bool leaveOpen = false)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("Writing a ZIP archive requires a seekable stream.", nameof(stream));

        _stream = stream;
        _options = options;
        _leaveOpen = leaveOpen;
    }

    public int EntryCount => _entries.Count;

    public void AddDirectory(string key, DateTime? modified = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        key = PathUtil.NormalizeKey(key);
        if (key.Length == 0)
            return;
        if (!key.EndsWith('/'))
            key += "/";

        var (stamp, _) = WriteLocalHeader(key, modified ?? DateTime.Now, encrypted: false, method: MethodStore, checkByte: 0);
        FinishEntry(stamp, crc: 0, uncompressedSize: 0, isDirectory: true, unixMode: default);
    }

    public void AddFile(string key, Stream source, DateTime? modified = null, UnixFileMode? unixMode = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        key = PathUtil.NormalizeKey(key);
        if (key.Length == 0)
            throw new ArgumentException("Entry name must not be empty.", nameof(key));

        var encrypted = !string.IsNullOrEmpty(_options.Password);
        var useDeflate = _options.CompressionLevel != CompressionLevel.NoCompression;
        var method = useDeflate ? MethodDeflate : MethodStore;
        var block = new byte[81920];
        Stream? owned = null;

        try
        {
            // The 12 byte encryption header ends with the high byte of the CRC and the keystream
            // continues from that header straight into the payload, so the CRC must be known
            // before the header goes out: hash the input once up front.
            uint preCrc = 0;
            if (encrypted)
            {
                if (!source.CanSeek)
                {
                    var buffered = new MemoryStream();
                    source.CopyTo(buffered);
                    buffered.Position = 0;
                    owned = buffered;
                    source = buffered;
                }

                var prehash = new Crc32();
                int preRead;
                while ((preRead = source.Read(block, 0, block.Length)) > 0)
                    prehash.Update(block.AsSpan(0, preRead));

                source.Position = 0;
                preCrc = prehash.Value;
            }

            var (stamp, cipher) = WriteLocalHeader(key, modified ?? DateTime.Now, encrypted, method, (byte)(preCrc >> 24));

            var crc = encrypted ? null : new Crc32();
            long uncompressedSize = 0;
            Stream output = (Stream?)cipher ?? _stream;
            DeflateStream? deflate = useDeflate
                ? new DeflateStream(output, _options.CompressionLevel, leaveOpen: true)
                : null;

            try
            {
                int read;
                while ((read = source.Read(block, 0, block.Length)) > 0)
                {
                    crc?.Update(block.AsSpan(0, read));
                    uncompressedSize += read;

                    if (deflate is not null)
                        deflate.Write(block, 0, read);
                    else
                        output.Write(block, 0, read);

                    if (_stream.Position > MaxArchiveSize)
                        throw new InvalidOperationException("Archive exceeds the ZIP64 limit.");
                }

                deflate?.Dispose();
                deflate = null;
                output.Flush();
            }
            finally
            {
                deflate?.Dispose();
            }

            FinishEntry(stamp, crc?.Value ?? preCrc, uncompressedSize, isDirectory: false, unixMode: unixMode ?? DefaultFileMode(key));
        }
        finally
        {
            owned?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        WriteCentralDirectory();

        if (!_leaveOpen)
            _stream.Dispose();
    }

    private sealed record HeaderStamp(long HeaderOffset, byte[] Name, ushort Flags, ushort Method, ushort DosTime, ushort DosDate);

    private (HeaderStamp Stamp, ZipCryptoStream? Cipher) WriteLocalHeader(
        string key, DateTime modified, bool encrypted, ushort method, byte checkByte)
    {
        if (_entries.Count >= MaxEntries)
            throw new InvalidOperationException("A ZIP archive cannot contain more than 65535 entries.");

        var nameBytes = Encoding.UTF8.GetBytes(key);
        if (nameBytes.Length > ushort.MaxValue)
            throw new InvalidOperationException($"Entry name is too long: {key}");

        var headerOffset = _stream.Position;
        if (headerOffset > MaxArchiveSize)
            throw new InvalidOperationException("Archive exceeds the ZIP64 limit.");

        var (dosTime, dosDate) = ToDosDateTime(modified);
        var flags = (ushort)(FlagUtf8 | (encrypted ? FlagEncrypted : 0));

        Span<byte> header = stackalloc byte[LocalHeaderSize];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, LocalSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], VersionNeeded);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], flags);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], method);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], dosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], dosDate);
        BinaryPrimitives.WriteUInt16LittleEndian(header[26..], (ushort)nameBytes.Length);
        _stream.Write(header);
        _stream.Write(nameBytes);

        ZipCryptoStream? cipher = null;
        if (encrypted)
        {
            // One cipher instance covers the 12 byte header and the payload so the keystream
            // state carries over exactly the way every ZIP reader expects it.
            cipher = new ZipCryptoStream(_stream, _options.Password!);
            Span<byte> cryptoHeader = stackalloc byte[12];
            Random.Shared.NextBytes(cryptoHeader[..11]);
            cryptoHeader[11] = checkByte; // high byte of the CRC
            cipher.Write(cryptoHeader.ToArray());
            cipher.Flush();
        }

        return (new HeaderStamp(headerOffset, nameBytes, flags, method, dosTime, dosDate), cipher);
    }

    private void FinishEntry(HeaderStamp stamp, uint crc, long uncompressedSize, bool isDirectory, UnixFileMode unixMode)
    {
        var dataEnd = _stream.Position;
        var payloadStart = stamp.HeaderOffset + LocalHeaderSize + stamp.Name.Length;
        var compressedSize = dataEnd - payloadStart; // includes the 12 byte encryption header

        if (compressedSize > uint.MaxValue || uncompressedSize > uint.MaxValue)
            throw new InvalidOperationException("Entry is too large for the ZIP format (ZIP64 is not supported).");

        var resume = _stream.Position;

        Span<byte> patch = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(patch, crc);
        BinaryPrimitives.WriteUInt32LittleEndian(patch[4..], (uint)compressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(patch[8..], (uint)uncompressedSize);
        _stream.Position = stamp.HeaderOffset + 14;
        _stream.Write(patch);

        _stream.Position = resume;

        var dosAttributes = isDirectory ? (ushort)0x10 : (ushort)0x20;
        var host = OperatingSystem.IsWindows() ? (ushort)0 : (ushort)3;
        uint externalAttributes = host == 3
            ? ((uint)ToUnixMode(unixMode, isDirectory) << 16) | dosAttributes
            : dosAttributes;

        _entries.Add(new CentralEntry(
            stamp.Name,
            stamp.Flags,
            stamp.Method,
            stamp.DosTime,
            stamp.DosDate,
            crc,
            (uint)compressedSize,
            (uint)uncompressedSize,
            (uint)stamp.HeaderOffset,
            (ushort)((host << 8) | VersionNeeded),
            externalAttributes));
    }

    private void WriteCentralDirectory()
    {
        var directoryOffset = _stream.Position;
        if (directoryOffset > MaxArchiveSize)
            throw new InvalidOperationException("Archive exceeds the ZIP64 limit.");

        Span<byte> header = stackalloc byte[CentralHeaderSize];
        foreach (var entry in _entries)
        {
            header.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(header, CentralSignature);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], entry.VersionMadeBy);
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], VersionNeeded);
            BinaryPrimitives.WriteUInt16LittleEndian(header[8..], entry.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(header[10..], entry.Method);
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], entry.DosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(header[14..], entry.DosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..], entry.Crc);
            BinaryPrimitives.WriteUInt32LittleEndian(header[20..], entry.CompressedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header[24..], entry.UncompressedSize);
            BinaryPrimitives.WriteUInt16LittleEndian(header[28..], (ushort)entry.Name.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header[38..], entry.ExternalAttributes);
            BinaryPrimitives.WriteUInt32LittleEndian(header[42..], entry.LocalHeaderOffset);

            _stream.Write(header);
            _stream.Write(entry.Name);
        }

        var directorySize = _stream.Position - directoryOffset;

        Span<byte> end = stackalloc byte[22];
        end.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(end, EndSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], (ushort)_entries.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], (ushort)_entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(end[12..], (uint)directorySize);
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], (uint)directoryOffset);
        _stream.Write(end);
        _stream.Flush();
    }

    private static UnixFileMode DefaultFileMode(string key)
    {
        var extension = Path.GetExtension(key);
        if (extension.Equals(".sh", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".run", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bin", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".desktop", StringComparison.OrdinalIgnoreCase))
        {
            return UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                 | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                 | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        }

        return UnixFileMode.UserRead | UnixFileMode.UserWrite
             | UnixFileMode.GroupRead
             | UnixFileMode.OtherRead | UnixFileMode.OtherWrite;
    }

    private static int ToUnixMode(UnixFileMode mode, bool isDirectory)
    {
        if (isDirectory)
            return 0x41ED; // drwxr-xr-x

        var value = 0x8000; // regular file
        if ((mode & UnixFileMode.UserRead) != 0) value |= 0x100;
        if ((mode & UnixFileMode.UserWrite) != 0) value |= 0x080;
        if ((mode & UnixFileMode.UserExecute) != 0) value |= 0x040;
        if ((mode & UnixFileMode.GroupRead) != 0) value |= 0x020;
        if ((mode & UnixFileMode.GroupWrite) != 0) value |= 0x010;
        if ((mode & UnixFileMode.GroupExecute) != 0) value |= 0x008;
        if ((mode & UnixFileMode.OtherRead) != 0) value |= 0x004;
        if ((mode & UnixFileMode.OtherWrite) != 0) value |= 0x002;
        if ((mode & UnixFileMode.OtherExecute) != 0) value |= 0x001;
        return value;
    }

    private static (ushort Time, ushort Date) ToDosDateTime(DateTime value)
    {
        var year = Math.Clamp(value.Year, 1980, 2107);
        var time = (ushort)((value.Hour << 11) | (value.Minute << 5) | (value.Second / 2));
        var date = (ushort)(((year - 1980) << 9) | (value.Month << 5) | value.Day);
        return (time, date);
    }

    private sealed record CentralEntry(
        byte[] Name,
        ushort Flags,
        ushort Method,
        ushort DosTime,
        ushort DosDate,
        uint Crc,
        uint CompressedSize,
        uint UncompressedSize,
        uint LocalHeaderOffset,
        ushort VersionMadeBy,
        uint ExternalAttributes);
}
