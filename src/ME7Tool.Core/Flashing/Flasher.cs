using static ME7Tool.Core.Common.Lang;
using ME7Tool.Core.Kwp2000;
using ME7Tool.Core.Transport;

namespace ME7Tool.Core.Flashing;

/// <summary>Uma zona contígua da flash a apagar/escrever (um ou mais sectores seguidos).</summary>
public readonly record struct FlashZone(uint Offset, int Length)
{
    public uint End => Offset + (uint)Length; // exclusivo
}

/// <summary>
/// Motor de flash ME7.3.xx por K-line, segundo a spec Fiat 07223 (§10 rotinas, §11 download):
/// <list type="number">
/// <item>Sessão de programação 0x85 (opcionalmente com baud de trabalho) + SecurityAccess.</item>
/// <item>Tester code (3B 98) e data de programação (3B 99) — OBRIGATÓRIOS e antes de apagar: a
/// aplicação da ME7.3 recusa o 31 sem eles (máquina de estados 'flashorder').</item>
/// <item>Por cada zona de sectores (dividida nas fronteiras da IROM): <c>31 02</c> apagar → <c>33 02</c>
/// até concluir → <c>34</c> requestDownload (lê o maxNumberOfBlockLength) → <c>36</c>… → <c>37</c>.</item>
/// <item>Opcional: <c>31 01</c> verificação por CRC-32 → <c>33 01</c> (algoritmo confirmado na IROM).</item>
/// </list>
/// Full ou parcial (o parcial exige o original e só grava os sectores alterados — R17).
/// O sector de boot (0x0000–0x3FFF) fica protegido por omissão.
/// </summary>
public sealed class Flasher
{
    private readonly Kwp2000Interface _kwp;
    private readonly FlashLayout _layout;
    private readonly FlashOptions _opt;
    private bool _inProgramming;

    public event Action<FlashProgress>? Progress;

    /// <summary>Mensagens de detalhe (respostas das rotinas, tamanhos de bloco, avisos).</summary>
    public Action<string>? OnLog { get; set; }

    /// <summary>Relatório do diagnóstico automático depois de uma recusa 0x22 ao 31 (para guardar em ficheiro).</summary>
    public Action<HandoverDiagnosticReport>? OnHandoverDiagnostic { get; set; }

    /// <summary>
    /// Mostra uma instrução ao utilizador (ex.: "desliga a ignição") e devolve uma função que diz se ele já
    /// confirmou. O flasher continua a correr (ex.: TesterPresent) enquanto espera.
    /// </summary>
    public Func<string, Func<bool>>? Instruct { get; set; }

    private DateTime? _date;
    private string? _tester;
    private CancellationToken _ct;
    private bool _handoverDone;   // o 1.º 31 já passou (ou já se religou ao programador)
    private bool _keyCycleDone;   // procedimento em 2 passos: o ciclo de chave já foi feito

    private byte[]? _image; // imagem a gravar (o diagnóstico compara o programador da RAM com ela)

    public FlashOptions Options => _opt;

    public Flasher(Kwp2000Interface kwp, FlashLayout layout, FlashOptions? options = null)
    {
        _kwp = kwp;
        _layout = layout;
        _opt = options ?? new FlashOptions();
    }

    // ------------------------------------------------- Fluxos de alto nível

    /// <summary>
    /// Teste sem escrever nada: entra na sessão de programação e faz o SecurityAccess.
    /// Serve para validar ligação, baud e seed/key em bancada antes do primeiro flash.
    /// </summary>
    public void TestProgrammingAccess()
    {
        if (_inProgramming)
        {
            Report(FlashPhase.Done, 1, 1, T("Já em sessão de programação nesta ligação (seed/key feito antes) — " +
                "para repetir o teste, desliga e liga de novo.",
                "Already in programming session on this connection (seed/key done before) — " +
                "to repeat the test, disconnect and connect again."));
            return;
        }
        EnterProgramming();
        Report(FlashPhase.Done, 1, 1, T("Sessão de programação e SecurityAccess OK (nada foi escrito).",
                                        "Programming session and SecurityAccess OK (nothing was written)."));
    }

    /// <summary>
    /// Flash completo: grava todos os sectores (exceto o de boot, se protegido).
    /// <paramref name="programmingDate"/> null = data de hoje; <paramref name="testerCode"/> null = mantém
    /// o tester code atual (lido da ECU). A ECU obriga a escrevê-los ANTES do primeiro apagar.
    /// </summary>
    public IReadOnlyList<FlashZone> ProgramFull(byte[] image, DateTime? programmingDate = null, string? testerCode = null,
        CancellationToken ct = default)
    {
        ValidateImage(image);
        var sectors = _layout.Sectors.Where(s => !IsProtected(s)).ToList();
        if (sectors.Count < _layout.Sectors.Count)
            Log("Sector de boot (0x0000–0x3FFF) protegido: não vai ser escrito.");
        var zones = PlanZones(_layout, sectors, out var notes);
        notes.ForEach(Log);
        Program(zones, image, programmingDate, testerCode, ct);
        return zones;
    }

