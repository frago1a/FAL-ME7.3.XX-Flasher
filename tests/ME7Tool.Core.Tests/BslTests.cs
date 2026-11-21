using ME7Tool.Core.Bsl;
using ME7Tool.Core.Transport;

namespace ME7Tool.Core.Tests;

/// <summary>
/// ECU falsa em modo boot (C167 + kernel Minimon) para testar o BSL sem hardware: eco de cada byte (K-line), entrada
/// com loader e kernel, comandos do Minimon e um driver de EEPROM simulado. A flash e a IROM só aparecem com os
/// registos de configuração certos; a EEPROM só responde com o chip-select certo.
/// </summary>
internal sealed class FakeBootEcu : ICommunicationDevice
{
    private readonly Queue<byte> _rx = new();
    private readonly List<byte> _args = new();
    private enum State { Boot, Loader, Kernel, Command }
    private State _state = State.Boot;
    private byte _cmd;
    private byte _lastChecksum;

    public byte[] Memory { get; } = new byte[0x1000000];
    public byte[] Flash { get; } = new byte[0x80000];
    public byte[] InternalRom { get; } = new byte[0x8000];
    public byte[] Eeprom { get; } = new byte[0x400];
    public (int Port, int Pin) EepromChipSelect { get; set; } = (4, 7);
    /// <summary>Registo de estado da EEPROM: 95080 normal = 00 (como na Fiat ME3.1), 95040 = F0.</summary>
    public byte EepromStatus { get; set; } = 0x00;
    public bool EepromWriteProtectPin { get; set; }
    public int EepromBytesWritten { get; private set; }
    public bool CorruptNextBlockChecksum { get; set; }
    public byte Variant { get; set; } = 0xC5;

