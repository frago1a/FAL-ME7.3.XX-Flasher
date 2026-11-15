namespace ME7Tool.Core.Flashing;

/// <summary>Um sector de flash: offset (no ficheiro) e tamanho, em bytes.</summary>
public readonly record struct FlashSector(uint Offset, int Length)
{
    public uint End => Offset + (uint)Length; // exclusivo
}

/// <summary>
/// Mapa de sectores da flash. Predefinição: AMD/Fujitsu Am29F400B bottom-boot, 512 KB
/// (confirmado na tabela do firmware ME7.3.1: 0x0/0x4000/0x6000/0x8000/0x10000/...).
/// Os offsets são relativos ao início da flash (0); <see cref="BaseAddress"/> é somado ao
/// enviar RequestDownload (a ECU vê a flash em 0x800000).
/// </summary>
public sealed class FlashLayout
{
    public IReadOnlyList<FlashSector> Sectors { get; }

    /// <summary>Endereço-base da flash no espaço do ECU (CPU). ME7.3.1: 0x800000.</summary>
    public uint BaseAddress { get; set; } = 0x800000;

    public FlashLayout(IReadOnlyList<FlashSector> sectors) => Sectors = sectors;

    public int TotalSize => Sectors.Sum(s => s.Length);

    /// <summary>
    /// Offsets onde uma zona de apagar/escrever tem de começar de novo (não se pode juntar o sector
    /// anterior com o seguinte num só pedido). ME7.3: 0x10000 e 0x20000 — a "data section"
    /// (0x810000–0x81FFFF) tem de ser apagada sozinha; um 31 02 que a atravesse falha (IROM sub_5516).
    /// </summary>
    public IReadOnlyList<uint> EraseBoundaries { get; set; } = Array.Empty<uint>();

    /// <summary>
    /// Sectores que a ECU destrói como efeito do apagar de outro: (sector apagado, sector a regravar).
    /// ME7.3: apagar o 0x8000 faz a ECU apagar o 0x30000 e copiar para lá o 0x8000 antigo (backup do
    /// cabeçalho da flash, IROM sub_5516) — por isso o 0x30000 tem de ser regravado a seguir.
    /// </summary>
    public IReadOnlyList<(uint Erased, uint AlsoRewrite)> SectorBackups { get; set; } =
        Array.Empty<(uint, uint)>();

    /// <summary>Am29F400B bottom-boot, 512 KB (16K/8K/8K/32K + 7×64K), com as regras da IROM ME7.3.</summary>
    public static FlashLayout Am29F400B_BottomBoot_512k()
    {
        var s = new List<FlashSector>
        {
            new(0x00000, 0x4000), // 16K
            new(0x04000, 0x2000), // 8K
            new(0x06000, 0x2000), // 8K
            new(0x08000, 0x8000), // 32K
        };
        for (uint a = 0x10000; a < 0x80000; a += 0x10000)
            s.Add(new(a, 0x10000)); // 64K
        return new FlashLayout(s)
        {
            EraseBoundaries = new uint[] { 0x10000, 0x20000 },
            SectorBackups = new[] { (0x08000u, 0x30000u) },
        };
    }

    /// <summary>Sector que contém um dado offset (ou null se fora do mapa).</summary>
    public FlashSector? SectorAt(uint offset)
    {
        foreach (var sec in Sectors)
            if (offset >= sec.Offset && offset < sec.End) return sec;
        return null;
    }
}
