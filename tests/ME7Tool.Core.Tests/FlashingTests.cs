using ME7Tool.Core.Flashing;
using ME7Tool.Core.Kwp2000;
using Xunit;

namespace ME7Tool.Core.Tests;

public class FlashingTests
{
    const byte Ecu = 0x11, Tester = 0xF1;

    private static byte[] Resp(params byte[] data)
        => new Kwp2000Message(Kwp2000AddressMode.Physical, Tester, Ecu, data).Encode();

    // ---------------- Layout ----------------

    [Fact]
    public void Layout_512k_HasCorrectTotalAndSectors()
    {
        var l = FlashLayout.Am29F400B_BottomBoot_512k();
        Assert.Equal(0x80000, l.TotalSize);
        Assert.Equal(11, l.Sectors.Count); // 16K+8K+8K+32K + 7x64K
        Assert.Equal(0x10000u, l.SectorAt(0x12345)!.Value.Offset); // dentro do 1º 64K
        Assert.Equal(0x0u, l.SectorAt(0x100)!.Value.Offset);       // boot sector
    }

    // ---------------- Diff ----------------

    [Fact]
    public void Diff_DetectsChangedSectorOnly()
    {
        var l = FlashLayout.Am29F400B_BottomBoot_512k();
        var orig = new byte[l.TotalSize];
        var mod = (byte[])orig.Clone();
        mod[0x10005] = 0xAA; // altera um byte no sector 0x10000

        var changed = FlashDiff.ChangedSectors(l, orig, mod);
        Assert.Single(changed);
        Assert.Equal(0x10000u, changed[0].Offset);
    }

    [Fact]
    public void Diff_DifferentSizes_Throws()
    {
        var l = FlashLayout.Am29F400B_BottomBoot_512k();
        Assert.Throws<ArgumentException>(() =>
            FlashDiff.Compare(l, new byte[10], new byte[20]));
    }

    // ---------------- Flasher ----------------

    /// <summary>ECU falsa segundo a spec 07223: 31/33 com routineNotComplete, 34 → 74 maxBlock.</summary>
    private sealed class FakeEcu
    {
        public List<uint> Downloads = new();
        public List<(uint Start, uint Stop)> Erases = new();
        public List<int> TransferSizes = new();
        public int PendingPolls = 2;          // nº de 33 com NRC 0x23 antes de concluir
        public byte MaxBlock = 0x81;          // maxNumberOfBlockLength
        public bool ZeroSeed;                 // ECU já desbloqueada
        public byte? ChecksumNrc;             // NRC à rotina 01
        public byte[]? ChecksumParams;        // parâmetros do 31 01 (início+fim+CRC-32 esperado)
        public byte? SecurityNrc;             // NRC ao pedido de seed (ex.: 0x22 imobilizador)
        public int BusyTransfers;             // nº de 36 a responder NRC 0x23 antes de aceitar
        public bool IromOnly;                 // só o programador da IROM (sem 0x3B)
        public byte? StartRoutineNrc;         // NRC ao 31 (ex.: 0x22 passagem à IROM recusada)
        public bool SilentFirst31;            // 1.º 31 sem resposta (a ECU "reinicia no programador")
        public int Connects;
        public byte[] TesterCode = "OLDTESTER1"u8.ToArray();
        public byte[]? ProgDate;              // resposta ao 1A 99 (BCD), null = não suportado
        public List<byte[]> Writes = new();   // pedidos 3B (por ordem)
        public List<byte> Sids = new();       // todos os SIDs recebidos (por ordem)
        public List<byte[]> DownloadReqs = new();   // pedidos 34 completos
        public List<byte> TransferBytes = new();    // dados de todos os 36, por ordem
        private int _polls;