    public string Name => "FakeBoot";
    public bool IsOpen { get; private set; }
    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;
    public void Dispose() { }
    public void SetBaudRate(int baudRate) { }
    public void SetBreak(bool enabled) { }
    public void DiscardBuffers() => _rx.Clear();

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        int n = 0;
        while (n < buffer.Length && _rx.Count > 0) buffer[n++] = _rx.Dequeue();
        return n;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            _rx.Enqueue(b);   // eco da K-line
            Feed(b);
        }
    }

    private ushort Word(uint a) => (ushort)(Memory[a] | Memory[a + 1] << 8);
    private void Reply(params byte[] bytes) { foreach (var b in bytes) _rx.Enqueue(b); }

    private void Feed(byte b)
    {
        switch (_state)
        {
            case State.Boot:
                if (b == 0x00) { Reply(Variant); _state = State.Loader; }
                break;
            case State.Loader:
                _args.Add(b);
                if (_args.Count == BslResources.Loader.Length) { _args.Clear(); Reply(0x01); _state = State.Kernel; }
                break;
            case State.Kernel:
                _args.Add(b);
                if (_args.Count == BslResources.Kernel.Length) { _args.Clear(); Reply(0x03); _state = State.Command; }
                break;
            case State.Command:
                if (_cmd == 0) StartCommand(b);
                else { _args.Add(b); TryFinishCommand(); }
                break;
        }
    }

    private void StartCommand(byte c)
    {
        switch (c)
        {
            case 0x93: Reply(0xAA, 0xEA); break;
            case 0x33: Reply(0xAA, _lastChecksum, 0xEA); break;
            case 0x82 or 0xCD or 0x85 or 0x84 or 0x9F: Reply(0xAA); _cmd = c; _args.Clear(); break;
        }
    }

    private void TryFinishCommand()
    {
        uint addr = _args.Count >= 3 ? (uint)(_args[0] | _args[1] << 8 | _args[2] << 16) : 0;
        switch (_cmd)
        {
            case 0x82 when _args.Count == 5:
                Memory[addr] = _args[3]; Memory[addr + 1] = _args[4];
                Done(0xEA);
                break;
            case 0xCD when _args.Count == 3:
                Done(Memory[addr], Memory[addr + 1], 0xEA);
                break;
            case 0x85 when _args.Count == 5:
            {
                int len = _args[3] | _args[4] << 8;
                var data = new byte[len];
                for (int i = 0; i < len; i++) data[i] = ReadMapped(addr + (uint)i);
                _lastChecksum = Xor(data);
                if (CorruptNextBlockChecksum) { _lastChecksum ^= 0xFF; CorruptNextBlockChecksum = false; }
                Done(data.Append((byte)0xEA).ToArray());
                break;
            }
            case 0x84 when _args.Count >= 5 && _args.Count == 5 + (_args[3] | _args[4] << 8):
            {
                var data = _args.Skip(5).ToArray();
                data.CopyTo(Memory, addr);
                _lastChecksum = Xor(data);
                Done(0xEA);
                break;
            }
            case 0x9F when _args.Count == 19:
            {
                var r = new ushort[8];
                for (int i = 0; i < 8; i++) r[i] = (ushort)(_args[3 + 2 * i] | _args[4 + 2 * i] << 8);
                if (addr == 0xF640 && DriverLoaded(BslResources.Flash29FDriver)) RunFlashDriver(r);
                else if (addr == 0xF640) RunEepromDriver(r);
                var outp = new List<byte>();
                foreach (var w in r) { outp.Add((byte)w); outp.Add((byte)(w >> 8)); }
                outp.Add(0xEA);
                Done(outp.ToArray());
                break;
            }
        }
    }

    private void Done(params byte[] reply) { Reply(reply); _cmd = 0; _args.Clear(); }

    /// <summary>A flash só aparece em 0x800000 com a configuração de leitura; a IROM em 0x10000 com SYSCON 0xF604.</summary>
    private byte ReadMapped(uint a)
    {
        ushort syscon = Word(0xFF12), buscon0 = Word(0xFF0C);
        if (a >= 0x800000 && a < 0x880000)
            return syscon == 0xE204 && buscon0 == 0x04AD ? Flash[a - 0x800000] : (byte)0xFF;
        if (a >= 0x010000 && a < 0x018000)
            return syscon == 0xF604 ? InternalRom[a - 0x010000] : (byte)0xFF;
        return Memory[a];
    }

    private void RunEepromDriver(ushort[] r)
    {
        // o driver tem de estar carregado e com o chip-select acertado para o pino certo (BSET Px.y em 0xF60C)
        byte port8 = EepromChipSelect.Port switch { 2 => 0xE0, 3 => 0xE2, 4 => 0xE4, 6 => 0xE6, 7 => 0xE8, _ => 0xEA };
        ushort wantSetCs = (ushort)(((EepromChipSelect.Pin << 4) | 0xF) | (port8 << 8));
        bool driver = Memory[0xF610] == BslResources.Eeprom95Driver[0x10];
        bool csOk = driver && Word(0xF60C) == wantSetCs;
        // chip-select errado: ninguém responde e a linha de dados fica a 0 (estado 00, dados a 00)
        switch (r[0])
        {
            case 0x0093:
                r[1] = csOk ? EepromStatus : (ushort)0x00;
                break;
            case 0x0036:
                int len = r[1], off = r[2];
                if (csOk) Array.Copy(Eeprom, off, Memory, r[5], len);
                else Array.Clear(Memory, r[5], len);
                r[7] = 0;
                break;
            case 0x0037:   // escrita byte a byte (com o pino WP ativo a EEPROM ignora-a, sem erro)
                if (csOk && !EepromWriteProtectPin) Array.Copy(Memory, r[5], Eeprom, r[2], r[1]);
                EepromBytesWritten += r[1];
                r[7] = 0;
                break;
        }
    }

    private bool DriverLoaded(byte[] driver) => Memory.AsSpan(0xF600, driver.Length).SequenceEqual(driver);

    // --- driver da flash AMD 29Fx00B (A29fx00Bx.a66) ---
    public ushort FlashDevice { get; set; } = 0x22AB;
    /// <summary>Se verdadeiro, o apagar não deixa a flash a FF (sector "gasto") e devolve erro, como o driver real.</summary>
    public bool FailErase { get; set; }
    public List<int> ErasedOffsets { get; } = new();

    /// <summary>Escrita só com a configuração de escrita (ADDRSEL1 e BUSCON1 a 0, BUSCON0 04AD).</summary>
    private bool WriteBus => Word(0xFF12) == 0xE204 && Word(0xFF0C) == 0x04AD && Word(0xFE18) == 0 && Word(0xFF14) == 0;

    private void RunFlashDriver(ushort[] r)
    {
        switch (r[0])
        {
            case 0x06:   // GETSTATE: r14 = 0 fabricante / 1 chip → r9
                r[1] = !WriteBus ? (ushort)0xFFFF : r[6] == 0 ? (ushort)0x0001 : FlashDevice;
                r[7] = 0;
                break;
            case 0x01:   // ERASE: r9 = A15–A0, r10 = página (escrita), r12 = última palavra do sector
            {
                int from = (int)(((uint)r[2] << 16 | r[1]) - 0x800000);
                int last = (int)(((uint)r[2] << 16 | r[4]) - 0x800000);
                if (!WriteBus || FailErase) { r[7] = 1; break; }
                Array.Fill(Flash, (byte)0xFF, from, last + 2 - from);
                ErasedOffsets.Add(from);
                r[7] = 0;
                break;
            }
            case 0x00:   // PROG: r9 = bytes, r10/r11 = origem, r13/r14 = destino (escrita)
            {
                int len = r[1];
                uint src = (uint)r[3] << 16 | r[2];
                int dst = (int)(((uint)r[6] << 16 | r[5]) - 0x800000);
                bool ok = WriteBus;
                for (int i = 0; i < len && ok; i++)
                {
                    byte want = Memory[src + i];
                    Flash[dst + i] &= want;            // a flash só passa bits de 1 a 0
                    ok = Flash[dst + i] == want;
                }
                r[7] = ok ? (ushort)0 : (ushort)1;
                break;
            }
        }
    }

    private static byte Xor(byte[] d) { byte x = 0; foreach (var b in d) x ^= b; return x; }
}