    /// <summary>
    /// Flash parcial: exige o ficheiro ORIGINAL para diff; só grava os sectores alterados (mais os que a
    /// ECU obriga a regravar — ver <see cref="FlashLayout.SectorBackups"/>). Devolve as zonas gravadas.
    /// </summary>
    public IReadOnlyList<FlashZone> ProgramPartial(byte[] original, byte[] modified,
        DateTime? programmingDate = null, string? testerCode = null, CancellationToken ct = default)
    {
        ValidateImage(original);
        ValidateImage(modified);

        var changed = FlashDiff.ChangedSectors(_layout, original, modified);
        if (changed.Count == 0)
        {
            Report(FlashPhase.Done, 0, 0, "Sem diferenças — nada a gravar.");
            return Array.Empty<FlashZone>();
        }
        if (changed.Any(IsProtected))
            throw new InvalidOperationException(
                "Há diferenças no sector de boot (0x0000–0x3FFF), que está protegido. " +
                "Confirma o ficheiro; para o escrever mesmo assim, desliga a proteção do sector de boot.");

        var zones = PlanZones(_layout, changed, out var notes);
        notes.ForEach(Log);
        Program(zones, modified, programmingDate, testerCode, ct);
        return zones;
    }

    /// <summary>
    /// Flash completo Fiat por OBD, como o Examiner (ME7.3.1 Thesis/Stilo). O programador da Fiat só aceita:
    /// <list type="bullet">
    /// <item>apagar <c>31 02 80 00 00 87 FF FF</c> — início tem de ser 0x800000 (senão 7F 31 22); ele apaga
    /// sozinho dados (0x810000…), código (0x820000…) e arranque (0x800000…);</item>
    /// <item>um único download <c>34 80 00 00 01 08 00 00</c> (512 KB, dataFormat 0x01 = cifrado), com os
    /// dados cifrados pela <see cref="FiatCipher"/>;</item>
    /// <item>verificação <c>31 01 80 00 00 87 FF FF cs cs</c> (metade de cima do CRC-32 da imagem).</item>
    /// </list>
    /// A chave é a do programador que JÁ está na ECU (quem desencripta é ele): ler do ficheiro original.
    /// ATENÇÃO: se falhar depois do apagar, a ECU fica sem aplicação (recuperação por BSL).
    /// </summary>
    public void ProgramFiatFull(byte[] image, byte[]? key = null, DateTime? programmingDate = null, string? testerCode = null,
        CancellationToken ct = default)
    {
        ValidateImage(image);
        var prepared = FiatFullFlash.Prepare(image);
        ushort cs = FiatFullFlash.Checksum(prepared);
        var cipher = new FiatCipher(key);
        Log(T($"Flash completo Fiat: {FiatFullFlash.Size / 1024} KB cifrados, chave {Convert.ToHexString(cipher.Key.ToArray())}, checksum 0x{cs:X4}.",
              $"Fiat full flash: {FiatFullFlash.Size / 1024} KB encrypted, key {Convert.ToHexString(cipher.Key.ToArray())}, checksum 0x{cs:X4}."));

        _date = programmingDate; _tester = testerCode; _ct = ct; _image = image;
        EnterProgramming();
        bool firstErase = !_identDone;
        WriteIdentification(programmingDate, testerCode);
        if (firstErase && _opt.PauseBeforeEraseMs > 0) PauseWithTesterPresent(_opt.PauseBeforeEraseMs, ct);
        if (firstErase && _opt.TwoStepKeyCycle && !_keyCycleDone) KeyCycleBetweenSteps(ct);
        else if (firstErase && _opt.IgnitionOffHandover && !_handoverDone) AskIgnitionOff(ct);

        const long total = FiatFullFlash.Size;
        ct.ThrowIfCancellationRequested();
        Report(FlashPhase.Erasing, 0, total, T("A apagar a flash toda (o programador apaga dados, código e arranque)...",
                                               "Erasing the whole flash (the programmer erases data, code and boot)..."));
        int savedTimeout = _opt.RoutineTimeoutMs;
        _opt.RoutineTimeoutMs = Math.Max(savedTimeout, 180_000); // 512 KB de apagar
        try
        {
            var res = RunRoutine(0x02, Addr3(FiatFullFlash.Start).Concat(Addr3(FiatFullFlash.StopInclusive)).ToArray(), T("apagar tudo", "erase all"));
            Log(T($"Apagar 0x800000–0x87FFFF: concluído{ResultText(res)}. A partir daqui a ECU não tem aplicação até acabar.",
                  $"Erase 0x800000–0x87FFFF: done{ResultText(res)}. From here on the ECU has no application until it finishes."));
        }
        finally { _opt.RoutineTimeoutMs = savedTimeout; }

        Report(FlashPhase.Writing, 0, total, T("A escrever 512 KB (cifrados)...", "Writing 512 KB (encrypted)..."));
        var dlReq = Addr3(FiatFullFlash.Start).Append(FiatFullFlash.DataFormat).Concat(Addr3(FiatFullFlash.Size)).ToArray();
        var dl = _kwp.SendService(Kwp2000Service.RequestDownload, dlReq); // 34 80 00 00 01 08 00 00
        int block = BlockSizeFrom(dl);
        Log(T($"RequestDownload 0x800000 (512 KB, formato 0x01): blocos de {block} bytes",
              $"RequestDownload 0x800000 (512 KB, format 0x01): {block}-byte blocks") +
            (dl.Data.Length >= 2 ? $" (ECU: maxNumberOfBlockLength {dl.Data[1]})." : "."));
        if (_opt.DownloadSettleMs > 0) Thread.Sleep(_opt.DownloadSettleMs);

        cipher.Reset(); // o programador reinicia o estado no 34
        var buf = new byte[block];
        int sent = 0;
        while (sent < FiatFullFlash.Size)
        {
            ct.ThrowIfCancellationRequested();
            int chunk = Math.Min(block, FiatFullFlash.Size - sent);
            cipher.Encrypt(prepared.AsSpan(sent, chunk), buf.AsSpan(0, chunk));
            TransferData(buf.AsSpan(0, chunk), 0); // repetir após 0x21/0x23 manda os mesmos bytes cifrados
            sent += chunk;
            Report(FlashPhase.Writing, sent, total, T("A escrever 512 KB (cifrados)", "Writing 512 KB (encrypted)"));
        }
        _kwp.SendService(Kwp2000Service.RequestTransferExit);

        Report(FlashPhase.Verifying, total, total, T($"A verificar (checksum 0x{cs:X4})...", $"Verifying (checksum 0x{cs:X4})..."));
        try
        {
            var p = Addr3(FiatFullFlash.Start).Concat(Addr3(FiatFullFlash.StopInclusive)).Concat(new[] { (byte)(cs >> 8), (byte)cs }).ToArray();
            var res = RunRoutine(0x01, p, "checksum");
            Log(T($"Checksum 0x{cs:X4}: ECU confirmou{ResultText(res)}.", $"Checksum 0x{cs:X4}: confirmed by the ECU{ResultText(res)}."));
        }
        catch (Kwp2000NegativeResponseException ex)
        {
            throw new InvalidOperationException(
                T($"A escrita terminou mas a ECU recusou a checksum 0x{cs:X4} ({ex.ResponseCode}). A flash pode estar mal " +
                  "escrita: NÃO desligues; tenta o flash completo outra vez. Se não der, recupera por BSL.",
                  $"Writing finished but the ECU rejected checksum 0x{cs:X4} ({ex.ResponseCode}). The flash may be badly " +
                  "written: DON'T power off; try the full flash again. If that fails, recover by BSL."), ex);
        }
        Report(FlashPhase.Done, 1, 1, T("Concluído. Desliga e volta a ligar a ignição para a ECU arrancar o software novo.",
                                        "Done. Turn the ignition off and on again so the ECU starts the new software."));
    }

