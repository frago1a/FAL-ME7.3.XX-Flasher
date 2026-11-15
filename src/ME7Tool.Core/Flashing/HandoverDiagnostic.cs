using System.Text;
using ME7Tool.Core.Kwp2000;
using ME7Tool.Core.Transport;

namespace ME7Tool.Core.Flashing;

/// <summary>Resultado do diagnóstico: resumo (para o log) e detalhe completo (para ficheiro).</summary>
public sealed record HandoverDiagnosticReport(IReadOnlyList<string> Summary, string Detail);

/// <summary>
/// Diagnóstico SÓ DE LEITURA para quando a ECU recusa passar ao programador (31 → 7F 31 78 → 7F 31 22).
/// Na aplicação ME7.3, essa passagem copia o programador de flash da Fiat (flash 0x808048…) para a XRAM
/// (0x380000…) e lê cada palavra de volta; uma diferença retira a permissão (kwp09_Bits bit 1).
/// Correr LOGO A SEGUIR à recusa, sem cortar a alimentação da ECU, para ver:
/// <list type="bullet">
/// <item>as variáveis de controlo na RAM interna (kwp09_Bits, flashorder, triggerOpModeChange, flashState…);</item>
/// <item>se a XRAM contém o programador e até onde (amostras + leitura fina na fronteira).</item>
/// </list>
/// Endereços confirmados nos ficheiros Thesis e Alfa (iguais nas duas).
/// </summary>
public sealed class HandoverDiagnostic
{
    // RAM interna (on-chip XRAM 0xE000–0xE7FF), iguais na Thesis e na Alfa.
    public const uint FlashStateAddr = 0xE2BC;
    public const uint UmFpwBitsAddr = 0xE2C2;
    public const uint TriggerOpModeChangeAddr = 0xE2C4;
    public const uint Kwp09BitsAddr = 0xE5E0;
    public const uint FlashOrderAddr = 0xE5E2;

    /// <summary>Endereço (CPU) do programador na flash e destino na XRAM.</summary>
    public uint ProgrammerSource { get; set; } = 0x808048;
    public uint ProgrammerDest { get; set; } = 0x380000;

    /// <summary>Bytes copiados. 0 = detetar no ficheiro (Thesis 0x2B68 palavras = 0x56D0; Alfa 0x3E00).</summary>
    public int ProgrammerLength { get; set; }

    public int SampleStep { get; set; } = 0x200;
    public int SampleSize { get; set; } = 0x20;
    public int ChunkSize { get; set; } = 0x20;
    public int InternalChunk { get; set; } = 0x40;

    public Action<string>? OnLog { get; set; }
    public Action<int, int>? OnProgress { get; set; }

    private readonly StringBuilder _detail = new();
    private readonly List<string> _summary = new();

    /// <summary>
    /// Nº de bytes que o carregador copia, lido do próprio código no ficheiro: procura
    /// <c>mov r8,#8048h</c> seguido de <c>cmp r12,#n</c> (n palavras). Devolve 0 se não encontrar.
    /// </summary>
    public static int DetectProgrammerLength(byte[] image)
    {
        var movR8 = new byte[] { 0xE6, 0xF8, 0x48, 0x80 };
        for (int i = 0; i + 4 <= image.Length; i++)
        {
            if (!image.AsSpan(i, 4).SequenceEqual(movR8)) continue;
            int end = Math.Min(image.Length - 4, i + 0x90);
            for (int j = i; j < end; j++)
                if (image[j] == 0x46 && image[j + 1] == 0xFC)
                {
                    int words = image[j + 2] | image[j + 3] << 8;
                    if (words > 0x100 && words < 0x4000) return words * 2;
                }
        }
        return 0;
    }

