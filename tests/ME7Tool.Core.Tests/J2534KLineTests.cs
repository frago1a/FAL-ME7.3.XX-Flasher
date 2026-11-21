using ME7Tool.Core.Kwp2000;
using ME7Tool.Core.Transport;

namespace ME7Tool.Core.Tests;

/// <summary>
/// Interface J2534 falso (ISO14230): faz o init sozinho, recebe frames inteiras e não devolve eco.
/// A ECU falsa responde a cada pedido; o interface pode recusar alguns bauds.
/// </summary>
public sealed class FakeFrameDevice : IKLineFrameDevice
{
    private readonly Queue<byte> _rx = new();
    public string Name => "FakeJ2534";
    public bool IsOpen { get; private set; }
    public List<byte[]> Written { get; } = new();
    public List<int> Bauds { get; } = new();
    public int Baud { get; private set; } = 10400;
    public HashSet<int> Unsupported { get; } = new();
    public (int P1, int P3, int P4)? Timing { get; private set; }
    public byte[] FastInitReply { get; set; } = Frame(0x83, 0xF1, 0x10, 0xC1, 0xEF, 0x8F);
    public Func<Kwp2000Message, byte[]?>? Ecu { get; set; }

    public static byte[] Frame(params byte[] noChecksum)
    {
        var f = new byte[noChecksum.Length + 1];
        noChecksum.CopyTo(f, 0);
        byte cs = 0; foreach (var b in noChecksum) cs += b;
        f[^1] = cs;
        return f;
    }

    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;
    public void Dispose() { }
    public void SetBaudRate(int baudRate)
    {
        if (Unsupported.Contains(baudRate)) throw new IOException($"DATA_RATE {baudRate} not supported");
        Baud = baudRate; Bauds.Add(baudRate);
    }
    public void SetBreak(bool on) { if (on) throw new NotSupportedException(); }
    public void DiscardBuffers() => _rx.Clear();
    /// <summary>Respostas ao fast-init por ordem (vazio = FastInitReply); uma vazia simula a ECU calada.</summary>
    public Queue<byte[]> FastInitReplies { get; } = new();
    public byte[] FastInit(byte[] request)
    {
        Written.Add(request);
        var r = FastInitReplies.Count > 0 ? FastInitReplies.Dequeue() : FastInitReply;
        if (r.Length == 0) throw new IOException("FAST_INIT: ERR_TIMEOUT");
        return r;
    }
    public byte[] FiveBaudInit(byte address) => new byte[] { 0xEF, 0x8F };
    public void SetTiming(int p1MaxMs, int p3MinMs, int p4MinMs) => Timing = (p1MaxMs, p3MinMs, p4MinMs);

    public void Write(ReadOnlySpan<byte> data)
    {
        var frame = data.ToArray();
        Written.Add(frame);
        var req = Kwp2000Message.TryParse(frame) ?? throw new InvalidOperationException("partial frame written");
        var resp = Ecu?.Invoke(req);
        if (resp != null) foreach (var b in resp) _rx.Enqueue(b);
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        int n = 0;
        while (n < buffer.Length && _rx.Count > 0) buffer[n++] = _rx.Dequeue();
        return n;
    }
}

public class J2534KLineTests
{
    private static (Kwp2000Interface kwp, FakeFrameDevice dev) Make()
    {
        var dev = new FakeFrameDevice();
        var kwp = new Kwp2000Interface(dev, new KwpTiming { P4Min = 5, ResponseTimeoutMs = 100, InterByteTimeoutMs = 30 })
        {
            FastInitTargets = new byte[] { 0x10 },
        };
        // ECU: 0x10 → 50 <sessão> [baud]; 0x3E → 7E
        dev.Ecu = req => req.Data[0] switch
        {
            0x10 => FakeFrameDevice.Frame(new byte[] { (byte)(0x80 | req.Data.Length), 0xF1, 0x10, 0x50 }.Concat(req.Data.Skip(1)).ToArray()),
            0x3E => FakeFrameDevice.Frame(0x81, 0xF1, 0x10, 0x7E),
            _ => null,
        };
        return (kwp, dev);
    }

