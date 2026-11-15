using ME7Tool.Core.Kwp2000;

namespace ME7Tool.Core.Tests;

public class KwpBaudTests
{
    [Theory]
    [InlineData(10400)]
    [InlineData(19200)]
    [InlineData(38400)]
    [InlineData(57600)]
    [InlineData(115200)]
    [InlineData(124800)]
    public void ByteRoundTrip_IsCloseToRequested(uint baud)
    {
        byte b = KwpBaud.ToByte(baud);
        uint decoded = KwpBaud.FromByte(b);
        // A codificação XXXYYYYY é discreta; aceitamos ~3% de erro de quantização.
        Assert.InRange(decoded, (uint)(baud * 0.97), (uint)(baud * 1.03));
    }

    [Fact]
    public void FromByte_UsesFiatFormula()
    {
        // byte = XXXYYYYY, baud = (2^X * (Y+32) * 6400) / 32
        // X=3, Y=0 → (8 * 32 * 6400)/32 = 51200
        Assert.Equal(51200u, KwpBaud.FromByte(0x60));
    }
}
