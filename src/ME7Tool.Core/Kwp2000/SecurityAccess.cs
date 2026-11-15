namespace ME7Tool.Core.Kwp2000;

/// <summary>
/// Cálculo seed→key do Bosch ME7 (LFSR Galois de 32 bits).
/// Confirmado em três fontes independentes: firmware ME7.3.1 (sub_4746/sub_6090),
/// NefMoto (mInternalROMKeyTable) e o extrato do binário (poly table @ 0x58C).
///
/// Algoritmo (N iterações):
///   x = seed
///   repetir N: x = (x &amp; 0x80000000) != 0 ? (x &lt;&lt; 1) ^ poly : (x &lt;&lt; 1)
///   key = x
/// </summary>
public static class SecurityAccess
{
    /// <summary>
    /// Tabela interna da ROM do C167 — COMUM a todo o Bosch ME7 (VAG e Fiat).
    /// Idêntica byte-a-byte ao firmware Fiat (0x58C) e ao NefMoto.
    /// Índice = nívelDeAcesso − 0x3C (0..4).
    /// </summary>
    public static readonly uint[] InternalRomKeyTable =
    {
        0x75775EB5, // idx 0 (nível 0x3C)
        0x5AEDFED5, // idx 1 (nível 0x3D)
        0x6B5F7DD5, // idx 2 (nível 0x3E)
        0x6F757B6B, // idx 3 (nível 0x3F)
        0x5FBD5DBD, // idx 4 (nível 0x40)
    };

    /// <summary>Polinómio por-ECU (idx 5 / nível 0x41). Desta Thesis: lido de 0x801A.</summary>
    public const uint PerEcuPolyThesis = 0x564A4F25;

    /// <summary>Fallback usado pelo firmware quando a zona por-ECU está em branco.</summary>
    public const uint FallbackPoly = 0xFABC568E;

    private const uint Mask = 0xFFFFFFFF;

    /// <summary>Núcleo do LFSR: aplica N iterações com o polinómio dado.</summary>
    public static uint ComputeKey(uint seed, uint poly, int iterations)
    {
        uint x = seed;
        for (int i = 0; i < iterations; i++)
        {
            if ((x & 0x80000000) != 0)
                x = ((x << 1) ^ poly) & Mask;
            else
                x = (x << 1) & Mask;
        }
        return x;
    }

    /// <summary>
    /// Nr. de iterações a partir do SeedParameter (firmware: param + 0x23, capped em 0xFF
    /// se transbordar abaixo de 0x23).
    /// </summary>
    public static int IterationsFromSeedParameter(byte seedParameter)
    {
        int n = (seedParameter + 0x23) & 0xFF;
        if (n < 0x23) n = 0xFF;
        return n;
    }

    /// <summary>
    /// Nível de acesso derivado da sub-função do pedido 0x27 (dispatcher Fiat):
    /// nível = clamp((subfunção + 1) &gt;&gt; 1, 0x3C, 0x41).
    /// </summary>
    public static int LevelFromSubFunction(byte subFunction)
    {
        int level = (subFunction + 1) >> 1;
        if (level < 0x3C || level > 0x40) level = 0x41;
        return level;
    }

    /// <summary>
    /// Devolve o polinómio para um nível de acesso (0x3C..0x41).
    /// 0x3C..0x40 → tabela interna; 0x41 → por-ECU (ou fallback).
    /// </summary>
    public static uint PolyForLevel(int level, uint perEcuPoly)
    {
        int index = level - 0x3C;
        if (index >= 0 && index < InternalRomKeyTable.Length)
            return InternalRomKeyTable[index];
        // idx 5 (nível 0x41): valor por-ECU; fallback se em branco (0 ou 0xFFFFFFFF)
        if (perEcuPoly == 0 || perEcuPoly == 0xFFFFFFFF)
            return FallbackPoly;
        return perEcuPoly;
    }

    /// <summary>
    /// Fluxo completo Fiat ME7.3.1: calcula a key a partir do seed de 32 bits.
    /// </summary>
    /// <param name="seed">Seed de 32 bits recebido (big-endian na mensagem).</param>
    /// <param name="subFunction">Sub-função do pedido 0x27 (1 = request seed; par = send key).</param>
    /// <param name="seedParameter">Parâmetro do pedido (controla nr. de iterações).</param>
    /// <param name="perEcuPoly">Polinómio por-ECU (idx 5); usa PerEcuPolyThesis/fallback se não souber.</param>
    public static uint ComputeFiatKey(uint seed, byte subFunction, byte seedParameter, uint perEcuPoly = PerEcuPolyThesis)
    {
        int level = LevelFromSubFunction(subFunction);
        uint poly = PolyForLevel(level, perEcuPoly);
        int iterations = IterationsFromSeedParameter(seedParameter);
        return ComputeKey(seed, poly, iterations);
    }
}