        public byte[]? Respond(Kwp2000Message req)
        {
            var d = req.Data;
            Sids.Add(d[0]);
            switch ((Kwp2000Service)d[0])
            {
                case Kwp2000Service.StartDiagnosticSession: return Resp(0x50, d[1]);
                case Kwp2000Service.ReadECUIdentification:
                    return d[1] == 0x98 ? Resp(new byte[] { 0x5A, 0x98 }.Concat(TesterCode).ToArray())
                         : d[1] == 0x99 && ProgDate != null ? Resp(new byte[] { 0x5A, 0x99 }.Concat(ProgDate).ToArray())
                         : Resp(0x7F, 0x1A, 0x12);
                case Kwp2000Service.AccessTimingParameters:
                    return d[1] == 0 ? Resp(0xC3, 0x00, 0, 0xFF, 0, 0x14, 0) : Resp(0xC3, 0x03);
                case Kwp2000Service.SecurityAccess:
                    if (SecurityNrc is byte snrc) return Resp(0x7F, 0x27, snrc);
                    if ((d[1] & 1) == 1)
                        return ZeroSeed ? Resp(0x67, d[1], 0, 0, 0, 0) : Resp(0x67, d[1], 0x1A, 0x2B, 0x3C, 0x4D);
                    return Resp(0x67, d[1]);
                case Kwp2000Service.StartCommunication:
                    Connects++;
                    return new Kwp2000Message(Kwp2000AddressMode.Physical, Tester, Ecu, 0xC1, 0xEF, 0x8F).Encode();
                case Kwp2000Service.StopCommunication: return Resp(0xC2);
                case Kwp2000Service.StartRoutineByLocalIdentifier:
                    if (SilentFirst31) { SilentFirst31 = false; return null; }
                    if (StartRoutineNrc is byte rnrc) { StartRoutineNrc = null; return Resp(0x7F, 0x31, rnrc); }
                    if (d[1] == 0x02) Erases.Add((A(d, 2), A(d, 5)));
                    if (d[1] == 0x01) ChecksumParams = d[2..];
                    _polls = 0;
                    return Resp(0x71, d[1]);
                case Kwp2000Service.RequestRoutineResultsByLocalIdentifier:
                    if (d[1] == 0x01 && ChecksumNrc is byte nrc) return Resp(0x7F, 0x33, nrc);
                    if (_polls++ < PendingPolls) return Resp(0x7F, 0x33, 0x23);
                    return Resp(0x73, d[1], 0x00);
                case Kwp2000Service.RequestDownload:
                    Downloads.Add(A(d, 1));
                    DownloadReqs.Add(d);
                    return Resp(0x74, MaxBlock);
                case Kwp2000Service.TransferData:
                    if (BusyTransfers > 0) { BusyTransfers--; return Resp(0x7F, 0x36, 0x23); }
                    TransferSizes.Add(d.Length - 1);
                    TransferBytes.AddRange(d.Skip(1));
                    return Resp(0x76);
                case Kwp2000Service.RequestTransferExit: return Resp(0x77);
                case Kwp2000Service.TesterPresent: return Resp(0x7E);
                case Kwp2000Service.WriteDataByLocalIdentifier:
                    if (IromOnly) return Resp(0x7F, 0x3B, 0x11);
                    Writes.Add(d); return Resp(0x7B, d[1]);
                default: return Resp(0x7F, d[0], 0x11);
            }
        }

        private static uint A(byte[] d, int i) => (uint)(d[i] << 16 | d[i + 1] << 8 | d[i + 2]);
    }

    private static (Flasher flasher, FakeCommunicationDevice dev, FakeEcu ecu) MakeFlasher(FlashOptions? opt = null)
    {
        var dev = new FakeCommunicationDevice(); dev.Open();
        var ecu = new FakeEcu();
        dev.AutoResponder = ecu.Respond;
        var timing = new KwpTiming { P4Min = 0, ResponseTimeoutMs = 200, InterByteTimeoutMs = 50 };
        var kwp = new Kwp2000Interface(dev, timing, Ecu, Tester)
        {
            FastInitTargets = new[] { Ecu }, FastInitIdleMs = 0, FastInitResponseTimeoutMs = 50,
        };
        opt ??= new FlashOptions();
        opt.RoutinePollMs = 1;
        opt.DownloadSettleMs = 0;
        var flasher = new Flasher(kwp, FlashLayout.Am29F400B_BottomBoot_512k(), opt);
        return (flasher, dev, ecu);
    }