    /// <summary>Pré-visualização do diff (para mostrar ao utilizador antes de gravar).</summary>
    public List<SectorDiff> PreviewDiff(byte[] original, byte[] modified)
        => FlashDiff.Compare(_layout, original, modified);

    /// <summary>
    /// Plano de zonas a apagar/escrever (o mesmo que o motor usa — a app mostra-o antes de gravar):
    /// junta os sectores que a ECU obriga a regravar (<see cref="FlashLayout.SectorBackups"/>) e divide
    /// as zonas nas fronteiras que a IROM exige (<see cref="FlashLayout.EraseBoundaries"/>).
    /// </summary>
    public static List<FlashZone> PlanZones(FlashLayout layout, IEnumerable<FlashSector> sectors, out List<string> notes)
    {
        notes = new List<string>();
        var set = sectors.ToList();
        foreach (var (erased, also) in layout.SectorBackups)
        {
            if (set.Any(s => s.Offset == erased) && !set.Any(s => s.Offset == also) && layout.SectorAt(also) is FlashSector extra)
            {
                set.Add(extra);
                notes.Add($"O sector 0x{also:X5} também vai ser regravado: ao apagar o 0x{erased:X5} a ECU " +
                          $"usa-o como backup e destrói-o.");
            }
        }
        return Zones(set, layout.EraseBoundaries);
    }

    /// <summary>
    /// Junta sectores seguidos em zonas contíguas (uma rotina de apagar + um download por zona), sem
    /// atravessar as <paramref name="boundaries"/> (offsets onde uma zona nova tem de começar).
    /// </summary>
    public static List<FlashZone> Zones(IEnumerable<FlashSector> sectors, IReadOnlyCollection<uint>? boundaries = null)
    {
        var zones = new List<FlashZone>();
        foreach (var s in sectors.OrderBy(s => s.Offset))
        {
            bool split = boundaries?.Contains(s.Offset) == true;
            if (zones.Count > 0 && zones[^1].End == s.Offset && !split)
                zones[^1] = new FlashZone(zones[^1].Offset, zones[^1].Length + s.Length);
            else
                zones.Add(new FlashZone(s.Offset, s.Length));
        }
        return zones;
    }

    // ------------------------------------------------- Passos

    /// <summary>Sequência obrigatória: sessão + segurança → tester code + data → zonas.</summary>
    private void Program(IReadOnlyList<FlashZone> zones, byte[] image, DateTime? programmingDate, string? testerCode,
        CancellationToken ct)
    {
        _date = programmingDate; _tester = testerCode; _ct = ct;
        EnterProgramming();
        bool firstErase = !_identDone;
        WriteIdentification(programmingDate, testerCode);
        if (firstErase && _opt.PauseBeforeEraseMs > 0) PauseWithTesterPresent(_opt.PauseBeforeEraseMs, ct);
        if (firstErase && _opt.TwoStepKeyCycle && !_keyCycleDone) KeyCycleBetweenSteps(ct);
        else if (firstErase && _opt.IgnitionOffHandover && !_handoverDone) AskIgnitionOff(ct);
        FlashZones(zones, image, ct);
        Report(FlashPhase.Done, 1, 1, "Concluído. Desliga e volta a ligar a ignição para a ECU arrancar o software novo.");
    }

