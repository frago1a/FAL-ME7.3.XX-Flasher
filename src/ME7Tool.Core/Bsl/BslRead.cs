using ME7Tool.Core.Flashing;
using static ME7Tool.Core.Common.Lang;

namespace ME7Tool.Core.Bsl;

/// <summary>
/// Leituras por modo boot (BSL), com o kernel Minimon já a correr (<see cref="C167Bsl.Connect"/>). Só lê: as escritas
/// são nos registos de configuração do CPU (SYSCON, BUSCON, ADDRSEL) e na RAM interna (driver da EEPROM), que se
/// perdem ao desligar. Port das leituras do C167BootTool (GPL-3.0).
/// </summary>
public static class BslRead
{
    // registos do C167
    private const uint Syscon = 0xFF12, Buscon0 = 0xFF0C, Buscon1 = 0xFF14, Buscon2 = 0xFF16, Buscon3 = 0xFF18, Buscon4 = 0xFF1A;
    private const uint Addrsel1 = 0xFE18, Addrsel2 = 0xFE1A, Addrsel3 = 0xFE1C, Addrsel4 = 0xFE1E;

    /// <summary>Onde a ROM interna fica visível com SYSCON 0xF604 (ROMEN, ROMS1).</summary>
    public const uint InternalRomAddress = 0x010000;

    /// <summary>ROM interna do C167CR: 32 KB.</summary>
    public const int InternalRomSize = 0x8000;

    /// <summary>Onde a flash externa fica visível com a configuração de leitura (CS0).</summary>
    public const uint ExternalFlashAddress = 0x800000;

    /// <summary>Flash externa das ME7.3 / ME3.1 da Fiat (AM29F400, 512 KB).</summary>
    public const int ExternalFlashSize = 0x80000;

    /// <summary>EEPROM 95080 das ME7.3 / ME3.1 da Fiat: 1 KB, chip-select P4.7.</summary>
    public const int Eeprom95080Size = 0x400;

    private const int BlockSize = 0x200;
    private const uint DriverAddress = 0xF600, DriverEntry = 0xF640, DriverBuffer = 0xFC00;
    private const ushort SpiGetState = 0x0093, SpiRead = 0x0036, SpiWrite = 0x0037;

    /// <summary>Lê a ROM interna do C167 (32 KB por omissão).</summary>
    public static byte[] ReadInternalRom(C167Bsl bsl, int size = InternalRomSize, Action<int, int>? progress = null)
    {
        bsl.SetWord(Syscon, 0xF604);
        foreach (var r in new[] { Buscon0, Addrsel1, Addrsel2, Addrsel3, Addrsel4, Buscon1, Buscon2, Buscon3, Buscon4 })
            bsl.SetWord(r, 0);
        return ReadRange(bsl, InternalRomAddress, size, progress);
    }

    /// <summary>
    /// Lê a flash externa (512 KB por omissão). Configuração de chip-selects da ME7 (C167BootTool) — confirmada numa
    /// Fiat ME3.1: leitura igual byte a byte ao dump de uma máquina de bancada. O hardware da ME7.3.1 é o mesmo.
    /// </summary>
    public static byte[] ReadExternalFlash(C167Bsl bsl, int size = ExternalFlashSize, Action<int, int>? progress = null)
    {
        ConfigureExternalBus(bsl);
        return ReadRange(bsl, ExternalFlashAddress, size, progress);
    }

    /// <summary>Identificação lida por modo boot: bloco de identificação da flash e família pela posição dele.</summary>
    public sealed record BslIdentity(EcuIdent? Ident, string? Family);

    /// <summary>
    /// Identifica a ECU sem ler a flash toda: lê 1 KB à volta de 0x8000 (bloco da ME3.1, perto de 0x80C6) e, se não
    /// estiver lá, 1 KB à volta de 0x10000 (ME7.3.1, perto de 0x10190) — ~0,2 s a 125000 baud.
    /// </summary>
    public static BslIdentity Identify(C167Bsl bsl)
    {
        ConfigureExternalBus(bsl);
        foreach (var (offset, family) in new[] { (0x8000, "ME3.1"), (0x10000, "ME7.3.1") })
        {
            var window = ReadRange(bsl, ExternalFlashAddress + (uint)offset, 0x400, null);
            if (EcuIdent.FromImage(window) is { } id) return new BslIdentity(id, family);
        }
        return new BslIdentity(null, null);
    }