    [Fact]
    public void ProgramPartial_ErasesThenWritesOnlyChangedSector()
    {
        var (flasher, _, ecu) = MakeFlasher();
        var orig = new byte[0x80000];
        var mod = (byte[])orig.Clone();
        mod[0x10005] = 0xAA; // altera o sector 0x10000

        var zones = flasher.ProgramPartial(orig, mod);

        Assert.Single(zones);
        Assert.Equal(new FlashZone(0x10000, 0x10000), zones[0]);
        Assert.Equal(new[] { (0x810000u, 0x81FFFFu) }, ecu.Erases);   // apagar com fim inclusivo
        Assert.Equal(new[] { 0x810000u }, ecu.Downloads);
        Assert.All(ecu.TransferSizes, n => Assert.True(n <= 0x80));    // maxNumberOfBlockLength 0x81 − 1
        Assert.Equal(0x10000, ecu.TransferSizes.Sum());
    }

    [Fact]
    public void AdjacentChangedSectors_Merge_ButNotAcrossDataSectionBoundaries()
    {
        // 0x50000+0x60000 juntam-se; 0x10000 (data section) fica sozinho e o 0x20000 começa zona nova.
        var (flasher, _, ecu) = MakeFlasher();
        var orig = new byte[0x80000];
        var mod = (byte[])orig.Clone();
        mod[0x10000] = 1; mod[0x20000] = 1; mod[0x50000] = 1; mod[0x60000] = 1;
        var zones = flasher.ProgramPartial(orig, mod);
        Assert.Equal(new[]
        {
            new FlashZone(0x10000, 0x10000), new FlashZone(0x20000, 0x10000), new FlashZone(0x50000, 0x20000),
        }, zones);
        Assert.Equal(new[] { (0x810000u, 0x81FFFFu), (0x820000u, 0x82FFFFu), (0x850000u, 0x86FFFFu) }, ecu.Erases);
    }

    [Fact]
    public void Sector8000_Changed_AlsoRewritesSector30000()
    {
        // Ao apagar o 0x8000 a ECU usa o 0x30000 como backup (apaga-o) — tem de ser regravado.
        var (flasher, _, ecu) = MakeFlasher();
        var orig = new byte[0x80000];
        var mod = (byte[])orig.Clone();
        mod[0x8000] = 1;
        var zones = flasher.ProgramPartial(orig, mod);
        Assert.Equal(new[] { new FlashZone(0x8000, 0x8000), new FlashZone(0x30000, 0x10000) }, zones);
        Assert.Equal(new[] { 0x808000u, 0x830000u }, ecu.Downloads); // 0x8000 primeiro, depois o backup
    }

    [Fact]
    public void Identification_IsWrittenBeforeFirstErase_TesterThenDate()
    {
        var (flasher, _, ecu) = MakeFlasher();
        var orig = new byte[0x80000];
        var mod = (byte[])orig.Clone(); mod[0x50000] = 1;
        flasher.ProgramPartial(orig, mod, new DateTime(2026, 11, 15), "NEWCODE");

        int i98 = ecu.Writes.FindIndex(w => w[1] == 0x98), i99 = ecu.Writes.FindIndex(w => w[1] == 0x99);
        Assert.True(i98 >= 0 && i99 > i98);                                       // 98 antes do 99
        Assert.Equal(12, ecu.Writes[i98].Length);                                // 3B 98 + 10 bytes
        Assert.Equal("NEWCODE   "u8.ToArray(), ecu.Writes[i98][2..]);
        Assert.Equal(new byte[] { 0x3B, 0x99, 0x20, 0x26, 0x11, 0x15 }, ecu.Writes[i99]);
        int last3B = ecu.Sids.LastIndexOf(0x3B), first31 = ecu.Sids.IndexOf(0x31);
        Assert.True(last3B < first31);                                           // tudo antes do apagar
    }

    [Fact]
    public void NoTesterCode_KeepsTheCurrentOne_ReadFromEcu()
    {
        var (flasher, _, ecu) = MakeFlasher();
        var orig = new byte[0x80000];
        var mod = (byte[])orig.Clone(); mod[0x50000] = 1;
        flasher.ProgramPartial(orig, mod);
        var w98 = ecu.Writes.Single(w => w[1] == 0x98);
        Assert.Equal(ecu.TesterCode, w98[2..]);
        Assert.Contains(ecu.Writes, w => w[1] == 0x99); // data de hoje
        Assert.True(ecu.Sids.IndexOf(0x1A) < ecu.Sids.IndexOf(0x10)); // lido antes da sessão 0x85
    }