    public HandoverDiagnosticReport Run(Kwp2000Interface kwp, byte[] image, int workingBaud)
    {
        _detail.Clear(); _summary.Clear();
        int len = ProgrammerLength > 0 ? ProgrammerLength : DetectProgrammerLength(image);
        if (len == 0) { len = 0x56D0; Note("Tamanho da cópia não detetado no ficheiro; usado 0x56D0 (Thesis)."); }
        uint srcOff = ProgrammerSource - 0x800000;
        if (0x38048 + len > image.Length || srcOff + len > image.Length)
            throw new ArgumentException("O ficheiro não tem o programador na zona esperada (precisa de 512 KB).");

        Line($"Diagnóstico da passagem ao programador — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line($"Programador: flash 0x{ProgrammerSource:X6} → XRAM 0x{ProgrammerDest:X6}, 0x{len:X} bytes.");

        EnsureReadAccess(kwp, workingBaud);

        // 1) RAM interna: dump completo + variáveis de controlo.
        Line("");
        Line("== RAM interna 0xE000–0xE7FF ==");
        var iram = new byte?[0x800];
        int steps = 0x800 / InternalChunk + len / SampleStep + 16, step = 0;
        for (uint a = 0xE000; a < 0xE800; a += (uint)InternalChunk)
        {
            var d = TryRead(kwp, a, InternalChunk);
            if (d != null) for (int k = 0; k < d.Length && a - 0xE000 + k < 0x800; k++) iram[a - 0xE000 + k] = d[k];
            Line($"{a:X6}: {(d == null ? "(não legível)" : Convert.ToHexString(d))}");
            OnProgress?.Invoke(++step, steps);
        }
        int? flashState = DecodeControl(iram);
        if (flashState == 0xDD)
        {
            // Com flashState 0xDD a ECU copia do backup (sector 0x30000), não de 0x808048.
            srcOff = 0x38048;
            Note("flashState 0xDD: a comparação usa o programador do backup (flash 0x838048).");
        }

        // 2) XRAM: amostras.
        Line("");
        Line($"== XRAM: amostras de 0x{SampleSize:X} bytes de 0x{SampleStep:X} em 0x{SampleStep:X} (RAM | programador | % igual) ==");
        var samples = new List<(int Off, double? Ratio)>();
        for (int off = 0; off < len; off += SampleStep)
        {
            int n = Math.Min(SampleSize, len - off);
            var ram = TryRead(kwp, ProgrammerDest + (uint)off, n);
            var exp = image.AsSpan((int)srcOff + off, n).ToArray();
            double? r = ram == null ? null : Ratio(ram, exp);
            samples.Add((off, r));
            Line($"{ProgrammerDest + off:X6}: {(ram == null ? "(não legível)" : Convert.ToHexString(ram))} | {Convert.ToHexString(exp)} | {(r == null ? "n/d" : $"{r:P0}")}");
            OnProgress?.Invoke(++step, steps);
        }

        // 3) Interpretação + leitura fina na fronteira.
        var readable = samples.Where(s => s.Ratio != null).ToList();
        int good = readable.Count(s => s.Ratio >= 0.75), bad = readable.Count(s => s.Ratio < 0.25);
        Note($"Amostras legíveis: {readable.Count}/{samples.Count} · iguais ao programador: {good} · diferentes: {bad}.");

        if (readable.Count == 0)
            Note("Nenhuma amostra da XRAM foi legível — não dá para concluir sobre a cópia.");
        else if (readable[0].Ratio < 0.25 && good == 0)
            Note("A XRAM não contém o programador em lado nenhum: a cópia NÃO correu (ou foi toda reescrita). " +
                 "O 0x22 terá vindo antes de copiar.");
        else if (bad == 0)
            Note("A XRAM contém o programador em toda a extensão: a cópia correu até ao fim e bate certo nas amostras.");
        else
        {
            // última amostra boa antes da primeira zona só de diferenças até ao fim
            int lastGoodIdx = readable.FindLastIndex(s => s.Ratio >= 0.75);
            int firstBadAfter = readable.FindIndex(lastGoodIdx + 1, s => s.Ratio < 0.25);
            if (lastGoodIdx >= 0 && firstBadAfter > lastGoodIdx)
            {
                int from = readable[lastGoodIdx].Off, to = readable[firstBadAfter].Off + SampleSize;
                Note($"A cópia parece ter parado entre 0x{ProgrammerDest + from:X6} e 0x{ProgrammerDest + to:X6} " +
                     "(antes: igual ao programador; depois: dados da aplicação).");
                Line("");
                Line($"== Leitura fina 0x{ProgrammerDest + from:X6}–0x{ProgrammerDest + to:X6} ==");
                int? firstDiff = null;
                for (int off = from; off < to; off += ChunkSize)
                {
                    int n = Math.Min(ChunkSize, len - off);
                    if (n <= 0) break;
                    var ram = TryRead(kwp, ProgrammerDest + (uint)off, n);
                    var exp = image.AsSpan((int)srcOff + off, n).ToArray();
                    Line($"{ProgrammerDest + off:X6}: {(ram == null ? "(não legível)" : Convert.ToHexString(ram))} | {Convert.ToHexString(exp)}");
                    if (ram != null && firstDiff == null)
                        for (int k = 0; k + 1 < n; k += 2) // a cópia é por palavras
                            if (ram[k] != exp[k] || ram[k + 1] != exp[k + 1]) { firstDiff = off + k; break; }
                    OnProgress?.Invoke(++step, steps);
                }
                if (firstDiff is int fd)
                    Note($"Primeira palavra diferente na zona da fronteira: XRAM 0x{ProgrammerDest + fd:X6} " +
                         $"(programador na flash 0x{0x800000 + srcOff + fd:X6}).");
            }
            else Note("Amostras misturadas (iguais e diferentes intercaladas): a aplicação pode estar a reescrever " +
                      "variáveis por cima do programador. Ver o ficheiro de detalhe.");
        }

        Line("");
        Line("== Resumo ==");
        foreach (var s in _summary) Line(s);
        return new HandoverDiagnosticReport(_summary.ToList(), _detail.ToString());
    }

    /// <summary>
    /// Teste da RAM externa acima dos 16 KB (0x384000+), onde o programador da Thesis precisa de escrever
    /// (copia 0x56D0 bytes; a Alfa só 0x3E00, e as aplicações só usam a página 0xE0 = 0x380000–0x383FFF).
    /// Correr com a ECU acabada de ligar (antes de qualquer tentativa de flash): passa à sessão 0x86,
    /// compara blocos a 16 KB de distância (espelho?) e escreve um padrão com 0x3D em endereços de teste,
    /// lê de volta e repõe sempre o valor original.
    /// </summary>
    public HandoverDiagnosticReport RunRamTest(Kwp2000Interface kwp, int workingBaud)
    {
        _detail.Clear(); _summary.Clear();
        Line($"Teste da RAM externa — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        EnsureReadAccess(kwp, workingBaud);

        Line("");
        Line("== Leitura: blocos a 16 KB de distância (iguais = possível espelho) ==");
        foreach (var (lo, hi) in new (uint, uint)[] { (0x380000, 0x384000), (0x381000, 0x385000), (0x382000, 0x386000), (0x383F00, 0x387F00) })
        {
            var a = TryRead(kwp, lo, 16); var b = TryRead(kwp, hi, 16);
            string eq = a == null || b == null ? "n/d" : a.AsSpan().SequenceEqual(b) ? "IGUAIS" : "diferentes";
            Line($"{lo:X6}: {Hex(a)}  |  {hi:X6}: {Hex(b)}  -> {eq}");
        }

        Line("");
        Line("== Escrita (0x3D) + leitura de volta; o valor original é reposto ==");
        // controlo em baixo (zona livre segundo testes anteriores) e testes acima dos 16 KB
        foreach (uint t in new uint[] { 0x380800, 0x384800, 0x385000, 0x387F00 })
            WriteReadBack(kwp, t, t >= 0x384000 ? t - 0x4000 : (uint?)null);

        Line("");
        Line("== Resumo ==");
        foreach (var s in _summary) Line(s);
        return new HandoverDiagnosticReport(_summary.ToList(), _detail.ToString());
    }

    /// <summary>Zonas lidas por <see cref="RunRamActivity"/> (início, comprimento, descrição).</summary>
    public static IReadOnlyList<(uint Addr, int Len, string Label)> ActivityRegions { get; } = BuildActivityRegions();

    private static List<(uint, int, string)> BuildActivityRegions()
    {
        var r = new List<(uint, int, string)>
        {
            (0x380000, 0x100, "início da XRAM (destino do programador)"),
            (0x381B00, 0x200, "tabelas de medição (DAMOS CHNL_A/B/C, PRG_ERAM) — o copiador zera esta zona"),
            (0x383F00, 0x100, "TRGSEGAD / fim dos primeiros 16 KB"),
        };
        // amostras de 0x20 bytes de 1 KB em 1 KB por toda a XRAM (32 KB), fora das zonas acima
        for (uint a = 0x380400; a < 0x388000; a += 0x400)
            if (!r.Any(z => a >= z.Item1 && a < z.Item1 + (uint)z.Item2))
                r.Add((a, 0x20, a < 0x384000 ? "amostra (página 0xE0)" : "amostra (acima dos 16 KB)"));
        return r;
    }

    /// <summary>
    /// Teste de atividade da XRAM, só de leitura: lê as <see cref="ActivityRegions"/> várias vezes com uma
    /// pausa entre passagens e diz que bytes mudaram. Se a aplicação estiver a escrever na zona para onde o
    /// copiador copia o programador (0x380000…), a releitura da cópia pode falhar (→ 7F 31 22).
    /// </summary>
    public HandoverDiagnosticReport RunRamActivity(Kwp2000Interface kwp, int workingBaud, int passes = 3, int pauseMs = 1000,
                                                   CancellationToken ct = default)
    {
        _detail.Clear(); _summary.Clear();
        Line($"Teste de atividade da XRAM (só leitura) — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line($"{passes} passagens, {pauseMs} ms entre elas. Nada é escrito.");
        EnsureReadAccess(kwp, workingBaud);

        var chunks = new List<(uint Addr, int Len, int Region)>();
        for (int i = 0; i < ActivityRegions.Count; i++)
        {
            var (a, len, _) = ActivityRegions[i];
            for (int o = 0; o < len; o += ChunkSize) chunks.Add((a + (uint)o, Math.Min(ChunkSize, len - o), i));
        }

        var reads = new List<Dictionary<uint, byte[]?>>();
        var times = new List<DateTime>();
        for (int p = 0; p < passes; p++)
        {
            if (p > 0) { if (ct.WaitHandle.WaitOne(pauseMs)) ct.ThrowIfCancellationRequested(); }
            var pass = new Dictionary<uint, byte[]?>();
            times.Add(DateTime.Now);
            for (int c = 0; c < chunks.Count; c++)
            {
                ct.ThrowIfCancellationRequested();
                pass[chunks[c].Addr] = TryRead(kwp, chunks[c].Addr, chunks[c].Len);
                OnProgress?.Invoke(p * chunks.Count + c + 1, passes * chunks.Count);
            }
            reads.Add(pass);
            OnLog?.Invoke($"Passagem {p + 1}/{passes} lida.");
        }

        // bytes que mudaram entre passagens
        var changed = new List<(uint Addr, byte?[] Values)>();
        var perRegion = new int[ActivityRegions.Count];
        var readable = new int[ActivityRegions.Count];
        foreach (var (addr, len, region) in chunks)
        {
            for (int k = 0; k < len; k++)
            {
                var vals = reads.Select(r => r[addr] is byte[] b ? b[k] : (byte?)null).ToArray();
                if (vals.All(v => v == null)) continue;
                readable[region]++;
                var known = vals.Where(v => v != null).Distinct().Count();
                if (known > 1) { perRegion[region]++; changed.Add((addr + (uint)k, vals)); }
            }
        }

        Line("");
        Line("== Por zona: bytes legíveis / bytes que mudaram ==");
        for (int i = 0; i < ActivityRegions.Count; i++)
        {
            var (a, len, label) = ActivityRegions[i];
            Line($"{a:X6}–{a + (uint)len - 1:X6}  {readable[i],4} / {perRegion[i],4}  {label}");
        }

        Line("");
        Line("== Bytes que mudaram (valor em cada passagem) ==");
        foreach (var (a, vals) in changed.Take(400))
            Line($"{a:X6}: {string.Join(" → ", vals.Select(v => v is byte b ? b.ToString("X2") : "--"))}");
        if (changed.Count > 400) Line($"... e mais {changed.Count - 400}.");

        int total = changed.Count, totalRead = readable.Sum();
        int inProgrammer = changed.Count(c => c.Addr < 0x3856D0);
        int inTables = changed.Count(c => c.Addr >= 0x381B00 && c.Addr < 0x381D00);
        if (totalRead == 0)
            Note("Não deu para ler a XRAM (todas as leituras recusadas).");
        else if (total == 0)
            Note($"✓ XRAM PARADA: nenhum dos {totalRead} bytes lidos mudou em {passes} passagens. A aplicação não está a escrever nestas zonas.");
        else
        {
            Note($"⚠ XRAM ATIVA: {total} de {totalRead} bytes mudaram entre passagens.");
            Note($"   Dentro da zona do programador (0x380000–0x3856CF): {inProgrammer} bytes; nas tabelas de medição (0x381B00–0x381CFF): {inTables}.");
            if (inProgrammer > 0)
                Note("   A aplicação escreve por cima de onde o programador é copiado: se continuar a correr durante a cópia, a releitura falha (7F 31 22).");
        }
        Line("");
        Line("== Resumo ==");
        foreach (var s in _summary) Line(s);
        return new HandoverDiagnosticReport(_summary.ToList(), _detail.ToString());
    }

    private void WriteReadBack(Kwp2000Interface kwp, uint addr, uint? mirror)
    {
        const int n = 8;
        var orig = TryRead(kwp, addr, n);
        var morig = mirror is uint m0 ? TryRead(kwp, m0, n) : null;
        if (orig == null) { Note($"0x{addr:X6}: não legível — teste saltado."); return; }
        var pat = new byte[n];
        for (int i = 0; i < n; i++) pat[i] = (byte)(orig[i] ^ (0xA5 + i * 0x11)); // garantidamente diferente
        try { kwp.WriteMemoryByAddress(addr, pat); }
        catch (Kwp2000NegativeResponseException ex)
        {
            // Nada foi escrito: não há nada a repor. A ME7.3 só aceita 0x3D no modo especial
            // (opmode 0x1D16 na Thesis ou byte de calibração = 1); no modo normal a lista de escrita é vazia.
            Note($"0x{addr:X6}: a ECU recusou a escrita ({ex.ResponseCode}) — no modo normal a ME7.3 não deixa escrever RAM por diagnóstico.");
            return;
        }
        try
        {
            var back = TryRead(kwp, addr, n);
            var mnow = mirror is uint m1 ? TryRead(kwp, m1, n) : null;
            Line($"{addr:X6}: original {Hex(orig)}  escrito {Hex(pat)}  lido {Hex(back)}" +
                 (mirror is uint mm ? $"  | 0x{mm:X6} antes {Hex(morig)} depois {Hex(mnow)}" : ""));
            if (back == null) Note($"0x{addr:X6}: leitura de volta falhou.");
            else if (!back.AsSpan().SequenceEqual(pat))
                Note($"0x{addr:X6}: ✗ o que se escreveu NÃO ficou (lido {Hex(back)}) — não há RAM utilizável aqui.");
            else if (mnow != null && morig != null && !mnow.AsSpan().SequenceEqual(morig) && mnow.AsSpan().SequenceEqual(pat))
                Note($"0x{addr:X6}: ⚠ ficou, mas apareceu também em 0x{mirror:X6} — a RAM REPETE-SE a cada 16 KB (só há 16 KB).");
            else
                Note($"0x{addr:X6}: ✓ RAM real e independente (escreve e lê de volta).");
        }
        finally
        {
            try { kwp.WriteMemoryByAddress(addr, orig); } catch (Kwp2000Exception) { Note($"0x{addr:X6}: AVISO não deu para repor o original."); }
            if (mirror is uint m2 && morig != null)
                try { kwp.WriteMemoryByAddress(m2, morig); } catch (Kwp2000Exception) { }
        }
    }

    private static string Hex(byte[]? b) => b == null ? "(n/d)" : Convert.ToHexString(b);

    /// <summary>
    /// Garante que o 0x23 é aceite. Na sessão 0x85 a ME7.3 não o aceita (NRC 0x11) e também recusa o
    /// 10 86 (NRC 0x12) — visto na Thesis depois de uma recusa ao 31. Ordem: sessão atual → 10 86 →
    /// terminar a comunicação (82), nova ligação (fast-init) e 10 86. A RAM mantém-se numa ligação nova.
    /// </summary>
    private void EnsureReadAccess(Kwp2000Interface kwp, int workingBaud)
    {
        if (CanRead(kwp, out var why)) { Note("ReadMemoryByAddress aceite na sessão atual."); return; }
        Note($"0x23 não aceite na sessão atual ({why}); a tentar a sessão 0x86 (só leitura).");

        try
        {
            kwp.StartDiagnosticSession(Kwp2000SessionType.Development, workingBaud);
            if (CanRead(kwp, out why)) { Note("Sessão 0x86 OK."); return; }
            Note($"Em 0x86 o 0x23 continua recusado ({why}).");
        }
        catch (Kwp2000NegativeResponseException ex)
        {
            Note($"A ECU não deixa passar a 0x86 a partir desta sessão ({ex.ResponseCode}).");
        }

        Note("A terminar a comunicação e a fazer uma ligação nova para entrar em 0x86 (a RAM não se perde)...");
        try { kwp.StopCommunication(); } catch (Kwp2000Exception) { /* a ECU pode já não responder */ }
        kwp.Connect(KLineInit.FastInit);
        kwp.StartDiagnosticSession(Kwp2000SessionType.Development, workingBaud);
        kwp.ReadMemoryByAddress(Kwp09BitsAddr, 4); // se falhar aqui, a exceção sobe com o motivo
        Note("Nova ligação em sessão 0x86: leitura OK.");
    }

    private static bool CanRead(Kwp2000Interface kwp, out string why)
    {
        try { kwp.ReadMemoryByAddress(Kwp09BitsAddr, 4); why = ""; return true; }
        catch (Kwp2000NegativeResponseException ex) { why = ex.ResponseCode.ToString(); return false; }
    }

    private byte[]? TryRead(Kwp2000Interface kwp, uint addr, int n)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try { return kwp.ReadMemoryByAddress(addr, n); }
            catch (Kwp2000NegativeResponseException) { return null; } // zona negada pela ECU
            catch (Kwp2000Exception) when (attempt == 0) { }          // timeout: tenta outra vez
            catch (Kwp2000Exception) { return null; }
        }
        return null;
    }

    private int? DecodeControl(byte?[] iram)
    {
        int? B(uint a) => iram[a - 0xE000] is byte v ? v : null;
        int? W(uint a) => B(a) is int lo && B(a + 1) is int hi ? lo | hi << 8 : null;
        string H(int? v, int digits) => v is int x ? "0x" + x.ToString("X" + digits) : "n/d";

        int? k09 = W(Kwp09BitsAddr);
        Note($"kwp09_Bits = {H(k09, 4)}" + (k09 is int k
            ? $" (bit0 cópia feita={k & 1}, bit1 permissão={(k >> 1) & 1}, bit2 passagem={(k >> 2) & 1}, bit3 em curso={(k >> 3) & 1})"
            : ""));
        Note($"flashorder = {H(B(FlashOrderAddr), 2)} (7 = pronto para o 31)");
        Note($"triggerOpModeChange = {H(W(TriggerOpModeChangeAddr), 4)} (8 = mudar para o programador)");
        Note($"umFpwBits = {H(W(UmFpwBitsAddr), 4)}");
        int? fs = B(FlashStateAddr);
        Note($"flashState = {H(fs, 2)}" + fs switch
        {
            0xAA => " (cabeçalho da flash válido — copia de 0x808048)",
            0xDD => " (usa o backup no sector 0x30000 — copia de 0x838048!)",
            0x55 => " (0x55)",
            _ => "",
        });
        return fs;
    }

    private static double Ratio(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length), eq = 0;
        for (int i = 0; i < n; i++) if (a[i] == b[i]) eq++;
        return n == 0 ? 0 : (double)eq / n;
    }

    private void Line(string s) => _detail.AppendLine(s);
    private void Note(string s) { _summary.Add(s); OnLog?.Invoke(s); }
}