public class BslTests
{
    private static (C167Bsl bsl, FakeBootEcu ecu) Make()
    {
        var ecu = new FakeBootEcu();
        var rnd = new Random(1234);
        rnd.NextBytes(ecu.Flash); rnd.NextBytes(ecu.InternalRom); rnd.NextBytes(ecu.Eeprom);
        return (new C167Bsl(ecu) { TimeoutMs = 200 }, ecu);
    }

    [Fact]
    public void Connect_SendsLoaderAndKernel_AndReportsCpu()
    {
        var (bsl, _) = Make();
        bsl.Connect(57600);
        Assert.Equal((byte)0xC5, bsl.CpuVariant);
    }

    [Fact]
    public void ReadExternalFlash_Is512KbIdentical()
    {
        var (bsl, ecu) = Make();
        bsl.Connect(57600);
        int lastDone = 0;
        var data = BslRead.ReadExternalFlash(bsl, progress: (d, _) => lastDone = d);
        Assert.Equal(ecu.Flash, data);
        Assert.Equal(BslRead.ExternalFlashSize, lastDone);
    }

    private static void Put(byte[] flash, int at, string ascii) => System.Text.Encoding.ASCII.GetBytes(ascii).CopyTo(flash, at);

    [Fact]
    public void Identify_Me31Block_Near80C6()
    {
        var (bsl, ecu) = Make();
        Array.Fill(ecu.Flash, (byte)0xFF, 0x8000, 0x400);
        // disposição ME3.1: desenho Fiat, HW, SW, versão (valores sintéticos)
        Put(ecu.Flash, 0x80C6, "12345678"); ecu.Flash[0x80CE] = 0x00;
        Put(ecu.Flash, 0x80D1, "0261299999"); ecu.Flash[0x80DB] = 0x00; ecu.Flash[0x80DC] = 0x00;
        Put(ecu.Flash, 0x80DD, "1277399999"); ecu.Flash[0x80E7] = 0x01; ecu.Flash[0x80E8] = 0x02;
        bsl.Connect(125000);

        var id = BslRead.Identify(bsl);

        Assert.Equal("ME3.1", id.Family);
        Assert.Equal("0261299999", id.Ident!.Hardware);
        Assert.Equal("1277399999", id.Ident.Software);
        Assert.Equal("12345678", id.Ident.FiatDrawing);
    }

    [Fact]
    public void Identify_Me731Block_Near10190()
    {
        var (bsl, ecu) = Make();
        Array.Fill(ecu.Flash, (byte)0xFF, 0x8000, 0x400);     // nada na janela da ME3.1
        Array.Fill(ecu.Flash, (byte)0xFF, 0x10000, 0x400);
        // disposição ME7.3.1: 7C 86 …, HW, SW, versão, desenho (valores sintéticos)
        ecu.Flash[0x1018B] = 0x7C; ecu.Flash[0x1018C] = 0x86;
        Put(ecu.Flash, 0x10190, "0261299998"); ecu.Flash[0x1019A] = 0x00;
        Put(ecu.Flash, 0x1019B, "1037399998"); ecu.Flash[0x101A5] = 0x00;
        ecu.Flash[0x101A6] = 0x03; ecu.Flash[0x101A7] = 0x01;
        Put(ecu.Flash, 0x101A9, "87654321");
        bsl.Connect(125000);

        var id = BslRead.Identify(bsl);

        Assert.Equal("ME7.3.1", id.Family);
        Assert.Equal("0261299998", id.Ident!.Hardware);
        Assert.Equal("1037399998", id.Ident.Software);
    }