    private void EnterProgramming()
    {
        if (_inProgramming) return;

        // O tester code atual lê-se ANTES da sessão 0x85 (para o manter, se não for indicado outro).
        if (!_identDone)
        {
            try { _currentTesterCode = _kwp.ReadEcuIdentification(EcuIdentificationOption.TesterCode); }
            catch (Kwp2000Exception ex) { Log(T($"Não deu para ler o tester code atual ({ex.Message}).", $"Could not read the current tester code ({ex.Message}).")); }
            try { _currentDate = _kwp.ReadEcuIdentification(EcuIdentificationOption.ProgrammingDate); }
            catch (Kwp2000Exception ex) { Log(T($"Não deu para ler a data de programação atual ({ex.Message}).", $"Could not read the current programming date ({ex.Message}).")); }
        }

        Report(FlashPhase.Connecting, 0, 0, T("A entrar na sessão de programação (0x85)...", "Entering programming session (0x85)..."));
        _kwp.StartDiagnosticSession(_opt.ProgrammingSession, _opt.WorkingBaud);

        if (_opt.NegotiateTiming)
        {
            try { _kwp.NegotiateFastTiming(); }
            catch (Kwp2000Exception ex) { Log(T($"Timings por omissão ({ex.Message}).", $"Default timings ({ex.Message}).")); }
        }

        Report(FlashPhase.Unlocking, 0, 0, "SecurityAccess (seed/key)...");
        try { _kwp.SecurityAccessUnlock(_opt.SeedSubFunction, _opt.SeedParameter, _opt.PerEcuPoly); }
        catch (Kwp2000NegativeResponseException ex) when (ex.ResponseCode == Kwp2000ResponseCode.ConditionsNotCorrectOrRequestSequenceError)
        {
            throw new InvalidOperationException(
                T("A ECU recusou o SecurityAccess com 'condições não cumpridas' (NRC 0x22). Na ME7.3 isto " +
                  "acontece quando o imobilizador não autorizou. Só se programa com o imobilizador funcional (ECU no " +
                  "carro, ignição ligada com a chave reconhecida) ou com a centralina virgem. Nada foi apagado.",
                  "The ECU rejected SecurityAccess with 'conditions not correct' (NRC 0x22). On the ME7.3 this " +
                  "happens when the immobilizer has not authorized. Programming only works with a working immobilizer " +
                  "(ECU in the car, ignition on with the key recognized) or with a virgin ECU. Nothing was erased."), ex);
        }
        _inProgramming = true;
    }

    private byte[]? _currentTesterCode;
    private byte[]? _currentDate;
    private bool _identDone;

    /// <summary>
    /// Esquece o estado da sessão de programação (a próxima operação volta a fazer 10 85, seed/key e 3B).
    /// Usar quando outra coisa mudou a sessão (ex.: o diagnóstico religou em 0x86).
    /// </summary>
    public void ForgetSession() { _inProgramming = false; _identDone = false; _handoverDone = false; _keyCycleDone = false; }

    /// <summary>
    /// Tester code (3B 98) e data de programação (3B 99), por esta ordem e ANTES do primeiro apagar.
    /// A aplicação da ME7.3 só aceita o 31 01/02 depois destes dois (máquina de estados 'flashorder':
    /// security → 5, 3B 98 → 6, 3B 99 → 7; IROM/aplicação, ver handoff §11). Depois do primeiro 31 a
    /// ECU passa ao programador da IROM, que já não tem o 0x3B — por isso não dá para os escrever no fim.
    /// </summary>
    private void WriteIdentification(DateTime? programmingDate, string? testerCode)
    {
        if (_identDone) return;
        Report(FlashPhase.Finalizing, 0, 0, T("A escrever tester code e data de programação (obrigatório antes de apagar)...",
                                              "Writing tester code and programming date (required before erasing)..."));

        bool keep = _opt.KeepEcuIdentification;
        if (keep) testerCode = null; // usa o lido da ECU
        try
        {
            if (testerCode != null)
                _kwp.WriteTesterCode(testerCode);
            else if (_currentTesterCode is { Length: > 0 } cur)
            {
                _kwp.WriteTesterCodeRaw(cur);
                Log(T($"Tester code mantido: {Printable(cur)}.", $"Tester code kept: {Printable(cur)}."));
            }
            else
                throw new InvalidOperationException(
                    T("Não foi possível ler o tester code atual da ECU e não foi indicado nenhum. " +
                      "Escreve um tester code (até 10 caracteres) — a ECU exige-o antes de apagar.",
                      "Could not read the current tester code from the ECU and none was given. " +
                      "Enter a tester code (up to 10 characters) — the ECU requires it before erasing."));
        }
        catch (Kwp2000NegativeResponseException ex) when (ex.ResponseCode is
            Kwp2000ResponseCode.ServiceNotSupported or Kwp2000ResponseCode.ServiceNotSupportedInActiveSession)
        {
            // Sem 0x3B = a ECU está só com o programador da IROM (aplicação inválida, ex.: flash
            // interrompido). Aí não há flashorder: o 31 é aceite só com a segurança feita.
            Log(T("A ECU não tem o serviço 0x3B: está no programador da IROM (sem aplicação válida, ex.: depois " +
                  "de um flash interrompido). Tester code e data saltados — segue para apagar/escrever.",
                  "The ECU has no 0x3B service: it is in the IROM programmer (no valid application, e.g. after " +
                  "an interrupted flash). Tester code and date skipped — going on to erase/write."));
            _identDone = true;
            return;
        }

        if (keep && _currentDate is { Length: 4 } curDate)
        {
            _kwp.SendService(Kwp2000Service.WriteDataByLocalIdentifier,
                new byte[] { (byte)EcuIdentificationOption.ProgrammingDate }.Concat(curDate).ToArray());
            Log(T($"Data de programação mantida: {Convert.ToHexString(curDate)} (BCD).", $"Programming date kept: {Convert.ToHexString(curDate)} (BCD)."));
            _identDone = true;
            return;
        }
        if (keep) Log(T("Não deu para ler a data atual da ECU: escreve-se a data indicada.", "Could not read the ECU's current date: writing the given date."));
        var date = programmingDate ?? DateTime.Today;
        _kwp.WriteProgrammingDate(date);
        Log(T($"Data de programação: {date:yyyy-MM-dd}.", $"Programming date: {date:yyyy-MM-dd}."));
        _identDone = true;
    }