    [Fact]
    public void IromOnly_No3B_SkipsIdentification_AndStillFlashes()
    {
        // Depois de um flash interrompido a ECU fica só com a IROM, que não tem 0x3B.
        var (flasher, _, ecu) = MakeFlasher();
        ecu.IromOnly = true;
        var orig = new byte[0x80000];
        var mod = (byte[])orig.Clone(); mod[0x50000] = 1;
        var logs = new List<string>(); flasher.OnLog = logs.Add;
        flasher.ProgramPartial(orig, mod);
        Assert.Empty(ecu.Writes);
        Assert.Equal(new[] { 0x850000u }, ecu.Downloads);
        Assert.Contains(logs, l => l.Contains("programador da IROM"));
    }

    [Fact]
    public void StartRoutine_Nrc22_WithDiagnostic_ForgetsSession_RetryRedoesIdentification()
    {
        // Com o diagnóstico automático (que pode religar em 0x86) a nova tentativa refaz 10 85/27/3B.
        var (flasher, _, ecu) = MakeFlasher();
        ecu.StartRoutineNrc = 0x22;
        var orig = new byte[0x80000];
        var mod = (byte[])orig.Clone(); mod[0x50000] = 1;
        Assert.Throws<InvalidOperationException>(() => flasher.ProgramPartial(orig, mod));
        int writes = ecu.Writes.Count;
        flasher.ProgramPartial(orig, mod);
        Assert.True(ecu.Writes.Count > writes);                       // 3B 98/99 outra vez
        Assert.Equal(new[] { (0x850000u, 0x85FFFFu) }, ecu.Erases);
    }

    [Fact]
    public void StartRoutine_Nrc22_ExplainsAndRetryGoesStraightToErase()
    {
        // Sem diagnóstico: a 2.ª tentativa já não repete sessão/segurança/3B e vai direta ao 31 02.
        var (flasher, _, ecu) = MakeFlasher(new FlashOptions { DiagnoseOnHandoverFailure = false });
        ecu.StartRoutineNrc = 0x22;
        var orig = new byte[0x80000];
        var mod = (byte[])orig.Clone(); mod[0x50000] = 1;
        var ex = Assert.Throws<InvalidOperationException>(() => flasher.ProgramPartial(orig, mod));
        Assert.Contains("programador", ex.Message);
        Assert.Empty(ecu.Erases);

        int writes = ecu.Writes.Count, sids = ecu.Sids.Count;
        flasher.ProgramPartial(orig, mod);
        Assert.Equal(writes, ecu.Writes.Count);                       // sem novo 3B
        Assert.Equal((byte)0x31, ecu.Sids[sids]);                     // primeiro pedido = 31
        Assert.Equal(new[] { (0x850000u, 0x85FFFFu) }, ecu.Erases);
    }

    /// <summary>Instrução que se "confirma" sozinha depois de algumas perguntas (simula o OK do utilizador).</summary>
    private static Func<string, Func<bool>> AutoConfirm(List<string> shown, int afterPolls = 2)
        => text => { shown.Add(text); int n = 0; return () => ++n > afterPolls; };

    [Fact]
    public void IgnitionOffHandover_AsksToSwitchOff_KeepsAliveWithTesterPresent_ThenErases()
    {
        var (flasher, _, ecu) = MakeFlasher(new FlashOptions { IgnitionOffHandover = true });
        var shown = new List<string>();
        flasher.Instruct = AutoConfirm(shown);
        var orig = new byte[0x80000];
        var mod = (byte[])orig.Clone(); mod[0x50000] = 1;
        flasher.ProgramPartial(orig, mod);

        Assert.Single(shown);
        Assert.Contains("Desliga AGORA a ignição", shown[0]);
        int last3B = ecu.Sids.LastIndexOf(0x3B), first31 = ecu.Sids.IndexOf(0x31);
        Assert.Contains(ecu.Sids.Skip(last3B).Take(first31 - last3B), s => s == 0x3E); // TesterPresent na espera
        Assert.Equal(new[] { (0x850000u, 0x85FFFFu) }, ecu.Erases);
    }

