using static ME7Tool.Core.Common.Lang;

namespace ME7Tool.Core.Bsl;

/// <summary>
/// Escrita da flash externa (AMD 29F400BB, 512 KB, arranque em baixo) por modo boot, com o driver do C167BootTool
/// (GPL-3.0). Mais prudente do que o original: só apaga e grava os sectores que mudam em relação ao que está na flash
/// (o chamador lê-a antes e guarda-a como backup), recusa chips desconhecidos e verifica os sectores gravados.
/// Se correr mal, volta-se a entrar em modo boot (está na ROM do C167, não na flash) e grava-se de novo.
/// </summary>
public static class BslWrite
{
    private const uint Syscon = 0xFF12, Buscon0 = 0xFF0C, Buscon1 = 0xFF14, Buscon2 = 0xFF16, Buscon3 = 0xFF18, Buscon4 = 0xFF1A;
    private const uint Addrsel1 = 0xFE18, Addrsel2 = 0xFE1A, Addrsel3 = 0xFE1C, Addrsel4 = 0xFE1E;
    private const uint DriverAddress = 0xF600, DriverEntry = 0xF640, Buffer = 0xFC00;
    private const ushort FcProg = 0x00, FcErase = 0x01, FcGetState = 0x06;
    private const int BlockSize = 0x200;

    /// <summary>Tempo máximo à espera de um apagar de sector (o driver tem o seu próprio limite; 64 KB ~1 s típico).</summary>
    public const int EraseTimeoutMs = 60000;

    /// <summary>IDs lidos do chip da flash.</summary>
    public sealed record FlashChip(ushort Manufacturer, ushort Device)
    {
        /// <summary>AMD/Spansion Am29F400BB (512 KB, arranque em baixo) — o das ME7.3 / ME3.1 da Fiat.</summary>
        public bool IsAm29F400Bb => (Manufacturer & 0xFF) == 0x01 && Device == 0x22AB;

        public string Name => IsAm29F400Bb ? "Am29F400BB (512 KB)"
            : Device switch { 0x2258 => "Am29F800BB (1 MB)", 0x2223 => "Am29F400BT (512 KB, arranque em cima)", 0x22D6 => "Am29F800BT (1 MB)", _ => "?" };

        public override string ToString() => T($"{Name} (fabricante 0x{Manufacturer:X2}, chip 0x{Device:X4})", $"{Name} (manufacturer 0x{Manufacturer:X2}, device 0x{Device:X4})");
    }

    /// <summary>Sectores da Am29F400BB: 16 K, 8 K, 8 K, 32 K e 7 × 64 K (início, tamanho; offset na flash).</summary>
    public static readonly IReadOnlyList<(int Start, int Size)> Sectors = new[]
    {
        (0x00000, 0x4000), (0x04000, 0x2000), (0x06000, 0x2000), (0x08000, 0x8000),
        (0x10000, 0x10000), (0x20000, 0x10000), (0x30000, 0x10000), (0x40000, 0x10000),
        (0x50000, 0x10000), (0x60000, 0x10000), (0x70000, 0x10000),
    };

    /// <summary>Configura o barramento para escrita, carrega o driver e lê os IDs do chip.</summary>
    public static FlashChip PrepareAndReadChip(C167Bsl bsl)
    {
        bsl.SetWord(Syscon, 0xE204);
        foreach (var r in new[] { Addrsel1, Addrsel2, Addrsel3, Addrsel4, Buscon1, Buscon2, Buscon3, Buscon4 })
            bsl.SetWord(r, 0);
        bsl.SetWord(Buscon0, 0x04AD);
        bsl.WriteBlock(DriverAddress, BslResources.Flash29FDriver);

        ushort page = (ushort)(BslRead.ExternalFlashAddress >> 16);
        var m = bsl.Call(DriverEntry, FcGetState, 0, page, page, 0, 0, 0, 1);
        var d = bsl.Call(DriverEntry, FcGetState, 0, page, page, 0, 0, 1, 1);
        if (m[7] != 0 || d[7] != 0)
            throw new BslException(T("Não foi possível ler os IDs da flash.", "Could not read the flash IDs."));
        return new FlashChip(m[1], d[1]);
    }