    /// <summary>
    /// Procedimento Fiat em 2 passos (fluxo do Examiner): fim do passo 1 → chave OFF sem comunicação durante
    /// o power latch → chave ON → ligação nova e passo 2 (sessão + seed/key + 3B se ainda for a aplicação).
    /// </summary>
    private void KeyCycleBetweenSteps(CancellationToken ct)
    {
        if (Instruct == null) { Log("Procedimento em 2 passos pedido, mas sem interface para instruções — ignorado."); return; }
        Log("Passo 1 do procedimento Fiat concluído (sessão 0x85, seed/key, tester code e data).");
        try { _kwp.StopCommunication(); } catch (Kwp2000Exception) { /* a ECU pode já não responder */ }

        WaitFor(Instruct(
            "PROCEDIMENTO FIAT — passo 1 concluído\n\n" +
            "Desliga AGORA a chave (OFF).\n" +
            "Não a voltes a ligar até o flasher pedir.\n\n" +
            "Carrega OK assim que a desligares."), ct, "desligar a chave");

        int secs = Math.Max(0, _opt.KeyOffWaitSeconds);
        Report(FlashPhase.Connecting, 0, 0, $"Chave OFF: a esperar {secs} s pelo pós-funcionamento da ECU (sem comunicação)...");
        for (int left = secs; left > 0; left--)
        {
            ct.ThrowIfCancellationRequested();
            if (left % 10 == 0 || left <= 5) Log($"Chave OFF: faltam {left} s.");
            Thread.Sleep(1000);
        }

        WaitFor(Instruct(
            "PROCEDIMENTO FIAT — passo 2\n\n" +
            "Liga AGORA a chave (ON).\n\n" +
            "Carrega OK depois de ligares."), ct, "ligar a chave");

        Report(FlashPhase.Connecting, 0, 0, "A ligar de novo à ECU (passo 2)...");
        _kwp.Connect(KLineInit.FastInit);
        // Key bytes = os da última inicialização KWP (0xE1C2/0xE1C3): DF 8F = só a BootROM (IROM 0x2354);
        // EF 8F = aplicação (0x828DE8) OU programador da Fiat (0x8082FC); E9 8F = aplicação, ligação OBD-II (0x33).
        string kb = Convert.ToHexString(_kwp.KeyBytes);
        Log($"Ligado de novo: ECU 0x{_kwp.EcuAddress:X2}, key bytes {kb}" + kb switch
        {
            "DF8F" => " → só a BootROM está a atender (sem aplicação nem programador: não vai dar para escrever).",
            "EF8F" => " → aplicação ou programador da Fiat (os key bytes não os distinguem).",
            _ => ".",
        });
        // Distinguir aplicação de programador: a aplicação responde à leitura de DTC (18); o programador não.
        try
        {
            var dtcs = _kwp.ReadDtcByStatus();
            Log($"A ECU respondeu à leitura de DTC ({dtcs.Count} DTC) → é a APLICAÇÃO (não arrancou no programador).");
        }
        catch (Kwp2000NegativeResponseException ex)
        {
            Log($"A ECU recusou a leitura de DTC ({ex.ResponseCode}) → não é a aplicação normal (programador?).");
        }
        catch (Kwp2000Exception ex) { Log($"Leitura de DTC sem resposta ({ex.Message})."); }
        _keyCycleDone = true;
        _inProgramming = false; _identDone = false;
        EnterProgramming();
        WriteIdentification(_date, _tester);   // no programador da IROM o 3B não existe → é saltado
    }

