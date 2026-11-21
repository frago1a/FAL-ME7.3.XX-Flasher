using ME7Tool.Core.Transport;
using static ME7Tool.Core.Common.Lang;

namespace ME7Tool.Core.Bsl;

/// <summary>Erro do modo boot (BSL): sem resposta, eco errado, checksum errado, ECU que não está em modo boot.</summary>
public sealed class BslException(string message) : Exception(message);

/// <summary>Binários embutidos do modo boot (ver Bsl/Resources/NOTICE.txt).</summary>
public static class BslResources
{
    /// <summary>Loader de 32 bytes do bootstrap loader do C167.</summary>
    public static byte[] Loader => Load("Bsl.LOADK.bin");

    /// <summary>Kernel do Minimon (394 bytes).</summary>
    public static byte[] Kernel => Load("Bsl.MINIMONK.bin");

    /// <summary>Driver da EEPROM SPI 95xxx (carrega-se em 0xF600, entrada em 0xF640).</summary>
    public static byte[] Eeprom95Driver => Load("Bsl.Eeprom95Driver.bin");

    /// <summary>Driver das flash AMD 29Fx00B: programar, apagar sector, ler IDs (carrega-se em 0xF600, entrada em 0xF640).</summary>
    public static byte[] Flash29FDriver => Load("Bsl.Flash29FDriver.bin");