    /// <summary>Índices dos sectores em que <paramref name="image"/> difere de <paramref name="current"/> (512 KB cada).</summary>
    public static List<int> ChangedSectors(byte[] image, byte[] current)
    {
        CheckSize(image); CheckSize(current);
        var list = new List<int>();
        for (int s = 0; s < Sectors.Count; s++)
        {
            var (start, size) = Sectors[s];
            if (!image.AsSpan(start, size).SequenceEqual(current.AsSpan(start, size))) list.Add(s);
        }
        return list;
    }

    /// <summary>
    /// Apaga e grava os sectores indicados (com o barramento e o driver já preparados por
    /// <see cref="PrepareAndReadChip"/>). Blocos só com FF ficam apagados, não se programam.
    /// <paramref name="progress"/>: (texto, feito, total) em bytes dos sectores a gravar.
    /// </summary>
    public static void WriteSectors(C167Bsl bsl, byte[] image, IReadOnlyList<int> sectors, Action<string, int, int>? progress = null)
    {
        CheckSize(image);
        int total = sectors.Sum(s => Sectors[s].Size), done = 0;
        foreach (int s in sectors)
        {
            var (start, size) = Sectors[s];
            uint addr = BslRead.ExternalFlashAddress + (uint)start;
            ushort page = (ushort)(addr >> 16);
            ushort lastWord = (ushort)((addr + (uint)size - 2) & 0xFFFF);
            progress?.Invoke(T($"a apagar o sector {s}", $"erasing sector {s}"), done, total);
            var er = bsl.CallWithTimeout(DriverEntry, EraseTimeoutMs, FcErase, (ushort)addr, page, page, lastWord, 0, (ushort)s, 1);
            if (er[7] != 0)
                throw new BslException(T($"Apagar o sector {s} (0x{start:X5}) falhou (código 0x{er[7]:X2}).", $"Erasing sector {s} (0x{start:X5}) failed (code 0x{er[7]:X2})."));

            for (int off = start; off < start + size; off += BlockSize)
            {
                var block = image.AsSpan(off, BlockSize).ToArray();
                if (!block.All(b => b == 0xFF))
                {
                    bsl.WriteBlock(Buffer, block);
                    uint dst = BslRead.ExternalFlashAddress + (uint)off;
                    var pr = bsl.Call(DriverEntry, FcProg, BlockSize, (ushort)Buffer, 0, (ushort)(dst >> 16), (ushort)dst, (ushort)(dst >> 16), 1);
                    if (pr[7] != 0)
                        throw new BslException(T($"Programar 0x{off:X5} falhou (código 0x{pr[7]:X2}).", $"Programming 0x{off:X5} failed (code 0x{pr[7]:X2})."));
                }
                done += BlockSize;
                progress?.Invoke(T($"a gravar o sector {s}", $"writing sector {s}"), done, total);
            }
        }
    }

    /// <summary>
    /// Relê os sectores indicados (com a configuração de leitura validada no carro) e devolve os que não ficaram
    /// iguais ao ficheiro.
    /// </summary>
    public static List<int> VerifySectors(C167Bsl bsl, byte[] image, IReadOnlyList<int> sectors, Action<string, int, int>? progress = null)
    {
        CheckSize(image);
        BslRead.ConfigureExternalBus(bsl);
        var bad = new List<int>();
        int total = sectors.Sum(s => Sectors[s].Size), done = 0;
        foreach (int s in sectors)
        {
            var (start, size) = Sectors[s];
            int baseDone = done;
            var back = BslRead.ReadRange(bsl, BslRead.ExternalFlashAddress + (uint)start, size,
                (d, _) => progress?.Invoke(T($"a verificar o sector {s}", $"verifying sector {s}"), baseDone + d, total));
            if (!back.AsSpan().SequenceEqual(image.AsSpan(start, size))) bad.Add(s);
            done += size;
        }
        return bad;
    }

    private static void CheckSize(byte[] b)
    {
        if (b.Length != BslRead.ExternalFlashSize)
            throw new ArgumentException(T($"A imagem tem de ter {BslRead.ExternalFlashSize / 1024} KB (tem {b.Length} bytes).",
                                          $"The image must be {BslRead.ExternalFlashSize / 1024} KB (it has {b.Length} bytes)."));
    }
}