    [Fact]
    public void Identify_NoBlock_ReturnsNull()
    {
        var (bsl, ecu) = Make();
        Array.Fill(ecu.Flash, (byte)0xFF);
        bsl.Connect(125000);
        Assert.Null(BslRead.Identify(bsl).Ident);
    }

    [Fact]
    public void ReadInternalRom_Is32KbIdentical()
    {
        var (bsl, ecu) = Make();
        bsl.Connect(57600);
        Assert.Equal(ecu.InternalRom, BslRead.ReadInternalRom(bsl));
    }

    [Fact]
    public void ReadSpiEeprom_P47_Is1KbIdentical()
    {
        var (bsl, ecu) = Make();
        bsl.Connect(57600);
        Assert.Equal(ecu.Eeprom, BslRead.ReadSpiEeprom(bsl));
    }

    [Fact]
    public void ReadSpiEeprom_95040StatusF0_AlsoWorks()
    {
        var (bsl, ecu) = Make();
        ecu.EepromStatus = 0xF0;
        bsl.Connect(57600);
        Assert.Equal(ecu.Eeprom, BslRead.ReadSpiEeprom(bsl));
    }

    [Fact]
    public void ReadSpiEeprom_WrongChipSelect_ClearError()
    {
        var (bsl, _) = Make();
        bsl.Connect(57600);
        var ex = Assert.Throws<BslException>(() => BslRead.ReadSpiEeprom(bsl, csPort: 6, csPin: 3));
        Assert.Contains("P6.3", ex.Message);
    }

    [Fact]
    public void WriteSpiEeprom_OnlyChangedBytes_AndVerified()
    {
        var (bsl, ecu) = Make();
        bsl.Connect(125000);
        var current = BslRead.ReadSpiEeprom(bsl);
        var data = (byte[])current.Clone();
        for (int i = 0x100; i < 0x150; i++) data[i] ^= 0x5A;   // 80 bytes seguidos (3 chamadas de ≤ 32)
        data[0x3FF] ^= 1;

        int written = BslRead.WriteSpiEeprom(bsl, data, current);

        Assert.Equal(81, written);
        Assert.Equal(81, ecu.EepromBytesWritten);
        Assert.Equal(data, ecu.Eeprom);
    }

    [Fact]
    public void WriteSpiEeprom_NoCompare_WritesAll_NoVerify()
    {
        var (bsl, ecu) = Make();
        bsl.Connect(125000);
        var data = new byte[0x400]; new Random(77).NextBytes(data);

        int written = BslRead.WriteSpiEeprom(bsl, data, current: null, verify: false);

        Assert.Equal(0x400, written);
        Assert.Equal(0x400, ecu.EepromBytesWritten);
        Assert.Equal(data, ecu.Eeprom);
    }

    [Fact]
    public void WriteSpiEeprom_BlockProtection_Refused()
    {
        var (bsl, ecu) = Make();
        bsl.Connect(125000);
        var current = BslRead.ReadSpiEeprom(bsl);
        var data = (byte[])current.Clone(); data[0] ^= 1;
        ecu.EepromStatus = 0x0C;   // BP1 + BP0
        var ex = Assert.Throws<BslException>(() => BslRead.WriteSpiEeprom(bsl, data, current));
        Assert.Contains("0x0C", ex.Message);
        Assert.Equal(0, ecu.EepromBytesWritten);
    }

    [Fact]
    public void WriteSpiEeprom_WpPin_VerificationFails()
    {
        var (bsl, ecu) = Make();
        bsl.Connect(125000);
        var current = BslRead.ReadSpiEeprom(bsl);
        var data = (byte[])current.Clone(); data[0x20] ^= 0xFF;
        ecu.EepromWriteProtectPin = true;
        var ex = Assert.Throws<BslException>(() => BslRead.WriteSpiEeprom(bsl, data, current));
        Assert.Contains("0x020", ex.Message);
    }

    [Fact]
    public void BadBlockChecksum_Throws()
    {
        var (bsl, ecu) = Make();
        bsl.Connect(57600);
        BslRead.ReadInternalRom(bsl, 0x200);   // configura
        ecu.CorruptNextBlockChecksum = true;
        Assert.Throws<BslException>(() => bsl.ReadBlock(BslRead.InternalRomAddress, 0x200));
    }