    [Fact]
    public void FastInit_ViaInterface_KeyBytesAndNoEcho()
    {
        var (kwp, dev) = Make();
        kwp.Connect();
        Assert.Equal(new byte[] { 0xEF, 0x8F }, kwp.KeyBytes);
        Assert.Equal(0x10, kwp.EcuAddress);
        Assert.False(kwp.ExpectEcho);
        Assert.Equal(FakeFrameDevice.Frame(0x81, 0x10, 0xF1, 0x81), dev.Written.Single());
        Assert.Equal((20, 0, 5), dev.Timing); // P1max da KwpTiming (20), não o timeout do PC
    }

    /// <summary>ECU tipo ME3.1: com P1max &lt; 20 ms o interface deita fora as respostas longas (medido no carro).</summary>
    private static void LoseLongRepliesBelowP1_20(FakeFrameDevice dev)
    {
        static byte[] Id(byte option) => new byte[] { 0x5A, option }.Concat(System.Text.Encoding.ASCII.GetBytes("0261206655 ")).ToArray();
        dev.Ecu = req => req.Data[0] switch
        {
            0x1A => dev.Timing?.P1 >= 20 ? FakeFrameDevice.Frame(new byte[] { (byte)(0x80 | Id(req.Data[1]).Length), 0xF1, 0x10 }.Concat(Id(req.Data[1])).ToArray()) : null,
            0x3B => dev.Timing?.P1 >= 20 ? FakeFrameDevice.Frame(0x82, 0xF1, 0x10, 0x7B, 0x98) : null,
            0x3E => FakeFrameDevice.Frame(0x81, 0xF1, 0x10, 0x7E),
            _ => null,
        };
    }

    [Fact]
    public void LostReplyOnShortP1_RaisesP1To20_AndRepeatsRead()
    {
        var (kwp, dev) = Make();
        kwp.Timing.P1Max = 5;   // o perfil Download do flasher
        kwp.Connect();
        LoseLongRepliesBelowP1_20(dev);

        var r = kwp.SendService(Kwp2000Service.ReadECUIdentification, 0x92);

        Assert.Equal(new byte[] { 0x5A, 0x92 }, r.Data.Take(2).ToArray());
        Assert.Equal(20, dev.Timing!.Value.P1);
        kwp.SendService(Kwp2000Service.ReadECUIdentification, 0x94);   // continua a 20: já não perde
    }

    [Fact]
    public void LostReplyOnShortP1_WriteIsNotRepeated_ButP1Raised()
    {
        var (kwp, dev) = Make();
        kwp.Timing.P1Max = 5;
        kwp.Connect();
        LoseLongRepliesBelowP1_20(dev);

        Assert.Throws<Kwp2000TimeoutException>(() => kwp.SendService(Kwp2000Service.WriteDataByLocalIdentifier, 0x98, 0x20));
        Assert.Single(dev.Written, f => f.Length > 4 && Kwp2000Message.TryParse(f)!.Data[0] == 0x3B);   // escrita enviada uma só vez
        Assert.Equal(20, dev.Timing!.Value.P1);
    }

    [Fact]
    public void SilentEcu_EvenAt20_KeepsShortP1()
    {
        // ECU a reiniciar depois do flash: calada a 5 ms e a 20 ms → não era o P1max, fica como estava.
        var (kwp, dev) = Make();
        kwp.Timing.P1Max = 5;
        kwp.Connect();
        dev.Ecu = _ => null;

        Assert.Throws<Kwp2000TimeoutException>(() => kwp.SendService(Kwp2000Service.TesterPresent));
        Assert.Equal(5, dev.Timing!.Value.P1);
    }

    [Fact]
    public void Request_IsWrittenAsOneFrame_EvenWithP4()
    {
        var (kwp, dev) = Make();
        kwp.Connect();
        kwp.TesterPresent(true);
        Assert.Equal(FakeFrameDevice.Frame(0x81, 0x10, 0xF1, 0x3E), dev.Written.Last()); // Fiat 07223: só "3E"
    }

    [Fact]
    public void Session_BaudUnsupportedByInterface_StaysAt10400()
    {
        var (kwp, dev) = Make();
        dev.Unsupported.Add(124800);
        kwp.Connect();
        kwp.StartDiagnosticSession(Kwp2000SessionType.Programming, 124800);
        Assert.Equal(10400, dev.Baud);
        // pedido com o byte do baud de init (o programador exige-o) ou sem byte; nunca 124800
        var req = Kwp2000Message.TryParse(dev.Written.Last())!;
        Assert.Equal(0x10, req.Data[0]);
        Assert.True(req.Data.Length == 2 || req.Data[2] == KwpBaud.ToByte(10400));
    }