    private static byte[] Load(string name)
    {
        using var s = typeof(BslResources).Assembly.GetManifestResourceStream(name)
                      ?? throw new InvalidOperationException($"Recurso {name} em falta.");
        var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

/// <summary>
/// Modo boot (bootstrap loader) do C167 com o kernel Minimon — port do C167BootTool (GPL-3.0). A ECU tem de estar em
/// modo boot (na família ME7.3/ME3.1 da Fiat entra-se por um pino do conector) e ligada; a ligação é pela K-line com
/// um cabo KKL (FTDI ou porta série), e cada byte enviado volta em eco.
/// <para>Entrada: <c>00</c> → a ECU responde com o byte da variante do CPU; recebe o loader (32 bytes) → <c>01</c>;
/// recebe o kernel → <c>03</c>. Depois os comandos do Minimon confirmam com <c>AA</c> (comando aceite) e <c>EA</c>
/// (fim).</para>
/// </summary>
public sealed class C167Bsl(ICommunicationDevice device)
{
    private const byte IloaderStarted = 0x01, IapplicationStarted = 0x03;
    private const byte Ack1 = 0xAA, Ack2 = 0xEA;
    private const byte CwriteBlock = 0x84, CreadBlock = 0x85, CgetChecksum = 0x33, CtestComm = 0x93,
                       CcallFunction = 0x9F, CwriteWord = 0x82, CreadWord = 0xCD;

    /// <summary>Tempo máximo à espera de cada resposta (ms).</summary>
    public int TimeoutMs { get; set; } = 3000;

    /// <summary>K-line: cada byte enviado volta em eco (true por omissão).</summary>
    public bool ExpectEcho { get; set; } = true;

    public Action<string>? OnLog { get; set; }

    /// <summary>Byte da variante do CPU devolvido na entrada (null se o kernel já estava a correr).</summary>
    public byte? CpuVariant { get; private set; }

    public static string VariantName(byte b) => b switch
    {
        0x55 => "C166 / ST10",
        0xA5 => T("C167 (antigo)", "C167 (old)"),
        0xB5 => "C165",
        0xC5 => "C167",
        0xD5 => T("C167 com ID / ST10", "C167 with ID / ST10"),
        _ => T("desconhecido", "unknown"),
    };

    /// <summary>
    /// Entra em modo boot e carrega o kernel. O baud é medido pela própria ECU no byte <c>00</c> (autobaud).
    /// </summary>
    public void Connect(int baud)
    {
        device.SetBaudRate(baud);
        if (device is IModemLines ml)
        {
            // como o C167BootTool: impulso no DTR e cabo KKL em modo K-line (DTR e RTS a 0)
            ml.SetDtr(true); Thread.Sleep(100);
            ml.SetDtr(false); Thread.Sleep(100);
            ml.SetRts(false); Thread.Sleep(100);
        }
        device.DiscardBuffers();

        WriteEcho(new byte[] { 0x00 });
        byte r = ReadExact(1, T("resposta ao byte de arranque — a ECU está em modo boot e ligada?",
                                "answer to the start byte — is the ECU in boot mode and powered?"))[0];
        if (r == Ack1)
            Log(T("O kernel já estava a correr.", "The kernel was already running."));
        else
        {
            CpuVariant = r;
            Log(T($"Modo boot: CPU 0x{r:X2} ({VariantName(r)}). A enviar o loader…", $"Boot mode: CPU 0x{r:X2} ({VariantName(r)}). Sending the loader…"));
            WriteEcho(BslResources.Loader);
            Expect(IloaderStarted, T("arranque do loader", "loader start"));
            Log(T("A enviar o kernel Minimon…", "Sending the Minimon kernel…"));
            WriteEcho(BslResources.Kernel);
            Expect(IapplicationStarted, T("arranque do kernel", "kernel start"));
        }
        TestComm();
        Log(T("Kernel Minimon a correr.", "Minimon kernel running."));
    }

    /// <summary>Teste de comunicação com o kernel (<c>93</c> → <c>AA EA</c>).</summary>
    public void TestComm()
    {
        WriteEcho(new[] { CtestComm });
        var r = ReadExact(2, T("teste de comunicação", "communication test"));
        if (r[0] != Ack1 || r[1] != Ack2)
            throw new BslException(T($"Teste de comunicação: resposta {r[0]:X2} {r[1]:X2} em vez de AA EA.", $"Communication test: answer {r[0]:X2} {r[1]:X2} instead of AA EA."));
    }

    /// <summary>Escreve uma palavra (registo ou memória).</summary>
    public void WriteWord(uint address, ushort value)
    {
        Command(CwriteWord);
        WriteEcho(Concat(Addr(address), Word(value)));
        Expect(Ack2, T($"escrever 0x{address:X6}", $"write 0x{address:X6}"));
    }

    /// <summary>Lê uma palavra (registo ou memória).</summary>
    public ushort ReadWord(uint address)
    {
        Command(CreadWord);
        WriteEcho(Addr(address));
        var r = ReadExact(3, T($"ler 0x{address:X6}", $"read 0x{address:X6}"));
        if (r[2] != Ack2) throw Bad(r[2], $"0x{address:X6}");
        return (ushort)(r[0] | r[1] << 8);
    }

    /// <summary>Escreve uma palavra e confirma-a lendo de volta (como o C167BootTool faz com os registos).</summary>
    public void SetWord(uint address, ushort value)
    {
        WriteWord(address, value);
        ushort back = ReadWord(address);
        if (back != value)
            throw new BslException(T($"0x{address:X6}: escrito {value:X4}, lido {back:X4}.", $"0x{address:X6}: wrote {value:X4}, read {back:X4}."));
    }

    /// <summary>Lê um bloco de memória (até 64 KB) e confere o checksum XOR do kernel.</summary>
    public byte[] ReadBlock(uint address, int length)
    {
        if (length is <= 0 or > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(length));
        Command(CreadBlock);
        WriteEcho(Concat(Addr(address), Word((ushort)length)));
        var r = ReadExact(length + 1, T($"bloco em 0x{address:X6}", $"block at 0x{address:X6}"));
        if (r[length] != Ack2) throw Bad(r[length], $"0x{address:X6}");
        var data = r[..length];
        byte cs = GetChecksum();
        if (cs != Xor(data))
            throw new BslException(T($"Checksum errado no bloco em 0x{address:X6} (ECU {cs:X2}, calculado {Xor(data):X2}).",
                                     $"Wrong checksum in the block at 0x{address:X6} (ECU {cs:X2}, computed {Xor(data):X2})."));
        return data;
    }

    /// <summary>Escreve um bloco na RAM (usado para carregar drivers) e confere o checksum.</summary>
    public void WriteBlock(uint address, byte[] data)
    {
        if (data.Length is 0 or > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(data));
        Command(CwriteBlock);
        WriteEcho(Concat(Addr(address), Word((ushort)data.Length), data));
        Expect(Ack2, T($"bloco em 0x{address:X6}", $"block at 0x{address:X6}"));
        byte cs = GetChecksum();
        if (cs != Xor(data))
            throw new BslException(T($"Checksum errado ao escrever em 0x{address:X6} (ECU {cs:X2}, calculado {Xor(data):X2}).",
                                     $"Wrong checksum writing at 0x{address:X6} (ECU {cs:X2}, computed {Xor(data):X2})."));
    }

    /// <summary>Chama uma função na ECU com r8–r15 e devolve r8–r15 à saída.</summary>
    public ushort[] Call(uint address, params ushort[] r8ToR15) => CallWithTimeout(address, TimeoutMs, r8ToR15);

    /// <summary>Como <see cref="Call"/>, com um tempo máximo próprio (ex.: apagar um sector da flash demora segundos).</summary>
    public ushort[] CallWithTimeout(uint address, int timeoutMs, params ushort[] r8ToR15)
    {
        if (r8ToR15.Length != 8) throw new ArgumentException("8 registos (r8–r15).", nameof(r8ToR15));
        Command(CcallFunction);
        var p = new List<byte>(Addr(address));
        foreach (var w in r8ToR15) p.AddRange(Word(w));
        WriteEcho(p.ToArray());
        var r = ReadExact(17, T($"função em 0x{address:X6}", $"function at 0x{address:X6}"), timeoutMs);
        if (r[16] != Ack2) throw Bad(r[16], $"0x{address:X6}");
        var regs = new ushort[8];
        for (int i = 0; i < 8; i++) regs[i] = (ushort)(r[2 * i] | r[2 * i + 1] << 8);
        return regs;
    }

    // ------------------------------------------------------------ baixo nível

    private byte GetChecksum()
    {
        WriteEcho(new[] { CgetChecksum });
        var r = ReadExact(3, "checksum");
        if (r[2] != Ack2) throw Bad(r[2], "checksum");
        return r[1];
    }

    private void Command(byte c)
    {
        WriteEcho(new[] { c });
        Expect(Ack1, T($"comando 0x{c:X2}", $"command 0x{c:X2}"));
    }

    private void Expect(byte want, string what)
    {
        byte got = ReadExact(1, what)[0];
        if (got != want)
            throw new BslException(T($"{what}: resposta 0x{got:X2} em vez de 0x{want:X2}.", $"{what}: answer 0x{got:X2} instead of 0x{want:X2}."));
    }

    private static BslException Bad(byte got, string what) =>
        new(T($"{what}: terminou com 0x{got:X2} em vez de EA.", $"{what}: ended with 0x{got:X2} instead of EA."));

    private void WriteEcho(byte[] data)
    {
        device.Write(data);
        if (!ExpectEcho) return;
        var echo = ReadExact(data.Length, T("eco", "echo"));
        for (int i = 0; i < data.Length; i++)
            if (echo[i] != data[i])
                throw new BslException(T($"Eco diferente no byte {i}: enviado {data[i]:X2}, recebido {echo[i]:X2} (linha com ruído ou cabo sem eco?).",
                                         $"Echo differs at byte {i}: sent {data[i]:X2}, received {echo[i]:X2} (noisy line or cable without echo?)."));
    }

    private byte[] ReadExact(int count, string what, int? timeoutMs = null)
    {
        var buf = new byte[count];
        int got = 0;
        var deadline = Environment.TickCount64 + (timeoutMs ?? TimeoutMs);
        while (got < count)
        {
            long left = deadline - Environment.TickCount64;
            if (left <= 0)
                throw new BslException(T($"Sem resposta ({what}): {got}/{count} bytes.", $"No answer ({what}): {got}/{count} bytes."));
            got += device.Read(buf.AsSpan(got), (int)Math.Min(left, 1000));
        }
        return buf;
    }

    private void Log(string s) => OnLog?.Invoke(s);

    private static byte[] Addr(uint a) => new[] { (byte)a, (byte)(a >> 8), (byte)(a >> 16) };
    private static byte[] Word(ushort w) => new[] { (byte)w, (byte)(w >> 8) };
    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static byte Xor(byte[] data) { byte x = 0; foreach (var b in data) x ^= b; return x; }
}
