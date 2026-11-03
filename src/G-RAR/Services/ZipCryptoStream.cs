using System.Text;

namespace GRAR.Services;

/// <summary>
/// Implements the legacy PKWARE "ZipCrypto" stream cipher used by password protected ZIP files.
/// Output only: plaintext bytes are scrambled as they are written.
/// </summary>
internal sealed class ZipCryptoStream : Stream
{
    private readonly Stream _inner;
    private uint _k0 = 0x12345678;
    private uint _k1 = 0x23456789;
    private uint _k2 = 0x34567890;

    public ZipCryptoStream(Stream inner, string password)
    {
        _inner = inner;
        foreach (var b in Encoding.UTF8.GetBytes(password))
            UpdateKeys(b);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var plain = buffer[offset + i];
            var cipher = (byte)(plain ^ DecryptByte());
            _inner.WriteByte(cipher);
            UpdateKeys(plain);
        }
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // The underlying stream stays open; only this wrapper is discarded.
        base.Dispose(disposing);
    }

    private void UpdateKeys(byte value)
    {
        _k0 = Helpers.Crc32.UpdateRaw(_k0, value);
        _k1 = (_k1 + (_k0 & 0xFF)) * 134775813u + 1u;
        _k2 = Helpers.Crc32.UpdateRaw(_k2, (byte)(_k1 >> 24));
    }

    private byte DecryptByte()
    {
        var temp = (_k2 | 2) & 0xFFFF;
        return (byte)((temp * (temp ^ 1)) >> 8);
    }
}
