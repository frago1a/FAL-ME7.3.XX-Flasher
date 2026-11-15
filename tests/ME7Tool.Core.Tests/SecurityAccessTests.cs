using ME7Tool.Core.Kwp2000;
using Xunit;

namespace ME7Tool.Core.Tests;

/// <summary>
/// Cruza o port C# do seed/key com os valores de referência gerados pelo me73_seedkey.py
/// (seed 0x1A2B3C4D, SeedParameter 0 → 35 iterações), que por sua vez bate com o firmware.
/// </summary>
public class SecurityAccessTests
{
    const uint Seed = 0x1A2B3C4D;

    [Theory]
    [InlineData(0x75775EB5u, 0x68F543B3u)] // idx 0
    [InlineData(0x5AEDFED5u, 0x523B7336u)] // idx 1
    [InlineData(0x564A4F25u, 0x21B22596u)] // per-ECU (idx 5) desta Thesis
    [InlineData(0xFABC568Eu, 0x937355F0u)] // fallback
    public void ComputeKey_MatchesPythonReference(uint poly, uint expectedKey)
    {
        uint key = SecurityAccess.ComputeKey(Seed, poly, 35);
        Assert.Equal(expectedKey, key);
    }

    [Fact]
    public void IterationsFromSeedParameter_Zero_Is0x23()
        => Assert.Equal(0x23, SecurityAccess.IterationsFromSeedParameter(0));

    [Theory]
    [InlineData(0x01, 0x41)] // 0x27 01 → nível clamp 0x41 (índice 5, por-ECU)
    [InlineData(0x02, 0x41)]
    [InlineData(0x7D, 0x3F)] // subfunção alta → (0x7D+1)>>1 = 0x3F (índice 3)
    public void LevelFromSubFunction_Clamps(byte subFunc, int expectedLevel)
        => Assert.Equal(expectedLevel, SecurityAccess.LevelFromSubFunction(subFunc));

    [Fact]
    public void PolyForLevel_UsesInternalTableForLow_PerEcuFor0x41()
    {
        Assert.Equal(0x75775EB5u, SecurityAccess.PolyForLevel(0x3C, 0));
        Assert.Equal(SecurityAccess.PerEcuPolyThesis, SecurityAccess.PolyForLevel(0x41, SecurityAccess.PerEcuPolyThesis));
        // zona em branco → fallback
        Assert.Equal(SecurityAccess.FallbackPoly, SecurityAccess.PolyForLevel(0x41, 0xFFFFFFFF));
    }

    [Fact]
    public void ComputeFiatKey_Standard0x27_01_UsesPerEcuPoly()
    {
        // subfunção 1 → nível 0x41 → per-ECU; param 0 → 35 iterações
        uint key = SecurityAccess.ComputeFiatKey(Seed, subFunction: 0x01, seedParameter: 0);
        Assert.Equal(0x21B22596u, key);
    }
}
