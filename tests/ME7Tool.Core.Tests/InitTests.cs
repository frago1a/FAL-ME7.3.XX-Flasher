using ME7Tool.Core.Kwp2000;
using ME7Tool.Core.Transport;
using Xunit;

namespace ME7Tool.Core.Tests;

public class InitTests
{
    private static (Kwp2000Interface kwp, FakeCommunicationDevice dev) Make(params byte[] targets)
    {
        var dev = new FakeCommunicationDevice();
        var kwp = new Kwp2000Interface(dev, new KwpTiming { P4Min = 0, ResponseTimeoutMs = 100, InterByteTimeoutMs = 30 })
        {
            FastInitTargets = targets,
            FastInitIdleMs = 0,
            FastInitResponseTimeoutMs = 50,
            FiveBaudBitMs = 1,
        };
        return (kwp, dev);
    }

    [Fact]
    public void SlowInit_SendsAddressAt5Baud_ReadsKeyBytes_AnswersInvertedKb2()
    {
        var (kwp, dev) = Make(0x10);
        dev.RxAfterDiscard.Enqueue(new byte[] { 0x00, 0x55, 0xEF, 0x8F }); // lixo + sync + KB1 KB2
        dev.EnqueueResponse(new byte[] { 0xEF });                          // ~0x10 após o ~KB2

        kwp.Connect(KLineInit.FiveBaud);

        Assert.Equal(new byte[] { 0xEF, 0x8F }, kwp.KeyBytes);
        Assert.Equal(0x10, kwp.EcuAddress);
        // repouso alto, start (baixo), 0x10 LSB primeiro (0,0,0,0,1,0,0,0), stop (alto)
        Assert.Equal(new[] { false, true, true, true, true, true, false, true, true, true, false }, dev.BreakLog);
        Assert.Equal(new byte[] { 0x70 }, dev.WrittenFrames.Single()); // ~0x8F
    }

    [Fact]
    public void SlowInit_WrongComplement_FailsAllTargets()
    {
        var (kwp, dev) = Make(0x33);
        dev.RxAfterDiscard.Enqueue(new byte[] { 0x55, 0xEF, 0x8F });
        dev.EnqueueResponse(new byte[] { 0x12 }); // não é ~0x33
        Assert.Throws<Kwp2000TimeoutException>(() => kwp.Connect(KLineInit.FiveBaud));
    }

    [Fact]
    public void FastInit_FirstAttemptSilent_SecondAttemptConnects()
    {
        var (kwp, dev) = Make(0x10);
        int calls = 0;
        dev.AutoResponder = req => ++calls == 1
            ? null
            : new Kwp2000Message(Kwp2000AddressMode.Physical, 0xF1, 0x10, 0xC1, 0xEF, 0x8F).Encode();

        kwp.Connect();

        Assert.Equal(2, calls);
        Assert.Equal(0x10, kwp.EcuAddress);
        Assert.Equal(new byte[] { 0xEF, 0x8F }, kwp.KeyBytes);
    }

    [Fact]
    public void FastInit_OnlyGivenTarget_SingleAttempt_Throws()
    {
        var (kwp, dev) = Make(0x33);
        kwp.FastInitAttemptsPerTarget = 1;
        Assert.Throws<Kwp2000TimeoutException>(() => kwp.Connect());
        Assert.Single(dev.WrittenFrames);
        Assert.Equal(0x33, Kwp2000Message.TryParse(dev.WrittenFrames[0])!.Target);
    }
}
