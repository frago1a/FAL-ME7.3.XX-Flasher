using static ME7Tool.Core.Common.Lang;
namespace ME7Tool.Core.Flashing;

/// <summary>
/// Cifra do download Fiat por OBD (ME7.3.1 Thesis/Stilo; o mesmo algoritmo está no programador da Alfa).
/// O programador da Fiat (copiado para a XRAM 0x380000) desencripta cada byte recebido no 36 antes de o
/// escrever na flash (rotina na RAM 0x382D58, inicializada no 34 por 0x382D3C com a chave em 0x38209A).
///
/// <para>Estado: <c>s</c> (1 byte), começa em <c>chave[0]</c>; contador de bytes <c>n</c>.</para>
/// <para>Por cada byte cifrado <c>c</c>:</para>
/// <code>
///   x     = c XOR chave[s mod L]            (L = comprimento da chave, 24 na Thesis)
///   plano = x rodado à DIREITA (s mod 8) bits (rotação de 8 bits)
///   s     = (s + c) mod 256                 (o estado avança com o byte CIFRADO)
///   n++;  se n == 0x4000: s = chave[0], n = 0   (recomeça de 16 em 16 KB)
/// </code>
/// Encriptar é o inverso: <c>c = (plano rodado à ESQUERDA (s mod 8)) XOR chave[s mod L]</c>, depois
/// <c>s += c</c>. Confirmado ao desencriptar o BO010109.BIN do Examiner: dá o software 1037368755 da Thesis.
/// </summary>
public sealed class FiatCipher
{
    /// <summary>Chave do programador da Fiat da Thesis/Stilo (RAM 0x38209A = flash 0x80A0E2, 24 bytes).</summary>
    public static readonly byte[] DefaultKey =
    {
        0x03, 0xA2, 0xC3, 0x4C, 0x45, 0xFE, 0x07, 0x82, 0x31, 0xE2, 0x23, 0xD4,
        0x25, 0x76, 0xE7, 0x08, 0xB1, 0xC2, 0xF3, 0x84, 0xA5, 0xD6, 0x97, 0x18,
    };

    /// <summary>Bytes até o estado voltar ao início (o programador conta 0x4000).</summary>
    public const int ResetInterval = 0x4000;

    private readonly byte[] _key;
    private byte _s;
    private int _n;

    public FiatCipher(byte[]? key = null)
    {
        _key = (key ?? DefaultKey).ToArray();
        if (_key.Length == 0) throw new ArgumentException(T("Chave vazia.", "Empty key."), nameof(key));
        Reset();
    }

    public IReadOnlyList<byte> Key => _key;

    /// <summary>Volta ao estado inicial (o programador faz isto em cada 34).</summary>
    public void Reset() { _s = _key[0]; _n = 0; }

    public byte Decrypt(byte c)
    {
        int r = _s % 8;
        byte x = (byte)(c ^ _key[_s % _key.Length]);
        byte p = (byte)((x >> r) | (x << (8 - r)));
        Advance(c);
        return p;
    }

    public byte Encrypt(byte p)
    {
        int r = _s % 8;
        byte rot = (byte)((p << r) | (p >> (8 - r)));
        byte c = (byte)(rot ^ _key[_s % _key.Length]);
        Advance(c);
        return c;
    }

    public void Decrypt(ReadOnlySpan<byte> src, Span<byte> dst) { for (int i = 0; i < src.Length; i++) dst[i] = Decrypt(src[i]); }
    public void Encrypt(ReadOnlySpan<byte> src, Span<byte> dst) { for (int i = 0; i < src.Length; i++) dst[i] = Encrypt(src[i]); }

    private void Advance(byte c)
    {
        _s = (byte)(_s + c);
        if (++_n >= ResetInterval) Reset();
    }

    /// <summary>Desencripta um ficheiro inteiro (ex.: o .BIN do Examiner), a partir do estado inicial.</summary>
    public static byte[] DecryptAll(ReadOnlySpan<byte> data, byte[]? key = null)
    {
        var o = new byte[data.Length];
        new FiatCipher(key).Decrypt(data, o);
        return o;
    }

    /// <summary>Encripta um ficheiro inteiro a partir do estado inicial (o que vai nos 36).</summary>
    public static byte[] EncryptAll(ReadOnlySpan<byte> data, byte[]? key = null)
    {
        var o = new byte[data.Length];
        new FiatCipher(key).Encrypt(data, o);
        return o;
    }
}