    [Fact]
    public void NotInBootMode_ClearError()
    {
        var ecu = new SilentDevice();
        var ex = Assert.Throws<BslException>(() => new C167Bsl(ecu) { TimeoutMs = 50, ExpectEcho = false }.Connect(57600));
        Assert.Contains("modo boot", ex.Message);
    }

    [Fact]
    public void Resources_HaveExpectedSizes()
    {
        Assert.Equal(32, BslResources.Loader.Length);
        Assert.Equal(394, BslResources.Kernel.Length);
        Assert.Equal(278, BslResources.Eeprom95Driver.Length);
        Assert.Equal(398, BslResources.Flash29FDriver.Length);
    }

    [Fact]
    public void PrepareAndReadChip_Am29F400BB()
    {
        var (bsl, _) = Make();
        bsl.Connect(125000);
        var chip = BslWrite.PrepareAndReadChip(bsl);
        Assert.True(chip.IsAm29F400Bb);
        Assert.Equal(0x22AB, chip.Device);
    }

    [Fact]
    public void PrepareAndReadChip_OtherChip_NotAccepted()
    {
        var (bsl, ecu) = Make();
        ecu.FlashDevice = 0x2258;   // 29F800BB
        bsl.Connect(125000);
        Assert.False(BslWrite.PrepareAndReadChip(bsl).IsAm29F400Bb);
    }

    [Fact]
    public void ChangedSectors_OnlyTheOnesThatDiffer()
    {
        var a = new byte[0x80000]; new Random(5).NextBytes(a);
        var b = (byte[])a.Clone();
        b[0x05000] ^= 1;   // sector 1 (8 K em 0x4000)
        b[0x7FFFF] ^= 1;   // sector 10 (último)
        Assert.Equal(new[] { 1, 10 }, BslWrite.ChangedSectors(b, a));
        Assert.Empty(BslWrite.ChangedSectors(a, a));
    }

    [Fact]
    public void WriteSectors_EraseProgramVerify_OnlyChangedSectors()
    {
        var (bsl, ecu) = Make();
        bsl.Connect(125000);
        var current = BslRead.ReadExternalFlash(bsl);
        var image = (byte[])current.Clone();
        new Random(9).NextBytes(image.AsSpan(0x20000, 0x10000));   // sector 5
        Array.Fill(image, (byte)0xFF, 0x08000, 0x8000);              // sector 3 todo FF (fica só apagado)
        image[0x08010] = 0x12;

        var sectors = BslWrite.ChangedSectors(image, current);
        Assert.Equal(new[] { 3, 5 }, sectors);
        BslWrite.PrepareAndReadChip(bsl);
        int last = 0;
        BslWrite.WriteSectors(bsl, image, sectors, (_, d, _) => last = d);

        Assert.Equal(image, ecu.Flash);
        Assert.Equal(new[] { 0x08000, 0x20000 }, ecu.ErasedOffsets);   // o arranque (sector 0) não foi tocado
        Assert.Equal(0x8000 + 0x10000, last);
        Assert.Empty(BslWrite.VerifySectors(bsl, image, sectors));
    }

    [Fact]
    public void WriteSectors_EraseFails_Throws()
    {
        var (bsl, ecu) = Make();
        bsl.Connect(125000);
        var image = (byte[])ecu.Flash.Clone();
        image[0x30000] ^= 0xFF;
        BslWrite.PrepareAndReadChip(bsl);
        ecu.FailErase = true;
        var ex = Assert.Throws<BslException>(() => BslWrite.WriteSectors(bsl, image, new[] { 6 }));
        Assert.Contains("6", ex.Message);
    }

    [Fact]
    public void VerifySectors_ReportsMismatch()
    {
        var (bsl, ecu) = Make();
        bsl.Connect(125000);
        var image = (byte[])ecu.Flash.Clone();
        image[0x40001] ^= 0x01;
        Assert.Equal(new[] { 7 }, BslWrite.VerifySectors(bsl, image, new[] { 6, 7 }));
    }

    private sealed class SilentDevice : ICommunicationDevice
    {
        public string Name => "silent"; public bool IsOpen => true;
        public void Open() { } public void Close() { } public void Dispose() { }
        public void SetBaudRate(int b) { } public void Write(ReadOnlySpan<byte> d) { }
        public int Read(Span<byte> b, int t) => 0;
        public void DiscardBuffers() { } public void SetBreak(bool e) { }
    }
}
