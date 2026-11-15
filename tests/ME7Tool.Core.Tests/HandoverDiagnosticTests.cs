using ME7Tool.Core.Flashing;
using ME7Tool.Core.Kwp2000;
using Xunit;

namespace ME7Tool.Core.Tests;

public class HandoverDiagnosticTests
{
    const byte Ecu = 0x11, Tester = 0xF1;

    private static byte[] Resp(params byte[] data)
        => new Kwp2000Message(Kwp2000AddressMode.Physical, Tester, Ecu, data).Encode();

    /// <summary>
    /// ECU falsa com memória: RAM interna (0xE000–0xE7FF) e XRAM (0x380000–0x387FFF). Responde ao 0x23;
    /// opcionalmente só depois de 10 86 (para testar a mudança de sessão).
    /// </summary>
    public sealed class MemEcu
    {
        public readonly byte[] Iram = new byte[0x800];
        public readonly byte[] Xram = new byte[0x8000];
        public bool ReadOnlyIn86;
        public bool In86;
        public bool Locked85;                 // como a Thesis depois da recusa: em 0x85 recusa 10 86 (0x12)
        public bool InProgramming;            // sessão atual 0x85
        public int Connects;
        public List<(uint From, uint To)> Denied = new();
        public enum Ram { Real32k, Absent16k, Mirror16k }
        public Ram Hw = Ram.Real32k;

        /// <summary>Índice real na Xram para um endereço 0x38xxxx (−1 = não há RAM).</summary>
        private int XIdx(uint a)
        {
            int o = (int)(a - 0x380000);
            if (o >= 0x4000) return Hw switch { Ram.Absent16k => -1, Ram.Mirror16k => o - 0x4000, _ => o };
            return o;
        }

        public byte[]? Respond(Kwp2000Message req)
        {
            var d = req.Data;
            switch ((Kwp2000Service)d[0])
            {
                case Kwp2000Service.StartCommunication:
                    Connects++; InProgramming = false; In86 = false;
                    return new Kwp2000Message(Kwp2000AddressMode.Physical, Tester, Ecu, 0xC1, 0xEF, 0x8F).Encode();
                case Kwp2000Service.StopCommunication: return Resp(0xC2);
                case Kwp2000Service.StartDiagnosticSession:
                    if (Locked85 && InProgramming && d[1] == 0x86) return Resp(0x7F, 0x10, 0x12);
                    In86 = d[1] == 0x86; InProgramming = d[1] == 0x85;
                    return Resp(0x50, d[1]);
                case Kwp2000Service.ReadMemoryByAddress:
                    if (ReadOnlyIn86 && !In86) return Resp(0x7F, 0x23, 0x11);
                    uint a = (uint)(d[1] << 16 | d[2] << 8 | d[3]); int n = d[4];
                    if (Denied.Any(r => a <= r.To && a + n - 1 >= r.From)) return Resp(0x7F, 0x23, 0x31);
                    if (a >= 0xE000 && a + n <= 0xE800)
                        return Resp(new byte[] { 0x63 }.Concat(Iram.AsSpan((int)(a - 0xE000), n).ToArray()).ToArray());
                    if (a >= 0x380000 && a + n <= 0x388000)
                    {
                        var r = new byte[n];
                        for (int k = 0; k < n; k++) { int x = XIdx(a + (uint)k); r[k] = x < 0 ? (byte)0xFF : Xram[x]; }
                        return Resp(new byte[] { 0x63 }.Concat(r).ToArray());
                    }
                    return Resp(0x7F, 0x23, 0x31);
                case Kwp2000Service.WriteMemoryByAddress:
                    {
                        uint wa = (uint)(d[1] << 16 | d[2] << 8 | d[3]); int wn = d[4];
                        if (wa < 0x380000 || wa + wn > 0x388000) return Resp(0x7F, 0x3D, 0x31);
                        for (int k = 0; k < wn; k++) { int x = XIdx(wa + (uint)k); if (x >= 0) Xram[x] = d[5 + k]; }
                        return Resp(0x7D);
                    }
                default: return Resp(0x7F, d[0], 0x11);
            }
        }
    }

    private static (Kwp2000Interface kwp, MemEcu ecu) Make()
    {
        var dev = new FakeCommunicationDevice(); dev.Open();
        var ecu = new MemEcu();
        dev.AutoResponder = ecu.Respond;
        var kwp = new Kwp2000Interface(dev, new KwpTiming { P4Min = 0, ResponseTimeoutMs = 200, InterByteTimeoutMs = 50 }, Ecu, Tester)
        {
            FastInitTargets = new[] { Ecu },
            FastInitIdleMs = 0,
            FastInitResponseTimeoutMs = 50,
        };
        return (kwp, ecu);
    }