    internal static void ConfigureExternalBus(C167Bsl bsl)
    {
        bsl.SetWord(Syscon, 0xE204);
        bsl.SetWord(Buscon0, 0x04AD);   // 16 bits demultiplexado, 2 wait states
        bsl.SetWord(Addrsel1, 0x3803);  // janela de 32 KB em 0x380000 (RAM externa)
        bsl.SetWord(Addrsel2, 0x2008);
        bsl.SetWord(Addrsel3, 0);
        bsl.SetWord(Addrsel4, 0);
        bsl.SetWord(Buscon1, 0x040D);
        bsl.SetWord(Buscon2, 0x04AD);
        bsl.SetWord(Buscon3, 0);
        bsl.SetWord(Buscon4, 0);
    }

    /// <summary>
    /// Lê uma EEPROM SPI 95xxx pela SSC do C167 com o driver embutido. <paramref name="type"/>: 0 = 95080…95320,
    /// 1 = 95040/95P08. Chip-select na porta/pino dados (Fiat ME7.3 / ME3.1: P4.7).
    /// </summary>
    public static byte[] ReadSpiEeprom(C167Bsl bsl, int size = Eeprom95080Size, int csPort = 4, int csPin = 7, int type = 0,
                                       Action<int, int>? progress = null)
    {
        byte status = PrepareEepromDriver(bsl, csPort, csPin);
        var result = new byte[size];
        for (int off = 0; off < size; off += BlockSize)
        {
            int n = Math.Min(BlockSize, size - off);
            var rr = bsl.Call(DriverEntry, SpiRead, (ushort)n, (ushort)off, (ushort)type, 0, (ushort)DriverBuffer, 0, 9);
            if (rr[7] != 0)
                throw new BslException(T($"Leitura da EEPROM falhou em 0x{off:X3} (código 0x{rr[7]:X4}).", $"EEPROM read failed at 0x{off:X3} (code 0x{rr[7]:X4})."));
            bsl.ReadBlock(DriverBuffer, n).CopyTo(result, off);
            progress?.Invoke(off + n, size);
        }
        if (result.All(x => x == result[0]) && result[0] is 0x00 or 0xFF)
            throw new BslException(T($"A EEPROM leu-se toda a 0x{result[0]:X2} (estado 0x{status:X2}): o chip-select P{csPort}.{csPin} deve estar errado ou a EEPROM não responde.",
                                     $"The EEPROM read as all 0x{result[0]:X2} (status 0x{status:X2}): the chip-select P{csPort}.{csPin} is probably wrong or the EEPROM does not answer."));
        return result;
    }

    /// <summary>Bytes por chamada de escrita ao driver (byte a byte, ~5 ms cada na EEPROM → ~0,2 s por chamada).</summary>
    private const int EepromWriteChunk = 32;

    /// <summary>
    /// Grava uma EEPROM SPI 95xxx com o driver embutido, só nos bytes em que <paramref name="data"/> difere de
    /// <paramref name="current"/> (o conteúdo lido antes; null = grava todos), e verifica relendo (se <paramref name="verify"/>). Recusa se o registo de estado tiver a
    /// proteção de blocos ligada (BP1/BP0). Devolve o nº de bytes gravados.
    /// </summary>
    public static int WriteSpiEeprom(C167Bsl bsl, byte[] data, byte[]? current, int csPort = 4, int csPin = 7, int type = 0,
                                     Action<int, int>? progress = null, bool verify = true)
    {
        if (current != null && data.Length != current.Length) throw new ArgumentException(T("A EEPROM lida e o ficheiro têm tamanhos diferentes.", "The EEPROM read and the file have different sizes."));
        byte status = PrepareEepromDriver(bsl, csPort, csPin);
        if ((status & 0x0C) != 0)
            throw new BslException(T($"A EEPROM tem a proteção de escrita ligada (estado 0x{status:X2}, bits BP1/BP0): não se grava.",
                                     $"The EEPROM has write protection on (status 0x{status:X2}, BP1/BP0 bits): not writing."));

        bool Differs(int i) => current == null || data[i] != current[i];   // sem leitura: grava todos
        int total = 0;
        for (int i = 0; i < data.Length; i++) if (Differs(i)) total++;
        int done = 0;
        for (int off = 0; off < data.Length;)
        {
            if (!Differs(off)) { off++; continue; }
            int n = 1;
            while (off + n < data.Length && n < EepromWriteChunk && Differs(off + n)) n++;
            bsl.WriteBlock(DriverBuffer, data.AsSpan(off, n).ToArray());
            var wr = bsl.CallWithTimeout(DriverEntry, 5000, SpiWrite, (ushort)n, (ushort)off, (ushort)type, 0, (ushort)DriverBuffer, 0, 9);
            if (wr[7] != 0)
                throw new BslException(T($"Escrita da EEPROM falhou em 0x{off:X3} (código 0x{wr[7]:X4}).", $"EEPROM write failed at 0x{off:X3} (code 0x{wr[7]:X4})."));
            done += n; off += n;
            progress?.Invoke(done, total);
        }

        // o driver não espera pelo fim do último byte: esperar que o WIP (bit 0 do estado) desça antes de reler
        for (int i = 0; i < 50 && (bsl.Call(DriverEntry, SpiGetState, 0, 0, 0, 0, 0, 0, 1)[1] & 1) != 0; i++) Thread.Sleep(2);
        if (!verify) return total;

        var back = ReadSpiEeprom(bsl, data.Length, csPort, csPin, type);
        int bad = Enumerable.Range(0, data.Length).FirstOrDefault(i => back[i] != data[i], -1);
        if (bad >= 0)
            throw new BslException(T($"A verificação da EEPROM falhou em 0x{bad:X3} (lido 0x{back[bad]:X2}, esperado 0x{data[bad]:X2}): pino WP ativo ou EEPROM com defeito?",
                                     $"EEPROM verification failed at 0x{bad:X3} (read 0x{back[bad]:X2}, expected 0x{data[bad]:X2}): WP pin active or faulty EEPROM?"));
        return total;
    }

