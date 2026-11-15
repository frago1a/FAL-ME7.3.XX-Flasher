using ME7Tool.Core.Kwp2000;
using Xunit;

namespace ME7Tool.Core.Tests;

public class Kwp2000InterfaceTests
{
    const byte Ecu = 0x11;
    const byte Tester = 0xF1;

    private static (Kwp2000Interface kwp, FakeCommunicationDevice dev) Make()
    {
        var dev = new FakeCommunicationDevice();
        dev.Open();
        // Timing sem P4 (escrita num só bloco) e timeouts curtos para testes.
        var timing = new KwpTiming { P4Min = 0, ResponseTimeoutMs = 200, InterByteTimeoutMs = 50 };
        var kwp = new Kwp2000Interface(dev, timing, Ecu, Tester);
        return (kwp, dev);
    }

    private static byte[] Resp(params byte[] data)
        => new Kwp2000Message(Kwp2000AddressMode.Physical, Tester, Ecu, data).Encode();

    [Fact]
    public void SendService_PositiveResponse_ReturnsParsed()
    {
        var (kwp, dev) = Make();
        dev.EnqueueResponse(Resp(0x50, 0x85)); // resposta positiva a StartDiagnosticSession

        var resp = kwp.SendService(Kwp2000Service.StartDiagnosticSession, 0x85);

        Assert.True(resp.IsPositiveResponseTo(Kwp2000Service.StartDiagnosticSession));
        Assert.Equal(0x85, resp.Data[1]);
    }

    [Fact]
    public void NegativeResponse_Throws()
    {
        var (kwp, dev) = Make();
        dev.EnqueueResponse(Resp(0x7F, 0x23, (byte)Kwp2000ResponseCode.SecurityAccessDenied));

        var ex = Assert.Throws<Kwp2000NegativeResponseException>(
            () => kwp.SendService(Kwp2000Service.ReadMemoryByAddress, 0x80, 0, 0, 8));
        Assert.Equal(Kwp2000ResponseCode.SecurityAccessDenied, ex.ResponseCode);
        Assert.Equal(0x23, ex.RequestServiceId);
    }

    [Fact]
    public void ResponsePending_ThenPositive_Loops()
    {
        var (kwp, dev) = Make();
        // 1º pedido devolve eco + responsePending; mas só há UMA resposta por escrita.
        // Para simular 0x78 seguido de positiva, enfileiramos ambas: a 1ª escrita puxa a 1ª,
        // mas a positiva fica pendente — então pré-carregamos as duas concatenadas no buffer.
        dev.EnqueueResponse(Concat(
            Resp(0x7F, 0x27, (byte)Kwp2000ResponseCode.ResponsePending),
            Resp(0x67, 0x01, 0x11, 0x22, 0x33, 0x44)));

        var resp = kwp.SendService(Kwp2000Service.SecurityAccess, 0x01);
        Assert.True(resp.IsPositiveResponseTo(Kwp2000Service.SecurityAccess));
    }

    [Fact]
    public void StartDiagnosticSession_SetsCurrentSession()
    {
        var (kwp, dev) = Make();
        dev.EnqueueResponse(Resp(0x50, (byte)Kwp2000SessionType.Programming));
        kwp.StartDiagnosticSession(Kwp2000SessionType.Programming);
        Assert.Equal(Kwp2000SessionType.Programming, kwp.CurrentSession);
    }