    /// <summary>Imagem de 512 KB com o "programador" (bytes pseudo-aleatórios) em 0x8048 e o código do carregador.</summary>
    private static byte[] Image(int programmerLen)
    {
        var img = new byte[0x80000];
        var rnd = new Random(1234);
        for (int i = 0; i < programmerLen; i++) img[0x8048 + i] = (byte)rnd.Next(256);
        // carregador: mov r8,#8048h ... cmp r12,#n (n palavras)
        int words = programmerLen / 2;
        new byte[] { 0xE6, 0xF8, 0x48, 0x80, 0xE6, 0xF9, 0x80, 0x00, 0x46, 0xFC, (byte)words, (byte)(words >> 8) }
            .CopyTo(img, 0xD8C8);
        return img;
    }

    private static void StateAfterRefusal(MemEcu ecu)
    {
        ecu.Iram[0xE5E0 - 0xE000] = 0x09;          // kwp09_Bits: bit0 (cópia feita) + bit3 (em curso), bit1 = 0
        ecu.Iram[0xE5E2 - 0xE000] = 0x07;          // flashorder 7
        ecu.Iram[0xE2BC - 0xE000] = 0xAA;          // flashState válido
    }

    [Fact]
    public void DetectProgrammerLength_ReadsTheCopyLoopCount()
        => Assert.Equal(0x56D0, HandoverDiagnostic.DetectProgrammerLength(Image(0x56D0)));

    [Fact]
    public void CopyStoppedMidway_ReportsBoundaryAndFirstDifferentWord()
    {
        const int len = 0x56D0, stopAt = 0x2A40;
        var img = Image(len);
        var (kwp, ecu) = Make();
        StateAfterRefusal(ecu);
        Array.Copy(img, 0x8048, ecu.Xram, 0, stopAt);                   // copiado até stopAt
        for (int i = stopAt; i < len; i++) ecu.Xram[i] = (byte)(img[0x8048 + i] ^ 0x5A); // o resto: outra coisa

        var r = new HandoverDiagnostic().Run(kwp, img, 0);

        Assert.Contains(r.Summary, s => s.Contains("kwp09_Bits = 0x0009") && s.Contains("permissão=0"));
        Assert.Contains(r.Summary, s => s.Contains("flashorder = 0x07"));
        Assert.Contains(r.Summary, s => s.Contains("parado entre"));
        Assert.Contains(r.Summary, s => s.Contains($"0x{0x380000 + stopAt:X6}") && s.Contains("Primeira palavra diferente"));
        Assert.Contains("== RAM interna", r.Detail);
    }

    [Fact]
    public void CopyNeverRan_SaysSo()
    {
        var img = Image(0x56D0);
        var (kwp, ecu) = Make();
        StateAfterRefusal(ecu);
        for (int i = 0; i < 0x56D0; i++) ecu.Xram[i] = (byte)(img[0x8048 + i] ^ 0xFF);
        var r = new HandoverDiagnostic().Run(kwp, img, 0);
        Assert.Contains(r.Summary, s => s.Contains("NÃO correu"));
    }

    [Fact]
    public void CopyComplete_SaysSo()
    {
        var img = Image(0x56D0);
        var (kwp, ecu) = Make();
        StateAfterRefusal(ecu);
        Array.Copy(img, 0x8048, ecu.Xram, 0, 0x56D0);
        var r = new HandoverDiagnostic().Run(kwp, img, 0);
        Assert.Contains(r.Summary, s => s.Contains("até ao fim"));
    }

    [Fact]
    public void ReadNotAllowedIn85_SwitchesTo86_AndDeniedZonesAreSkipped()
    {
        var img = Image(0x56D0);
        var (kwp, ecu) = Make();
        ecu.ReadOnlyIn86 = true;
        ecu.Denied.Add((0x380C02, 0x380C08));                           // zona negada sobre a amostra 0x380C00
        StateAfterRefusal(ecu);
        Array.Copy(img, 0x8048, ecu.Xram, 0, 0x56D0);
        var r = new HandoverDiagnostic().Run(kwp, img, 0);
        Assert.True(ecu.In86);
        Assert.Contains(r.Summary, s => s.Contains("sessão 0x86"));
        Assert.Contains("(não legível)", r.Detail);                     // a amostra na zona negada
    }