    [Fact]
    public void Session_BaudSupported_SwitchesAfterAck()
    {
        var (kwp, dev) = Make();
        kwp.Connect();
        kwp.StartDiagnosticSession(Kwp2000SessionType.Programming, 124800);
        int expected = (int)KwpBaud.FromByte(KwpBaud.ToByte(124800));
        Assert.Equal(expected, dev.Baud);
        Assert.Equal(new[] { 10400, expected, 10400, expected }, dev.Bauds); // init, experimenta, volta, troca
    }

    [Fact]
    public void FunctionalInit_0x33_UsesPhysicalAddressThatAnswered()
    {
        var (kwp, dev) = Make();
        kwp.FastInitTargets = new byte[] { KLinePresets.ObdFunctional };
        kwp.Connect();
        Assert.Equal(FakeFrameDevice.Frame(0xC1, 0x33, 0xF1, 0x81), dev.Written.Single()); // modo funcional
        Assert.Equal(0x10, kwp.EcuAddress);
    }

    [Fact]
    public void KLinePresets_ScanFastFirst()
    {
        var order = KLinePresets.ScanOrder;
        int firstSlow = order.ToList().FindIndex(x => x.Init == KLineInit.FiveBaud);
        Assert.True(firstSlow > 0);
        Assert.All(order.Skip(firstSlow), x => Assert.Equal(KLineInit.FiveBaud, x.Init));
        Assert.Contains(order, x => x.Address == 0x33);
        Assert.Equal(order.Count, order.Distinct().Count());
    }

    [Fact]
    public void ApplyTiming_Live_ReachesInterface()
    {
        var (kwp, dev) = Make();
        kwp.Connect();
        kwp.ApplyTiming(new KwpTiming { P1Max = 7, P3Min = 3, P4Min = 1, P2Max = 80 });
        Assert.Equal((7, 0, 1), dev.Timing); // P3 só vai para o interface com EnforceP3Min
        Assert.Equal(80, kwp.Timing.P2Max);
        kwp.EnforceP3Min = true;
        kwp.ApplyTiming(new KwpTiming { P1Max = 5, P3Min = 3, P4Min = 0 });
        Assert.Equal((5, 3, 0), dev.Timing);
    }



    [Fact]
    public void TesterPresent_FallsBackToParameterForm_AndRemembers()
    {
        var (kwp, dev) = Make();
        int bare = 0;
        dev.Ecu = req => req.Data switch
        {
            [0x3E] => (bare++ >= 0 ? FakeFrameDevice.Frame(0x83, 0xF1, 0x10, 0x7F, 0x3E, 0x12) : null),   // recusa "3E"
            [0x3E, 0x01] => FakeFrameDevice.Frame(0x81, 0xF1, 0x10, 0x7E),
            _ => null,
        };
        kwp.Connect();
        kwp.TesterPresent();
        kwp.TesterPresent();
        Assert.True(kwp.TesterPresentWithParameter);
        Assert.Equal(1, bare); // só experimentou o "3E" uma vez
    }

    [Fact]
    public void Connect_RetriesAfterP3Max_WhenEcuStillInOldSession()
    {
        var (kwp, dev) = Make();
        kwp.FastInitAttemptsPerTarget = 1;
        kwp.RetryAfterP3Max = true;
        dev.FastInitReplies.Enqueue(Array.Empty<byte>());   // 1.ª: calada (ainda na sessão anterior)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        kwp.Connect();
        Assert.Equal(0x10, kwp.EcuAddress);
        Assert.Equal(2, dev.Written.Count);                 // duas tentativas
        Assert.True(sw.ElapsedMilliseconds >= 400);         // esperou antes da 2.ª
    }

    [Fact]
    public void Connect_WithoutRetry_FailsAtOnce()
    {
        var (kwp, dev) = Make();
        kwp.FastInitAttemptsPerTarget = 1;
        dev.FastInitReplies.Enqueue(Array.Empty<byte>());
        Assert.Throws<Kwp2000TimeoutException>(() => kwp.Connect());
    }

}