/// <summary>Flash completo Fiat por OBD: preparação da imagem, chave e checksum.</summary>
public static class FiatFullFlash
{
    /// <summary>Endereço/tamanho do download único que o programador aceita (34 80 00 00 01 08 00 00).</summary>
    public const uint Start = 0x800000;
    public const int Size = 0x80000;
    public const uint StopInclusive = Start + Size - 1; // 0x87FFFF

    /// <summary>dataFormatIdentifier exigido pelo programador (0x01 = dados cifrados).</summary>
    public const byte DataFormat = 0x01;

    /// <summary>Zona de backup que a ECU gere sozinha (cópia do sector 0x8000–0xFFFF).</summary>
    public const int BackupOffset = 0x38000, BackupLength = 0x8000;

    /// <summary>
    /// Imagem a enviar, igual à do Examiner: o ficheiro, com a zona de backup 0x38000–0x3FFFF em branco
    /// (FF) exceto o word 0x8000 no início e no fim de cada bloco de 8 KB — exatamente como no BO010109.
    /// </summary>
    public static byte[] Prepare(byte[] image)
    {
        if (image.Length != Size) throw new ArgumentException(T($"A imagem tem de ter {Size} bytes (512 KB).", $"The image must be {Size} bytes (512 KB)."));
        var p = (byte[])image.Clone();
        p.AsSpan(BackupOffset, BackupLength).Fill(0xFF);
        for (int b = BackupOffset; b < BackupOffset + BackupLength; b += 0x2000)
        {
            p[b] = 0x00; p[b + 1] = 0x80;               // início do bloco
            p[b + 0x1FFE] = 0x00; p[b + 0x1FFF] = 0x80; // fim do bloco
        }
        return p;
    }

    /// <summary>Checksum do 31 01 (2 bytes): metade de cima do CRC-32 da imagem preparada (BO010109: 0x9737).</summary>
    public static ushort Checksum(byte[] prepared) => (ushort)(Flasher.Crc32(prepared) >> 16);

    /// <summary>
    /// Lê a chave do programador da Fiat dentro da imagem: no handler do 34 está
    /// <c>mov r12,#chave; movbz r13,comprimento; calls 38h,…</c> (E6 FC kk kk C2 FD ll ll DA 38). Os endereços
    /// são da RAM (página 0xE0), que corresponde a flash 0x808048 + (endereço &amp; 0x3FFF). null se não encontrar.
    /// </summary>
    public static byte[]? FindKey(byte[] image, int programmerOffset = 0x8048, int programmerLength = 0x56D0)
    {
        if (image.Length < programmerOffset + programmerLength) return null;
        var area = image.AsSpan(programmerOffset, programmerLength);
        for (int i = 0; i + 10 <= area.Length; i++)
        {
            if (area[i] != 0xE6 || area[i + 1] != 0xFC || area[i + 4] != 0xC2 || area[i + 5] != 0xFD ||
                area[i + 8] != 0xDA || area[i + 9] != 0x38) continue;
            int keyAddr = area[i + 2] | area[i + 3] << 8, lenAddr = area[i + 6] | area[i + 7] << 8;
            if ((keyAddr & 0xC000) != 0x8000 || (lenAddr & 0xC000) != 0x8000) continue; // DPP2 = página 0xE0
            int keyOff = programmerOffset + (keyAddr & 0x3FFF), lenOff = programmerOffset + (lenAddr & 0x3FFF);
            if (lenOff >= image.Length) continue;
            int len = image[lenOff];
            if (len is < 4 or > 64 || keyOff + len > image.Length) continue;
            return image.AsSpan(keyOff, len).ToArray();
        }
        return null;
    }

    /// <summary>Texto de identificação da imagem (ex.: "0261206957 1037368755"), para confirmar uma desencriptação.</summary>
    public static string? Identify(byte[] image)
    {
        string txt = System.Text.Encoding.ASCII.GetString(image.Select(b => b is >= 0x20 and < 0x7F ? b : (byte)0).ToArray());
        var m = System.Text.RegularExpressions.Regex.Match(txt, @"0261\d{6}");
        var sw = System.Text.RegularExpressions.Regex.Match(txt, @"1037\d{6}");
        var ep = System.Text.RegularExpressions.Regex.Match(txt, @"\d+/\d/ME7[^\0]{5,60}");
        if (!m.Success && !sw.Success && !ep.Success) return null;
        return string.Join(" · ", new[] { m.Success ? "HW " + m.Value : null, sw.Success ? "SW " + sw.Value : null,
                                          ep.Success ? ep.Value.TrimEnd('/') : null }.Where(s => s != null));
    }
}
