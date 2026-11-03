namespace GRAR.Helpers;

/// <summary>Standard CRC-32 (as used by ZIP, PNG, ...).</summary>
public sealed class Crc32
{
    private static readonly uint[] Table = CreateTable();

    private uint _crc = 0xFFFFFFFF;

    public void Update(ReadOnlySpan<byte> data)
    {
        var crc = _crc;
        foreach (var b in data)
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        _crc = crc;
    }

    public uint Value => _crc ^ 0xFFFFFFFF;

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = new Crc32();
        crc.Update(data);
        return crc.Value;
    }

    /// <summary>Raw table step used by the legacy PKWARE encryption key schedule.</summary>
    public static uint UpdateRaw(uint crc, byte value) => (crc >> 8) ^ Table[(crc ^ value) & 0xFF];

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var entry = i;
            for (var j = 0; j < 8; j++)
                entry = (entry & 1) != 0 ? 0xEDB88320 ^ (entry >> 1) : entry >> 1;
            table[i] = entry;
        }

        return table;
    }
}
