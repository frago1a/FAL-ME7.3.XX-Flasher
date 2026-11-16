using static ME7Tool.Core.Common.Lang;

namespace ME7Tool.Core.Flashing;

/// <summary>Metade do CRC-32 que o programador verifica no 31 01.</summary>
public enum CrcHalf { High, Low }

/// <summary>
/// Perfil de flash completo por OBD da família ME7.3 Fiat, para as duas variantes que partilham o mesmo
/// hardware (C167) e a mesma cifra (<see cref="FiatCipher"/>), mas têm procedimentos diferentes:
/// <list type="bullet">
/// <item><b>ME7.3.1</b> (Thesis 2.0, Stilo/Alfa 1.8/2.0): flash vista em 0x800000, apaga e grava os 512 KB
/// todos (incluindo o arranque); checksum = metade de cima do CRC-32.</item>
/// <item><b>ME3.1</b> (Thesis 2.4, Stilo 2.4 — mesmo hardware, outro software): flash vista em 0x000000, o
/// programador fica no arranque 0x0000–0x3FFF e NÃO é apagado; apaga e grava 0x4000–0x7FFFF (496 KB);
/// checksum = metade de baixo do CRC-32.</item>
/// </list>
/// Endereços/tamanhos confirmados no firmware (Stilo ME3.1: kwp_31_02_EraseFlash, kwp_34_RequestDownload,
/// kwp_31_01_Checksum) e nos .Prm do Examiner.
/// </summary>
public sealed record FiatFlashProfile(
    string Name,
    uint FlashBase,
    uint EraseStart,
    uint EraseEnd,
    uint DownloadStart,
    int PayloadSize,
    int PayloadOffsetInImage,
    CrcHalf Checksum,
    bool HasBackupZone)
{
    /// <summary>dataFormatIdentifier (0x01 = cifrado) — igual nas duas.</summary>
    public const byte DataFormat = 0x01;

    /// <summary>Tamanho da imagem completa da ECU (512 KB nas duas; na ME3.1 o arranque não é gravado).</summary>
    public const int FullImageSize = 0x80000;

    /// <summary>Zona de backup que a ECU gere sozinha (cópia do sector de calibração) — só na ME7.3.1.</summary>
    public const int BackupOffset = 0x38000, BackupLength = 0x8000;

    /// <summary>ME7.3.1: 512 KB em 0x800000, apaga tudo, checksum metade de cima.</summary>
    public static readonly FiatFlashProfile Me731 = new(
        "ME7.3.1", 0x800000, 0x800000, 0x87FFFF, 0x800000, 0x80000, 0x00000, CrcHalf.High, HasBackupZone: true);

    /// <summary>ME3.1: 496 KB em 0x004000 (arranque 0x0000–0x3FFF intacto), checksum metade de baixo.</summary>
    public static readonly FiatFlashProfile Me31 = new(
        "ME3.1", 0x000000, 0x004000, 0x07FFFF, 0x004000, 0x7C000, 0x04000, CrcHalf.Low, HasBackupZone: false);

    public static readonly IReadOnlyList<FiatFlashProfile> All = new[] { Me731, Me31 };

    /// <summary>
    /// Deteta o perfil de uma imagem completa de 512 KB pela posição do bloco de identificação: a ME3.1 tem-no
    /// na calibração (perto de 0x80C6) e a ME7.3.1 mais acima (perto de 0x10190). null se não houver bloco.
    /// </summary>
    public static FiatFlashProfile? Detect(byte[] image)
    {
        if (image.Length != FullImageSize) return null;
        if (EcuIdent.FromImage(image, out int off) == null || off < 0) return null;
        return off < 0x10000 ? Me31 : Me731;
    }

    public uint EraseEndInclusive => EraseEnd;

    /// <summary>
    /// Imagem a enviar (payload cifrável), tirada da imagem completa de 512 KB. ME7.3.1: os 512 KB, com a zona
    /// de backup em branco como o Examiner. ME3.1: os 496 KB a partir de 0x4000 (sem o arranque).
    /// </summary>
    public byte[] Prepare(byte[] image)
    {
        if (image.Length != FullImageSize)
            throw new ArgumentException(T($"A imagem tem de ter {FullImageSize} bytes (512 KB).", $"The image must be {FullImageSize} bytes (512 KB)."), nameof(image));
        var payload = image.AsSpan(PayloadOffsetInImage, PayloadSize).ToArray();
        if (HasBackupZone)
        {
            // igual ao Examiner: zona de backup em branco (FF) exceto o word 0x8000 no início/fim de cada 8 KB
            int off = BackupOffset - PayloadOffsetInImage;
            payload.AsSpan(off, BackupLength).Fill(0xFF);
            for (int b = off; b < off + BackupLength; b += 0x2000)
            {
                payload[b] = 0x00; payload[b + 1] = 0x80;
                payload[b + 0x1FFE] = 0x00; payload[b + 0x1FFF] = 0x80;
            }
        }
        return payload;
    }

    /// <summary>Payload tal como vem de um ficheiro do Examiner (já é só o que se grava): valida o tamanho.</summary>
    public byte[] PayloadFromExaminer(byte[] decrypted)
    {
        if (decrypted.Length != PayloadSize)
            throw new ArgumentException(T($"O payload tem de ter {PayloadSize} bytes.", $"The payload must be {PayloadSize} bytes."), nameof(decrypted));
        return decrypted;
    }

    /// <summary>Checksum do 31 01 (2 bytes): a metade escolhida do CRC-32 do payload preparado.</summary>
    public ushort ChecksumOf(byte[] prepared)
    {
        uint crc = Flasher.Crc32(prepared);
        return (ushort)(Checksum == CrcHalf.High ? crc >> 16 : crc & 0xFFFF);
    }

    public override string ToString() => Name;
}