    /// <summary>Carrega o driver da EEPROM com o chip-select certo e devolve o registo de estado (FF → erro).</summary>
    private static byte PrepareEepromDriver(C167Bsl bsl, int csPort, int csPin)
    {
        if (csPin is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(csPin));
        (byte port8, byte dir8) = csPort switch
        {
            2 => ((byte)0xE0, (byte)0xE1), 3 => ((byte)0xE2, (byte)0xE3), 4 => ((byte)0xE4, (byte)0xE5),
            6 => ((byte)0xE6, (byte)0xE7), 7 => ((byte)0xE8, (byte)0xE9), 8 => ((byte)0xEA, (byte)0xEB),
            _ => throw new ArgumentOutOfRangeException(nameof(csPort), T("Portas suportadas: 2, 3, 4, 6, 7, 8.", "Supported ports: 2, 3, 4, 6, 7, 8.")),
        };

        bsl.WriteBlock(DriverAddress, BslResources.Eeprom95Driver);
        // o driver começa com 4 instruções de chip-select a acertar: BSET/BCLR <bit> (1.º byte = pino<<4 | opcode)
        ushort Bit(byte bitoff, int opcode) => (ushort)(((csPin << 4) | opcode) | (bitoff << 8));
        bsl.SetWord(DriverAddress + 0x0, Bit(dir8, 0xF));   // CS como saída
        bsl.SetWord(DriverAddress + 0x4, Bit(dir8, 0xE));   // CS como entrada
        bsl.SetWord(DriverAddress + 0xC, Bit(port8, 0xF));  // CS a 1 (inativo)
        bsl.SetWord(DriverAddress + 0x8, Bit(port8, 0xE));  // CS a 0 (ativo)

        // Registo de estado (RDSR): 95040 → F0 (bits 7–4 leem-se a 1); 95080/95160 → 00, ou 80 com o SRWD ligado (visto
        // numa Fiat ME3.1: 00). O C167BootTool só aceitava F0/80. FF = linha de dados a 1 = ninguém responde; um 00 pode
        // ser a linha presa a 0 (chip-select errado), por isso confirma-se pelos dados no fim.
        var st = bsl.Call(DriverEntry, SpiGetState, 0, 0, 0, 0, 0, 0, 1);
        byte status = (byte)st[1];
        if (status == 0xFF)
            throw new BslException(T($"A EEPROM não responde (estado 0xFF): chip-select P{csPort}.{csPin} errado ou EEPROM de outro tipo.",
                                     $"The EEPROM does not answer (status 0xFF): wrong chip-select P{csPort}.{csPin} or another EEPROM type."));
        return status;
    }

    internal static byte[] ReadRange(C167Bsl bsl, uint address, int size, Action<int, int>? progress)
    {
        var result = new byte[size];
        for (int off = 0; off < size; off += BlockSize)
        {
            int n = Math.Min(BlockSize, size - off);
            bsl.ReadBlock(address + (uint)off, n).CopyTo(result, off);
            progress?.Invoke(off + n, size);
        }
        return result;
    }
}