    [Fact]
    public void IgnitionOffHandover_SilentAfter31_AsksToSwitchOn_ReconnectsAndContinues()
    {
        var (flasher, _, ecu) = MakeFlasher(new FlashOptions { IgnitionOffHandover = true, HandoverTimeoutMs = 300 });
        ecu.SilentFirst31 = true;                       // a ECU reinicia no programador e não responde
        var shown = new List<string>();
        flasher.Instruct = AutoConfirm(shown);
        var orig = new byte[0x80000];
        var mod = (byte[])orig.Clone(); mod[0x50000] = 1;
        flasher.ProgramPartial(orig, mod);

        Assert.Equal(2, shown.Count);
        Assert.Contains("Volta a LIGAR a ignição", shown[1]);
        Assert.Equal(1, ecu.Connects);                  // voltou a ligar-se
        Assert.Equal(new[] { (0x850000u, 0x85FFFFu) }, ecu.Erases);
        Assert.Equal(new[] { 0x850000u }, ecu.Downloads);
    }

    [Fact]
    public void TwoStepKeyCycle_Step1_StopComm_KeyOffOn_Reconnect_Step2_ThenErase()
    {
        var (flasher, _, ecu) = MakeFlasher(new FlashOptions { TwoStepKeyCycle = true, KeyOffWaitSeconds = 0 });
        var shown = new List<string>();
        flasher.Instruct = AutoConfirm(shown);
        var orig = new byte[0x80000];
        var mod = (byte[])orig.Clone(); mod[0x50000] = 1;
        flasher.ProgramPartial(orig, mod);

        Assert.Equal(2, shown.Count);
        Assert.Contains("Desliga AGORA a chave", shown[0]);
        Assert.Contains("Liga AGORA a chave", shown[1]);
        Assert.Equal(1, ecu.Connects);                               // ligação nova para o passo 2
        int stop = ecu.Sids.IndexOf(0x82), first31 = ecu.Sids.IndexOf(0x31);
        Assert.True(stop > ecu.Sids.IndexOf(0x3B));                 // StopComm depois do passo 1
        var step2 = ecu.Sids.Skip(stop + 1).Take(first31 - stop - 1).ToList();
        Assert.Contains((byte)0x81, step2);                          // StartComm
        Assert.Contains((byte)0x10, step2);                          // 10 85
        Assert.Contains((byte)0x27, step2);                          // seed/key outra vez
        Assert.DoesNotContain((byte)0x3E, step2);                    // sem TesterPresent na chave OFF
        Assert.Equal(new[] { (0x850000u, 0x85FFFFu) }, ecu.Erases);
    }

    [Fact]
    public void NoTimingNegotiation_SendsNo83()
    {
        var (flasher, _, ecu) = MakeFlasher(new FlashOptions { NegotiateTiming = false });
        var orig = new byte[0x80000];
        var mod = (byte[])orig.Clone(); mod[0x50000] = 1;
        flasher.ProgramPartial(orig, mod);
        Assert.DoesNotContain((byte)0x83, ecu.Sids);
    }

    [Fact]
    public void PauseBeforeErase_SendsTesterPresentBetweenIdentificationAndErase()
    {
        var (flasher, _, ecu) = MakeFlasher(new FlashOptions { PauseBeforeEraseMs = 1200 });
        var orig = new byte[0x80000];
        var mod = (byte[])orig.Clone(); mod[0x50000] = 1;
        flasher.ProgramPartial(orig, mod);
        int last3B = ecu.Sids.LastIndexOf(0x3B), first31 = ecu.Sids.IndexOf(0x31);
        int tp = ecu.Sids.FindIndex(last3B, s => s == 0x3E);
        Assert.True(tp > last3B && tp < first31);
    }

    [Fact]
    public void TestProgrammingAccess_Twice_DoesNotPretend()
    {
        var (flasher, dev, _) = MakeFlasher();
        string? last = null;
        flasher.Progress += p => last = p.Message;
        flasher.TestProgrammingAccess();
        int frames = dev.WrittenFrames.Count;
        flasher.TestProgrammingAccess();
        Assert.Equal(frames, dev.WrittenFrames.Count); // não falou com a ECU
        Assert.Contains("Já em sessão de programação", last);
    }