    [Fact]
    public void ReadMemoryByAddress_ReturnsData()
    {
        var (kwp, dev) = Make();
        dev.EnqueueResponse(Resp(0x63, 0xDE, 0xAD, 0xBE, 0xEF));
        byte[] data = kwp.ReadMemoryByAddress(0x380000, 4);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, data);
    }

    [Fact]
    public void SecurityAccessUnlock_ComputesAndSendsCorrectKey()
    {
        var (kwp, dev) = Make();
        // seed = 0x1A2B3C4D → key esperada (per-ECU, 35 iter) = 0x21B22596
        dev.EnqueueResponse(Resp(0x67, 0x01, 0x1A, 0x2B, 0x3C, 0x4D)); // request seed
        dev.EnqueueResponse(Resp(0x67, 0x02));                         // send key OK

        kwp.SecurityAccessUnlock(seedSubFunction: 0x01);

        // 1º frame = pedido de seed, com o byte PP obrigatório (27 01 00) — a ME7.3 recusa 27 01 sozinho.
        var seedReq = Kwp2000Message.TryParse(dev.WrittenFrames[0]);
        Assert.Equal(new byte[] { 0x27, 0x01, 0x00 }, seedReq!.Data);

        // 2º frame escrito = pedido de key (27 02 K3 K2 K1 K0)
        var keyReq = Kwp2000Message.TryParse(dev.WrittenFrames[1]);
        Assert.NotNull(keyReq);
        Assert.Equal(new byte[] { 0x27, 0x02, 0x21, 0xB2, 0x25, 0x96 }, keyReq!.Data);
    }

    [Fact]
    public void WriteProgrammingDate_EncodesBcd()
    {
        var (kwp, dev) = Make();
        dev.EnqueueResponse(Resp(0x7B, 0x99));
        kwp.WriteProgrammingDate(new DateTime(2024, 11, 14));

        var req = Kwp2000Message.TryParse(dev.WrittenFrames[0]);
        Assert.NotNull(req);
        // 3B 99 20 24 11 14  (BCD Ano-Ano-Mês-Dia)
        Assert.Equal(new byte[] { 0x3B, 0x99, 0x20, 0x24, 0x11, 0x14 }, req!.Data);
    }

    [Fact]
    public void ReadDtcByStatus_ParsesList()
    {
        var (kwp, dev) = Make();
        // 58 count=2  (1234 status 60) (00AB status 20)
        dev.EnqueueResponse(Resp(0x58, 0x02, 0x12, 0x34, 0x60, 0x00, 0xAB, 0x20));
        var dtcs = kwp.ReadDtcByStatus();
        Assert.Equal(2, dtcs.Count);
        Assert.Equal(0x1234, dtcs[0].Code);
        Assert.Equal(0x60, dtcs[0].Status);
        Assert.Equal(0x00AB, dtcs[1].Code);
    }

    [Fact]
    public void ReadDtcByStatus_SendsFiatRequest_AndHandlesNoDtc()
    {
        var (kwp, dev) = Make();
        dev.EnqueueResponse(Resp(0x58, 0x00)); // spec 07223: sem avarias → 58 00
        var dtcs = kwp.ReadDtcByStatus();
        Assert.Empty(dtcs);
        var req = Kwp2000Message.TryParse(dev.WrittenFrames[0]);
        Assert.Equal(new byte[] { 0x18, 0x00, 0xFF, 0x00 }, req!.Data);
    }

    [Fact]
    public void ReadDtcByStatus_TruncatedList_ReturnsWhatArrived()
    {
        var (kwp, dev) = Make();
        dev.EnqueueResponse(Resp(0x58, 0x03, 0x03, 0x35, 0xE4)); // anuncia 3, só vem 1
        var dtcs = kwp.ReadDtcByStatus();
        Assert.Single(dtcs);
        Assert.Equal("P0335", dtcs[0].DisplayCode);
    }

    [Theory]
    [InlineData(0x0335, "P0335")]
    [InlineData(0x1171, "P1171")]
    [InlineData(0x4123, "C0123")]
    [InlineData(0x9001, "B1001")]
    [InlineData(0xC100, "U0100")]
    public void DtcRecord_DisplayCode(ushort code, string expected)
        => Assert.Equal(expected, new DtcRecord(code, 0).DisplayCode);

    [Fact]
    public void DtcRecord_DecodesFiatStatus()
    {
        // 0xE4 = 1 11 0 0100: MIL acesa, presente, teste não concluído, sem sinal
        var d = new DtcRecord(0x0335, 0xE4);
        Assert.True(d.WarningLamp);
        Assert.Equal(3, d.StorageState);
        Assert.False(d.TestComplete);
        Assert.Equal(0x4, d.Symptom);
        Assert.Equal("presente · MIL acesa · sem sinal · teste não concluído", d.StatusText);

        // 0x30 = 0 01 1 0000: memorizado, não presente, teste concluído, sem sintoma
        var m = new DtcRecord(0x0130, 0x30);
        Assert.Equal("memorizado (não presente agora)", m.StatusText);
    }

    [Fact]
    public void ClearDtc_SendsGroup()
    {
        var (kwp, dev) = Make();
        dev.EnqueueResponse(Resp(0x54));
        kwp.ClearDtc(0xFF00);
        var req = Kwp2000Message.TryParse(dev.WrittenFrames[0]);
        Assert.Equal(new byte[] { 0x14, 0xFF, 0x00 }, req!.Data);
    }

    [Fact]
    public void ConsecutiveRequests_DoNotPurge_AndRecordStats()
    {
        var (kwp, dev) = Make();
        for (int i = 0; i < 5; i++) dev.EnqueueResponse(Resp(0x63, (byte)i));
        for (int i = 0; i < 5; i++)
            Assert.Equal(new[] { (byte)i }, kwp.ReadMemoryByAddress(0x380000, 1));
        Assert.Equal(0, dev.DiscardCount);
        Assert.Equal(5, kwp.Stats.Averages().Count);
    }

    [Fact]
    public void Timeout_ResyncsAndNextRequestWorks()
    {
        var (kwp, dev) = Make();
        // Sem resposta → timeout → purga; o pedido seguinte funciona.
        Assert.Throws<Kwp2000TimeoutException>(() => kwp.ReadMemoryByAddress(0x380000, 1));
        Assert.True(dev.DiscardCount >= 1);
        dev.EnqueueResponse(Resp(0x63, 0x42));
        Assert.Equal(new byte[] { 0x42 }, kwp.ReadMemoryByAddress(0x380000, 1));
    }

    [Fact]
    public void BadChecksum_ResyncsInsteadOfDesyncingNextFrame()
    {
        var (kwp, dev) = Make();
        var bad = Resp(0x63, 0x01); bad[^1] ^= 0xFF;
        dev.EnqueueResponse(bad);
        Assert.Throws<Kwp2000Exception>(() => kwp.ReadMemoryByAddress(0x380000, 1));
        dev.EnqueueResponse(Resp(0x63, 0x02));
        Assert.Equal(new byte[] { 0x02 }, kwp.ReadMemoryByAddress(0x380000, 1));
    }

    [Fact]
    public void WriteMemoryByAddress_EncodesRequest()
    {
        var (kwp, dev) = Make();
        dev.EnqueueResponse(Resp(0x7D, 0x38, 0x41, 0x00));
        kwp.WriteMemoryByAddress(0x384100, new byte[] { 0xDE, 0xAD });
        var req = Kwp2000Message.TryParse(dev.WrittenFrames[0]);
        Assert.Equal(new byte[] { 0x3D, 0x38, 0x41, 0x00, 0x02, 0xDE, 0xAD }, req!.Data);
    }

    [Fact]
    public void ShortHeaders_SendsFormatWithoutAddresses_AndParsesShortResponse()
    {
        var (kwp, dev) = Make();
        kwp.ShortHeaders = true;
        dev.EnqueueResponse(new Kwp2000Message(Kwp2000AddressMode.None, 0, 0, 0x63, 0x11, 0x22).Encode());
        Assert.Equal(new byte[] { 0x11, 0x22 }, kwp.ReadMemoryByAddress(0x380010, 2));
        // 05 23 38 00 10 02 cs  (fmt = só comprimento)
        Assert.Equal(new byte[] { 0x05, 0x23, 0x38, 0x00, 0x10, 0x02, 0x72 }, dev.WrittenFrames[0]);
    }

    [Fact]
    public void Trace_LogsRequestAndChunks()
    {
        var (kwp, dev) = Make();
        var logs = new List<string>();
        kwp.OnLog = logs.Add;
        kwp.TraceNextRequests = 1;
        dev.EnqueueResponse(Resp(0x63, 0x01));
        dev.EnqueueResponse(Resp(0x63, 0x02));
        kwp.ReadMemoryByAddress(0x380000, 1);
        kwp.ReadMemoryByAddress(0x380000, 1);
        var trace = Assert.Single(logs, l => l.StartsWith("Trace:"));
        Assert.Contains("TX 8511F12338000001", trace);
        Assert.Contains("RX (ms:bytes)", trace);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0); b.CopyTo(r, a.Length);
        return r;
    }
}
