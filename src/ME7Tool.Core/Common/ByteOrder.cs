namespace ME7Tool.Core.Common;

/// <summary>Ordem de bytes de um valor multi-byte.</summary>
public enum Endianness
{
    /// <summary>Little-endian (lo-hi) — Intel / C167 / ME7.</summary>
    LoHi = 0,
    /// <summary>Big-endian (hi-lo) — Motorola / PowerPC / EDC16+.</summary>
    HiLo = 1,
}

/// <summary>Helpers para extrair inteiros de bytes respeitando a endianness.</summary>
public static class ByteOrder
{
    /// <summary>
    /// Lê um inteiro não-assinado de <paramref name="size"/> bytes (1,2,4) a partir de
    /// <paramref name="data"/>, na ordem indicada.
    /// </summary>
    public static ulong ReadUnsigned(ReadOnlySpan<byte> data, int size, Endianness endian)
    {
        if (data.Length < size)
            throw new ArgumentException($"Precisava de {size} bytes, só há {data.Length}.");

        ulong v = 0;
        if (endian == Endianness.LoHi)
        {
            for (int i = size - 1; i >= 0; i--) v = (v << 8) | data[i];
        }
        else // HiLo
        {
            for (int i = 0; i < size; i++) v = (v << 8) | data[i];
        }
        return v;
    }

    /// <summary>Estende o sinal de um valor de <paramref name="size"/> bytes.</summary>
    public static long SignExtend(ulong value, int size)
    {
        int bits = size * 8;
        ulong signBit = 1UL << (bits - 1);
        if ((value & signBit) != 0)
            return (long)(value | ~((1UL << bits) - 1));
        return (long)value;
    }

    /// <summary>Número de bits zero à direita (para alinhar um bitmask).</summary>
    public static int TrailingZeros(uint mask)
    {
        if (mask == 0) return 0;
        int n = 0;
        while ((mask & 1) == 0) { mask >>= 1; n++; }
        return n;
    }
}