    [Fact]
    public void Security_Nrc22_ExplainsImmobilizer()
    {
        var (flasher, _, ecu) = MakeFlasher();
        ecu.SecurityNrc = 0x22;
        var ex = Assert.Throws<InvalidOperationException>(() => flasher.TestProgrammingAccess());
        Assert.Contains("imobilizador", ex.Message);
    }

    [Fact]
    public void TransferData_Busy_IsRetried()
    {
        var (flasher, _, ecu) = MakeFlasher();
        ecu.BusyTransfers = 2;
        var orig = new byte[0x80000];
        var mod = (byte[])orig.Clone(); mod[0x50000] = 1;
        flasher.ProgramPartial(orig, mod);
        Assert.Equal(0x10000, ecu.TransferSizes.Sum()); // tudo aceite apesar dos 2 NRC 0x23
    }

    [Fact]
    public void BlockSize_IsAlwaysEven()
    {
        var (flasher, _, ecu) = MakeFlasher();
        ecu.MaxBlock = 0x80; // 0x80 − 1 = 0x7F (ímpar) → tem de descer para 0x7E
        var orig = new byte[0x80000];
        var mod = (byte[])orig.Clone(); mod[0x50000] = 1;
        flasher.ProgramPartial(orig, mod);
        Assert.All(ecu.TransferSizes, n => Assert.True(n % 2 == 0 && n <= 0x7E));
    }

    [Fact]
    public void ProgramPartial_BootSectorDiff_IsRefusedWhenProtected()
    {
        var (flasher, dev, _) = MakeFlasher();
        var orig = new byte[0x80000];
        var mod = (byte[])orig.Clone();
        mod[0x100] = 0x55;
        Assert.Throws<InvalidOperationException>(() => flasher.ProgramPartial(orig, mod));
        Assert.Empty(dev.WrittenFrames); // nem sequer entrou na sessão
    }

    [Fact]
    public void ProgramPartial_BootSector_AllowedWhenProtectionOff()
    {
        var (flasher, _, ecu) = MakeFlasher(new FlashOptions { ProtectBootSector = false });
        var orig = new byte[0x80000];
        var mod = (byte[])orig.Clone();
        mod[0x100] = 0x55;
        flasher.ProgramPartial(orig, mod);
        Assert.Equal(new[] { 0x800000u }, ecu.Downloads);
    }

    [Fact]
    public void ProgramPartial_NoDiff_FlashesNothing()
    {
        var (flasher, dev, _) = MakeFlasher();
        var buf = new byte[0x80000];
        Assert.Empty(flasher.ProgramPartial(buf, (byte[])buf.Clone()));
        Assert.Empty(dev.WrittenFrames);
    }

    [Fact]
    public void ProgramFull_WrongImageSize_Throws()
    {
        var (flasher, _, _) = MakeFlasher();
        Assert.Throws<ArgumentException>(() => flasher.ProgramFull(new byte[100]));
    }

    [Fact]
    public void ProgramFull_SkipsBootSector_SplitsAtDataSection()
    {
        var (flasher, _, ecu) = MakeFlasher();
        var phases = new List<FlashPhase>();
        flasher.Progress += p => phases.Add(p.Phase);

        var zones = flasher.ProgramFull(new byte[0x80000], new DateTime(2024, 11, 14), "TESTER01");

        // A data section (0x10000) é apagada sozinha; o resto não a atravessa.
        Assert.Equal(new[] { new FlashZone(0x4000, 0xC000), new FlashZone(0x10000, 0x10000), new FlashZone(0x20000, 0x60000) }, zones);
        Assert.Equal(new[] { (0x804000u, 0x80FFFFu), (0x810000u, 0x81FFFFu), (0x820000u, 0x87FFFFu) }, ecu.Erases);
        Assert.Equal(new[] { 0x804000u, 0x810000u, 0x820000u }, ecu.Downloads);
        Assert.Contains(FlashPhase.Erasing, phases);
        Assert.Contains(FlashPhase.Done, phases);
        Assert.Equal((byte)0x37, ecu.Sids[^1]); // nada depois do último 37 (sem 3B no fim)
    }

