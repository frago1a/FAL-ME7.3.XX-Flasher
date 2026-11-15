namespace ME7Tool.Core.Flashing;

/// <summary>Diferença num sector entre o original e o novo ficheiro.</summary>
public readonly record struct SectorDiff(FlashSector Sector, int BytesDiffering, long FirstDiffOffset)
{
    public bool Changed => BytesDiffering > 0;
}

/// <summary>
/// Compara o ficheiro original (o que está na ECU) com o novo, por sector.
/// Base da flash parcial (R17): só se apagam/escrevem os sectores alterados, e as
/// diferenças são mostradas ao utilizador ANTES de escrever.
/// </summary>
public static class FlashDiff
{
    /// <summary>Diferenças por sector (todos os sectores; usar .Changed para filtrar).</summary>
    public static List<SectorDiff> Compare(FlashLayout layout, ReadOnlySpan<byte> original, ReadOnlySpan<byte> modified)
    {
        if (original.Length != modified.Length)
            throw new ArgumentException($"Tamanhos diferentes: original {original.Length} vs novo {modified.Length}.");

        var result = new List<SectorDiff>(layout.Sectors.Count);
        foreach (var sec in layout.Sectors)
        {
            if (sec.End > original.Length) break;
            int diff = 0;
            long first = -1;
            for (uint i = sec.Offset; i < sec.End; i++)
            {
                if (original[(int)i] != modified[(int)i])
                {
                    diff++;
                    if (first < 0) first = i;
                }
            }
            result.Add(new SectorDiff(sec, diff, first));
        }
        return result;
    }

    /// <summary>Apenas os sectores alterados.</summary>
    public static List<FlashSector> ChangedSectors(FlashLayout layout, ReadOnlySpan<byte> original, ReadOnlySpan<byte> modified)
        => Compare(layout, original, modified).Where(d => d.Changed).Select(d => d.Sector).ToList();
}
