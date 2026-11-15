using ME7Tool.Core.Kwp2000;
using Xunit;

namespace ME7Tool.Core.Tests;

public class Kwp2000MessageTests
{
    [Fact]
    public void Encode_PhysicalWithEmbeddedLength_HasCorrectFrameAndChecksum()
    {
        // StartDiagnosticSession 0x85, tgt 0x11, src 0xF1
        var msg = Kwp2000Message.Request(0x11, 0xF1, Kwp2000Service.StartDiagnosticSession, 0x85);
        byte[] frame = msg.Encode();

        // Fmt=0x82 (Physical|len2) Tgt=11 Src=F1 Data=10 85 Checksum
        Assert.Equal(0x82, frame[0]);
        Assert.Equal(0x11, frame[1]);
        Assert.Equal(0xF1, frame[2]);
        Assert.Equal(0x10, frame[3]);
        Assert.Equal(0x85, frame[4]);
        byte expectedCs = unchecked((byte)(0x82 + 0x11 + 0xF1 + 0x10 + 0x85));
        Assert.Equal(expectedCs, frame[^1]);
    }

    [Fact]
    public void EncodeThenParse_Roundtrips()
    {
        var msg = Kwp2000Message.Request(0x11, 0xF1, Kwp2000Service.ReadMemoryByAddress,
            0x80, 0x00, 0x00, 0x08);
        byte[] frame = msg.Encode();

        var parsed = Kwp2000Message.TryParse(frame);
        Assert.NotNull(parsed);
        Assert.Equal(Kwp2000AddressMode.Physical, parsed!.AddressMode);
        Assert.Equal(0x11, parsed.Target);
        Assert.Equal(0xF1, parsed.Source);
        Assert.Equal((byte)Kwp2000Service.ReadMemoryByAddress, parsed.ServiceId);
        Assert.Equal(new byte[] { 0x23, 0x80, 0x00, 0x00, 0x08 }, parsed.Data);
    }

    [Fact]
    public void TryParse_BadChecksum_ReturnsNull()
    {
        var msg = Kwp2000Message.Request(0x11, 0xF1, Kwp2000Service.TesterPresent, 0x01);
        byte[] frame = msg.Encode();
        frame[^1] ^= 0xFF; // corrompe o checksum
        Assert.Null(Kwp2000Message.TryParse(frame));
    }

    [Fact]
    public void Encode_SeparateLengthByte_UsedForLongPayload()
    {
        var data = new byte[100];
        data[0] = (byte)Kwp2000Service.TransferData;
        var msg = new Kwp2000Message(Kwp2000AddressMode.Physical, 0x11, 0xF1, data);
        byte[] frame = msg.Encode();

        Assert.Equal(0x80, frame[0]);        // Physical, len embutido = 0
        Assert.Equal(0x11, frame[1]);
        Assert.Equal(0xF1, frame[2]);
        Assert.Equal(100, frame[3]);         // byte Len separado
        var parsed = Kwp2000Message.TryParse(frame);
        Assert.NotNull(parsed);
        Assert.Equal(100, parsed!.Data.Length);
    }
}