    [Fact]
    public void ZeroSeed_MeansAlreadyUnlocked_NoKeySent()
    {
        var (flasher, dev, ecu) = MakeFlasher();
        ecu.ZeroSeed = true;
        flasher.TestProgrammingAccess();
        var sec = dev.WrittenFrames.Select(f => Kwp2000Message.TryParse(f)!).Where(m => m.Data[0] == 0x27).ToList();
        Assert.Single(sec); // só o pedido de seed
    }

    [Fact]
    public void TestProgrammingAccess_WritesNothing()
    {
        var (flasher, dev, _) = MakeFlasher();
        flasher.TestProgrammingAccess();
        var sids = dev.WrittenFrames.Select(f => Kwp2000Message.TryParse(f)!.Data[0]).ToHashSet();
        Assert.DoesNotContain((byte)0x31, sids);
        Assert.DoesNotContain((byte)0x34, sids);
        Assert.DoesNotContain((byte)0x36, sids);
        Assert.DoesNotContain((byte)0x3B, sids); // nem tester code nem data
        Assert.Contains((byte)0x27, sids);
    }

    [Fact]
    public void Erase_ThatNeverFinishes_TimesOut_WithoutWriting()
    {
        var (flasher, _, ecu) = MakeFlasher(new FlashOptions { RoutineTimeoutMs = 50 });
        ecu.PendingPolls = int.MaxValue;
        var orig = new byte[0x80000];
        var mod = (byte[])orig.Clone(); mod[0x10000] = 1;
        Assert.Throws<TimeoutException>(() => flasher.ProgramPartial(orig, mod));
        Assert.Empty(ecu.Downloads); // não escreveu sem apagar
    }

    [Fact]
    public void Checksum_Nrc_IsWarningByDefault_ErrorWhenConfigured()
    {
        var orig = new byte[0x80000];
        var mod = (byte[])orig.Clone(); mod[0x10000] = 1;

        var logs = new List<string>();
        var (f1, _, e1) = MakeFlasher(new FlashOptions { VerifyChecksum = true });
        e1.ChecksumNrc = 0x22; f1.OnLog = logs.Add;
        f1.ProgramPartial(orig, mod);
        Assert.Contains(logs, l => l.StartsWith("AVISO CRC-32"));

        var (f2, _, e2) = MakeFlasher(new FlashOptions { VerifyChecksum = true, ChecksumFailureIsError = true });
        e2.ChecksumNrc = 0x22;
        Assert.Throws<InvalidOperationException>(() => f2.ProgramPartial(orig, mod));
    }