    [Fact]
    public void In85_23And1086Refused_ReconnectsInto86_AndReads()
    {
        // Como no log da Thesis: em 0x85, 23 → 0x11 e 10 86 → 0x12. Só uma ligação nova deixa entrar em 0x86.
        var img = Image(0x56D0);
        var (kwp, ecu) = Make();
        ecu.ReadOnlyIn86 = true; ecu.Locked85 = true; ecu.InProgramming = true;
        StateAfterRefusal(ecu);
        Array.Copy(img, 0x8048, ecu.Xram, 0, 0x56D0);
        var r = new HandoverDiagnostic().Run(kwp, img, 0);
        Assert.Equal(1, ecu.Connects);
        Assert.True(ecu.In86);
        Assert.Contains(r.Summary, s => s.Contains("ligação nova"));
        Assert.Contains(r.Summary, s => s.Contains("até ao fim"));
    }

    [Theory]
    [InlineData(MemEcu.Ram.Real32k, "RAM real e independente")]
    [InlineData(MemEcu.Ram.Absent16k, "NÃO ficou")]
    [InlineData(MemEcu.Ram.Mirror16k, "REPETE-SE")]
    public void RamTest_DetectsRealAbsentOrMirroredUpperRam(MemEcu.Ram hw, string expectUpper)
    {
        var (kwp, ecu) = Make();
        ecu.Hw = hw;
        var rnd = new Random(7); rnd.NextBytes(ecu.Xram);
        var before = (byte[])ecu.Xram.Clone();
        var r = new HandoverDiagnostic().RunRamTest(kwp, 0);
        Assert.Contains(r.Summary, s => s.StartsWith("0x380800") && s.Contains("RAM real"));      // controlo
        Assert.Contains(r.Summary, s => s.StartsWith("0x385000") && s.Contains(expectUpper));
        Assert.Equal(before, ecu.Xram);                                                      // tudo reposto
    }

    [Fact]
    public void RamActivity_QuietRam_SaysStopped_AndWritesNothing()
    {
        var (kwp, ecu) = Make();
        new Random(3).NextBytes(ecu.Xram);
        var before = (byte[])ecu.Xram.Clone();
        var r = new HandoverDiagnostic().RunRamActivity(kwp, 0, passes: 2, pauseMs: 0);
        Assert.Contains(r.Summary, s => s.Contains("XRAM PARADA"));
        Assert.Equal(before, ecu.Xram);
    }

    [Fact]
    public void RamActivity_ChangingBytes_AreReportedWithAddressAndZone()
    {
        var (kwp, ecu) = Make();
        int calls = 0;
        var dev = new FakeCommunicationDevice(); dev.Open();
        dev.AutoResponder = req =>
        {
            // a "aplicação" escreve em 0x380010 e 0x381B20 a cada leitura
            if (req.Data[0] == 0x23) { calls++; ecu.Xram[0x10] = (byte)calls; ecu.Xram[0x1B20] = (byte)(calls * 3); }
            return ecu.Respond(req);
        };
        kwp = new Kwp2000Interface(dev, new KwpTiming { P4Min = 0, ResponseTimeoutMs = 200, InterByteTimeoutMs = 50 }, Ecu, Tester);
        var r = new HandoverDiagnostic().RunRamActivity(kwp, 0, passes: 3, pauseMs: 0);
        Assert.Contains(r.Summary, s => s.Contains("XRAM ATIVA"));
        Assert.Contains(r.Summary, s => s.Contains("tabelas de medição (0x381B00–0x381CFF): 1"));
        Assert.Contains("380010:", r.Detail);
        Assert.Contains("381B20:", r.Detail);
    }

    [Fact]
    public void FlashStateDD_ComparesWithBackupProgrammer()
    {
        var img = Image(0x56D0);
        var rnd = new Random(99);
        for (int i = 0; i < 0x56D0; i++) img[0x38048 + i] = (byte)rnd.Next(256); // programador do backup
        var (kwp, ecu) = Make();
        StateAfterRefusal(ecu);
        ecu.Iram[0xE2BC - 0xE000] = 0xDD;
        Array.Copy(img, 0x38048, ecu.Xram, 0, 0x56D0);
        var r = new HandoverDiagnostic().Run(kwp, img, 0);
        Assert.Contains(r.Summary, s => s.Contains("0x838048"));
        Assert.Contains(r.Summary, s => s.Contains("até ao fim"));
    }
}