    private static void WaitFor(Func<bool> done, CancellationToken ct, string what)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            ct.ThrowIfCancellationRequested();
            if (sw.Elapsed > TimeSpan.FromMinutes(5)) throw new TimeoutException($"Sem confirmação para {what} em 5 min. Nada foi apagado.");
            Thread.Sleep(200);
        }
    }

    /// <summary>
    /// Passo 1 do procedimento Fiat: pede para desligar a ignição (KL15) mantendo a KL30, e espera pela
    /// confirmação com TesterPresent a cada segundo. Se a ECU deixar de responder com a ignição desligada,
    /// pára sem ter apagado nada.
    /// </summary>
    private void AskIgnitionOff(CancellationToken ct)
    {
        if (Instruct == null) { Log("Passagem com ignição desligada pedida, mas sem interface para instruções — ignorado."); return; }
        const string msg =
            "PROCEDIMENTO FIAT — passo 1 de 2\n\n" +
            "Desliga AGORA a ignição (chave em OFF / KL15).\n" +
            "Mantém a alimentação permanente (KL30, +12 V) ligada.\n\n" +
            "Carrega OK depois de desligares a ignição.";
        Report(FlashPhase.Finalizing, 0, 0, "À espera: desliga a ignição (chave OFF) e carrega OK...");
        var done = Instruct(msg);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            ct.ThrowIfCancellationRequested();
            if (sw.Elapsed > TimeSpan.FromMinutes(3))
                throw new TimeoutException("Não houve confirmação para desligar a ignição em 3 min. Nada foi apagado.");
            Thread.Sleep(1000);
            try { _kwp.TesterPresent(true); }
            catch (Kwp2000Exception ex)
            {
                throw new InvalidOperationException(
                    "A ECU deixou de responder enquanto esperava (ignição desligada?): " + ex.Message +
                    ". Nada foi apagado. Diz-me que isto aconteceu — quer dizer que a ECU fecha a comunicação " +
                    "com a ignição desligada e é preciso outra ordem.", ex);
            }
        }
        // confirmar que a ECU continua acordada com a ignição desligada
        try { _kwp.TesterPresent(true); }
        catch (Kwp2000Exception ex)
        {
            throw new InvalidOperationException(
                "A ECU deixou de responder ao desligar a ignição: " + ex.Message + ". Nada foi apagado.", ex);
        }
        Log("Ignição desligada e a ECU continua a responder — a pedir o apagar (passagem ao programador)...");
    }

    /// <summary>
    /// Passo 2 do procedimento Fiat: o 1.º 31 ficou sem resposta (a ECU reiniciou no programador). Pede para
    /// voltar a ligar a ignição, volta a ligar-se (fast-init), entra em 0x85 + segurança (e 3B, se a ECU ainda
    /// for a aplicação) e deixa o 31 ser repetido.
    /// </summary>
    private void ReconnectToProgrammer(CancellationToken ct)
    {
        Log("A ECU deixou de responder depois do pedido de apagar — deve ter reiniciado no programador da Fiat.");
        if (Instruct != null)
        {
            var done = Instruct(
                "PROCEDIMENTO FIAT — passo 2 de 2\n\n" +
                "A ECU passou ao programador (deixou de responder).\n" +
                "Volta a LIGAR a ignição (chave em ON).\n\n" +
                "Carrega OK depois de ligares a ignição.");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!done())
            {
                ct.ThrowIfCancellationRequested();
                if (sw.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException("Sem confirmação para ligar a ignição.");
                Thread.Sleep(200);
            }
        }
        try { _kwp.StopCommunication(); } catch (Kwp2000Exception) { }
        Report(FlashPhase.Connecting, 0, 0, "A voltar a ligar à ECU (programador)...");
        _kwp.Connect(KLineInit.FastInit);
        Log($"Ligado de novo (ECU 0x{_kwp.EcuAddress:X2}, key bytes {Convert.ToHexString(_kwp.KeyBytes)}).");
        _inProgramming = false; _identDone = false; // se ainda for a aplicação, os 3B voltam a ser precisos
        EnterProgramming();
        WriteIdentification(_date, _tester);        // no programador o 3B não existe → é saltado
        _handoverDone = true;
    }

    /// <summary>Pausa com TesterPresent (3E 01) a cada segundo para a ECU não fechar a sessão.</summary>
    private void PauseWithTesterPresent(int ms, CancellationToken ct)
    {
        Log(T($"Pausa de {ms} ms antes de apagar (com TesterPresent)...", $"Pausing {ms} ms before erasing (with TesterPresent)..."));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep((int)Math.Min(1000, Math.Max(0, ms - sw.ElapsedMilliseconds)));
            if (sw.ElapsedMilliseconds < ms) _kwp.TesterPresent(true);
        }
    }

    private static string Printable(byte[] d)
        => d.All(c => c is >= 0x20 and < 0x7F) ? $"\"{System.Text.Encoding.ASCII.GetString(d).TrimEnd()}\"" : Convert.ToHexString(d);

    private void FlashZones(IReadOnlyList<FlashZone> zones, byte[] image, CancellationToken ct)
    {
        long total = zones.Sum(z => (long)z.Length);
        long done = 0;
        _image = image;

        foreach (var z in zones)
        {
            ct.ThrowIfCancellationRequested();
            uint start = _layout.BaseAddress + z.Offset;
            uint stop = _layout.BaseAddress + z.End - (_opt.StopAddressInclusive ? 1u : 0u);
            string name = $"0x{z.Offset:X5}–0x{z.End - 1:X5} ({z.Length / 1024} KB)";

            if (_opt.EraseBeforeWrite)
            {
                Report(FlashPhase.Erasing, done, total, $"A apagar {name}...");
                var res = RunRoutine(0x02, Addr3(start).Concat(Addr3(stop)).ToArray(), "apagar");
                Log($"Apagar {name}: concluído{ResultText(res)}.");
            }

            // requestDownload: 34 a2 a1 a0 fmt s2 s1 s0 → 74 maxNumberOfBlockLength
            Report(FlashPhase.Writing, done, total, $"A escrever {name}...");
            var dl = _kwp.SendService(Kwp2000Service.RequestDownload,
                (byte)(start >> 16), (byte)(start >> 8), (byte)start, _opt.DataFormat,
                (byte)(z.Length >> 16), (byte)(z.Length >> 8), (byte)z.Length);
            int block = BlockSizeFrom(dl);
            Log($"RequestDownload {name}: blocos de {block} bytes" +
                (dl.Data.Length >= 2 ? $" (ECU: maxNumberOfBlockLength {dl.Data[1]})." : "."));
            // A tarefa de fundo da IROM só arma a escrita no ciclo seguinte; um 36 antes disso leva NRC 0x10.
            if (_opt.DownloadSettleMs > 0) Thread.Sleep(_opt.DownloadSettleMs);

            int sent = 0, counter = 1;
            while (sent < z.Length)
            {
                ct.ThrowIfCancellationRequested();
                int chunk = Math.Min(block, z.Length - sent);
                var data = image.AsSpan((int)z.Offset + sent, chunk);
                TransferData(data, counter++);
                sent += chunk;
                done += chunk;
                Report(FlashPhase.Writing, done, total, $"A escrever {name}");
            }
            _kwp.SendService(Kwp2000Service.RequestTransferExit);

            if (_opt.VerifyChecksum) Verify(z, start, stop, image, done, total);
        }
    }

    /// <summary>
    /// Verificação pós-escrita pela rotina 0x31 0x01 da ECU. Formato confirmado na desmontagem da
    /// IROM (sub_3EA2 / sub_5BE6, ME7.3.1 Alfa e Thesis):
    /// <code>31 01 s2 s1 s0 e2 e1 e0 c3 c2 c1 c0</code>
    /// (início 3 bytes, fim inclusivo 3 bytes, CRC-32 esperado 4 bytes big-endian). A ECU calcula o
    /// CRC-32 (poly 0xEDB88320, init/xorout 0xFFFFFFFF) sobre [início..fim] e compara com o esperado;
    /// responde 71 01 e depois 73 01 (OK) ou NRC. É o único meio de verificação: não há upload (0x35).
    /// </summary>
    private void Verify(FlashZone z, uint start, uint stop, byte[] image, long done, long total)
    {
        uint crc = Crc32(image.AsSpan((int)z.Offset, z.Length));
        Report(FlashPhase.Verifying, done, total, $"CRC-32 0x{z.Offset:X5} (esperado 0x{crc:X8})...");
        try
        {
            var p = Addr3(start).Concat(Addr3(stop))
                .Concat(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc }).ToArray();
            var res = RunRoutine(0x01, p, "CRC-32");
            Log($"CRC-32 0x{z.Offset:X5}: ECU confirmou 0x{crc:X8}{ResultText(res)}.");
        }
        catch (Kwp2000NegativeResponseException ex)
        {
            string msg = $"CRC-32 0x{z.Offset:X5}: ECU respondeu {ex.ResponseCode} (0x{(byte)ex.ResponseCode:X2}). " +
                         "O CRC-32 da zona escrita não bate com a flash — a escrita pode ter falhado.";
            if (_opt.ChecksumFailureIsError) throw new InvalidOperationException(msg, ex);
            Log("AVISO " + msg);
        }
    }

    // ------------------------------------------------- Rotinas (31/33)

    /// <summary>
    /// startRoutineByLocalIdentifier (31 id params) e depois requestRoutineResults (33 id) até a
    /// ECU deixar de responder routineNotComplete (0x23) / busy (0x21). Devolve os routineResults.
    /// </summary>
    private byte[] RunRoutine(byte id, byte[] parameters, string what)
    {
        var p = new byte[1 + parameters.Length];
        p[0] = id;
        parameters.CopyTo(p, 1);
        // O primeiro 31 na aplicação responde 7F 31 78 e passa o controlo ao programador, que só
        // depois envia o 71 — daí o timeout mais longo para este pedido.
        bool handover = _opt.IgnitionOffHandover && !_handoverDone;
        int timeout = handover ? _opt.HandoverTimeoutMs : _opt.RoutineStartTimeoutMs;
        try
        {
            try { _kwp.SendServiceWithTimeout(timeout, Kwp2000Service.StartRoutineByLocalIdentifier, p); }
            catch (Kwp2000Exception ex) when (handover && ex is not Kwp2000NegativeResponseException)
            {
                // Sem resposta com a ignição desligada = a ECU reiniciou no programador (procedimento Fiat).
                ReconnectToProgrammer(_ct);
                _kwp.SendServiceWithTimeout(_opt.RoutineStartTimeoutMs, Kwp2000Service.StartRoutineByLocalIdentifier, p);
            }
            if (handover) { _handoverDone = true; Log(T("Passagem ao programador aceite.", "Handover to the programmer accepted.")); }
        }
        catch (Kwp2000NegativeResponseException ex) when (ex.ResponseCode == Kwp2000ResponseCode.ConditionsNotCorrectOrRequestSequenceError)
        {
            // Thesis/Stilo: a passagem ao programador da Fiat funciona (confirmado: programador inteiro na
            // XRAM depois da recusa); quem responde 7F 31 22 é o PROGRAMADOR, porque o apagar tem de começar
            // em 0x800000 (flash completo). Também pode vir da aplicação se a cópia falhar a verificação.
            if (_opt.DiagnoseOnHandoverFailure && _image != null)
            {
                Log("A correr o diagnóstico (só leitura) enquanto a ECU ainda responde...");
                try
                {
                    var report = new HandoverDiagnostic { OnLog = m => Log("DIAG " + m) }
                        .Run(_kwp, _image, _opt.WorkingBaud);
                    OnHandoverDiagnostic?.Invoke(report);
                }
                catch (Exception dex) { Log($"Diagnóstico interrompido: {dex.Message}"); }
                ForgetSession(); // o diagnóstico pode ter mudado de sessão / religado
            }
            throw new InvalidOperationException(
                T($"A ECU recusou o '{what}' (7F 31 22). Na Thesis/Stilo o programador da Fiat só aceita o flash " +
                  "COMPLETO (apagar a partir de 0x800000, download único de 512 KB cifrado). Flash parcial só por BSL. " +
                  "Nada foi apagado. O programador fica a correr: para tentar outra vez corta a alimentação da ECU " +
                  "(ou desliga a ignição até ela se desligar).",
                  $"The ECU rejected '{what}' (7F 31 22). On the Thesis/Stilo the Fiat programmer only accepts a FULL " +
                  "flash (erase from 0x800000, single encrypted 512 KB download). Partial flashing only by BSL. " +
                  "Nothing was erased. The programmer keeps running: to try again, cut the ECU's power " +
                  "(or switch the ignition off until it shuts down)."), ex);
        }

        // A IROM só começa a rotina no ciclo seguinte da tarefa de fundo; um 33 imediato podia apanhar o
        // estado "não começou" (NRC 0x10, igual ao de erro).
        Thread.Sleep(_opt.RoutinePollMs);

        var deadline = DateTime.UtcNow.AddMilliseconds(_opt.RoutineTimeoutMs);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        int pending = 0;
        while (true)
        {
            try
            {
                var r = _kwp.SendService(Kwp2000Service.RequestRoutineResultsByLocalIdentifier, id);
                if (pending > 0) Log(T($"Rotina '{what}': concluída ao fim de {waited.Elapsed.TotalSeconds:0.0} s ({pending}× 'em curso').",
                                       $"Routine '{what}': finished after {waited.Elapsed.TotalSeconds:0.0} s ({pending}× 'in progress')."));
                return r.Data.Length > 2 ? r.Data[2..] : Array.Empty<byte>();
            }
            catch (Kwp2000NegativeResponseException ex) when (ex.ResponseCode is
                Kwp2000ResponseCode.RoutineNotCompleteOrServiceInProgress or Kwp2000ResponseCode.BusyRepeatRequest)
            {
                if (pending++ == 0) Log(T($"Rotina '{what}': em curso, a aguardar...", $"Routine '{what}': in progress, waiting..."));
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException(T($"A rotina '{what}' não terminou em {_opt.RoutineTimeoutMs / 1000} s.",
                                                 $"Routine '{what}' did not finish within {_opt.RoutineTimeoutMs / 1000} s."));
                Thread.Sleep(_opt.RoutinePollMs);
            }
            catch (Kwp2000NegativeResponseException ex) when (ex.ResponseCode == Kwp2000ResponseCode.ServiceNotSupported)
            {
                // ECU sem 33: o 31 já foi aceite (e com responsePending a ECU só responde no fim).
                Log(T($"AVISO: a ECU não suporta requestRoutineResults (0x33); assume-se '{what}' concluída.",
                      $"WARNING: the ECU does not support requestRoutineResults (0x33); assuming '{what}' finished."));
                return Array.Empty<byte>();
            }
        }
    }

    /// <summary>
    /// 36 com os dados. Se a ECU ainda estiver a programar o bloco anterior pode responder NRC 0x23/0x21
    /// (o bloco NÃO foi aceite) — espera e repete o mesmo bloco.
    /// </summary>
    private void TransferData(ReadOnlySpan<byte> data, int blockCounter)
    {
        byte[] p;
        if (_opt.UseBlockSequenceCounter)
        {
            p = new byte[data.Length + 1];
            p[0] = (byte)blockCounter;
            data.CopyTo(p.AsSpan(1));
        }
        else p = data.ToArray();

        for (int attempt = 1; ; attempt++)
        {
            try { _kwp.SendService(Kwp2000Service.TransferData, p); return; }
            catch (Kwp2000NegativeResponseException ex) when (attempt < _opt.TransferRetries && ex.ResponseCode is
                Kwp2000ResponseCode.RoutineNotCompleteOrServiceInProgress or Kwp2000ResponseCode.BusyRepeatRequest)
            {
                Log(T($"TransferData: ECU ocupada ({ex.ResponseCode}); a repetir o bloco ({attempt}).",
                      $"TransferData: ECU busy ({ex.ResponseCode}); repeating the block ({attempt})."));
                Thread.Sleep(_opt.RoutinePollMs);
            }
        }
    }

    /// <summary>
    /// Dados por TransferData: o mínimo entre a opção e (maxNumberOfBlockLength − 1), sempre PAR — a IROM
    /// escreve palavras de 16 bits e um bloco ímpar levava um byte a mais (IROM sub_513C).
    /// </summary>
    private int BlockSizeFrom(Kwp2000Message resp)
    {
        int block = _opt.TransferBlockSize;
        if (resp.Data.Length >= 2 && resp.Data[1] > 1)
            block = Math.Min(block, resp.Data[1] - 1 - (_opt.UseBlockSequenceCounter ? 1 : 0));
        return Math.Clamp(block, 2, 254) & ~1;
    }

    // ------------------------------------------------- Helpers

    /// <summary>
    /// CRC-32 (poly refletido 0xEDB88320, init 0xFFFFFFFF, xorout 0xFFFFFFFF — o CRC-32 do zip/zlib),
    /// byte a byte, igual ao que a rotina 0x31 0x01 da ECU calcula sobre a zona (sub_5BE6 da IROM).
    /// </summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }

    private bool IsProtected(FlashSector s) => _opt.ProtectBootSector && s.Offset < 0x4000;

    private static byte[] Addr3(uint a) => new[] { (byte)(a >> 16), (byte)(a >> 8), (byte)a };

    private static string ResultText(byte[] r) => r.Length > 0 ? T($" (resultado {Convert.ToHexString(r)})", $" (result {Convert.ToHexString(r)})") : "";

    private void ValidateImage(byte[] image)
    {
        if (image.Length != _layout.TotalSize)
            throw new ArgumentException(
                T($"Tamanho da imagem ({image.Length}) != tamanho do mapa de flash ({_layout.TotalSize}).",
                  $"Image size ({image.Length}) != flash map size ({_layout.TotalSize})."));
    }

    private void Log(string m) => OnLog?.Invoke(m);

    private void Report(FlashPhase phase, long cur, long total, string msg)
    {
        Progress?.Invoke(new FlashProgress(phase, cur, total, msg));
    }
}