    [Fact]
    public void Verify_SendsAddressRangeAndCrc32BigEndian()
    {
        // Zona 0x10000–0x1FFFF (sector de 64K): 31 01 <início 3> <fim inclusivo 3> <CRC-32 4 be>.
        var orig = new byte[0x80000];
        var mod = (byte[])orig.Clone(); mod[0x10000] = 1;
        var (flasher, _, ecu) = MakeFlasher(new FlashOptions { VerifyChecksum = true });
        flasher.ProgramPartial(orig, mod);

        uint baseAddr = 0x800000;
        uint crc = Flasher.Crc32(mod.AsSpan(0x10000, 0x10000));
        var expected = new byte[]
        {
            (byte)((baseAddr + 0x10000) >> 16), (byte)((baseAddr + 0x10000) >> 8), (byte)(baseAddr + 0x10000),
            (byte)((baseAddr + 0x1FFFF) >> 16), (byte)((baseAddr + 0x1FFFF) >> 8), (byte)(baseAddr + 0x1FFFF),
            (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc,
        };
        Assert.Equal(expected, ecu.ChecksumParams);
    }

    [Theory]
    [InlineData(new byte[0], 0x00000000u)]                                    // vazio
    [InlineData(new byte[] { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39 }, 0xCBF43926u)] // "123456789"
    public void Crc32_MatchesKnownVectors(byte[] data, uint expected)
        => Assert.Equal(expected, Flasher.Crc32(data));

    [Fact]
    public void ProgramFiatFull_SendsExaminerSequence_WithEncryptedImage()
    {
        var (flasher, _, ecu) = MakeFlasher();
        ecu.MaxBlock = 0xFD; // como o programador da Fiat (74 FD)
        var img = new byte[0x80000];
        new Random(42).NextBytes(img);
        flasher.ProgramFiatFull(img, FiatCipher.DefaultKey, new DateTime(2026, 11, 15), "TESTER");

        Assert.Equal(new[] { (0x800000u, 0x87FFFFu) }, ecu.Erases);                       // 31 02 80 00 00 87 FF FF
        Assert.Single(ecu.DownloadReqs);
        Assert.Equal("3480000001080000", Convert.ToHexString(ecu.DownloadReqs[0]));       // formato 0x01, 512 KB
        Assert.All(ecu.TransferSizes, n => Assert.True(n <= 252 && n % 2 == 0));
        var prepared = FiatFullFlash.Prepare(img);
        Assert.Equal(prepared, FiatCipher.DecryptAll(ecu.TransferBytes.ToArray()));       // a ECU desencripta e obtém a imagem
        ushort cs = FiatFullFlash.Checksum(prepared);
        Assert.Equal(new byte[] { 0x80, 0, 0, 0x87, 0xFF, 0xFF, (byte)(cs >> 8), (byte)cs }, ecu.ChecksumParams);
        // identificação (3B) antes do apagar, como a ECU exige
        int firstErase = ecu.Sids.IndexOf(0x31), lastWrite = ecu.Sids.LastIndexOf(0x3B);
        Assert.True(lastWrite >= 0 && lastWrite < firstErase);
    }

    [Fact]
    public void ProgramFiatFull_Me31_Uses4000RangeAndLowChecksum()
    {
        var (flasher, _, ecu) = MakeFlasher();
        ecu.MaxBlock = 0xFD;
        var img = new byte[0x80000];
        new Random(7).NextBytes(img);
        flasher.ProgramFiatFull(img, FiatCipher.DefaultKey, new DateTime(2026, 11, 15), "TESTER",
            profile: FiatFlashProfile.Me31);

        Assert.Equal(new[] { (0x004000u, 0x07FFFFu) }, ecu.Erases);                        // 31 02 00 40 00 07 FF FF (arranque intacto)
        Assert.Equal("340040000107C000", Convert.ToHexString(ecu.DownloadReqs[0]));        // 34 00 40 00 01 07 C0 00
        var prepared = FiatFlashProfile.Me31.Prepare(img);                                 // = 0x4000..0x7FFFF
        Assert.Equal(0x7C000, prepared.Length);
        Assert.Equal(prepared, FiatCipher.DecryptAll(ecu.TransferBytes.ToArray()));
        ushort cs = FiatFlashProfile.Me31.ChecksumOf(prepared);                            // metade de BAIXO
        Assert.Equal((ushort)(Flasher.Crc32(prepared) & 0xFFFF), cs);
        Assert.Equal(new byte[] { 0x00, 0x40, 0x00, 0x07, 0xFF, 0xFF, (byte)(cs >> 8), (byte)cs }, ecu.ChecksumParams);
    }

    [Fact]
    public void ProgramFiatFull_ChecksumRefused_IsAnError()
    {
        var (flasher, _, ecu) = MakeFlasher();
        ecu.ChecksumNrc = 0x10;
        var ex = Assert.Throws<InvalidOperationException>(() => flasher.ProgramFiatFull(new byte[0x80000]));
        Assert.Contains("checksum", ex.Message);
    }

    [Fact]
    public void KeepEcuIdentification_RewritesTesterCodeAndDateReadFromTheEcu()
    {
        var (flasher, _, ecu) = MakeFlasher(new FlashOptions { KeepEcuIdentification = true });
        ecu.MaxBlock = 0xFD;
        ecu.ProgDate = new byte[] { 0x20, 0x24, 0x03, 0x15 };
        flasher.ProgramFiatFull(new byte[0x80000], null, new DateTime(2026, 11, 15), "IGNORADO");
        Assert.Equal("3B98" + Convert.ToHexString("OLDTESTER1"u8.ToArray()), Convert.ToHexString(ecu.Writes[0]));
        Assert.Equal("3B9920240315", Convert.ToHexString(ecu.Writes[1]));
    }
}
