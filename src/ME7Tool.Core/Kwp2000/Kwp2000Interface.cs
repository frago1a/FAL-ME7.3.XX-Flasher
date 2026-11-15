using static ME7Tool.Core.Common.Lang;
using System.Diagnostics;
using ME7Tool.Core.Logging;
using ME7Tool.Core.Transport;

namespace ME7Tool.Core.Kwp2000;

/// <summary>
/// Máquina de estados KWP2000 sobre um <see cref="ICommunicationDevice"/> de bytes (K-line).
/// Trata init, framing, timing, echo, responsePending (0x78), sessão e SecurityAccess.
/// Lógica pura — testável com um dispositivo falso.
/// </summary>
public sealed class Kwp2000Interface : ILogChannel
{
    private readonly ICommunicationDevice _device;

    /// <summary>Interface que faz o init/framing sozinho (J2534 ISO14230), ou null (FTDI/série).</summary>
    private readonly IKLineFrameDevice? _frameDevice;

    public KwpTiming Timing { get; }
    public byte EcuAddress { get; set; }
    public byte TesterAddress { get; set; }

    /// <summary>
    /// Em K-line de fio único, o que enviamos volta como eco. true = ler e descartar o eco
    /// após cada escrita. Backends que já removem o eco (alguns J2534) devem pôr false.
    /// </summary>
    public bool ExpectEcho { get; set; } = true;

    public Kwp2000SessionType CurrentSession { get; private set; } = Kwp2000SessionType.Standard;
    public byte[] KeyBytes { get; private set; } = Array.Empty<byte>();

    /// <summary>
    /// Endereços-alvo a tentar no fast-init, por ordem (como o NefMoto). A Fiat responde em 0x10.
    /// Após ligar, <see cref="EcuAddress"/> fica com o que respondeu.
    /// </summary>
    public byte[] FastInitTargets { get; set; } = { 0x10, 0x01, 0x11 };

    /// <summary>Timeout (ms) por endereço durante o scan de fast-init.</summary>
    public int FastInitResponseTimeoutMs { get; set; } = 400;

    /// <summary>
    /// Baud do fast-init e do StartCommunication — a norma KWP2000 exige 10400 aqui.
    /// Depois de ligar, se <see cref="KwpTiming.BaudRate"/> for diferente, muda-se para esse
    /// (baud de trabalho/logging mais rápido).
    /// </summary>
    public int InitBaudRate { get; set; } = 10400;

    /// <summary>Callback de diagnóstico (passos da ligação, bytes, etc.).</summary>
    public Action<string>? OnLog { get; set; }
    private void LogMsg(string m) => OnLog?.Invoke(m);

    public Kwp2000Interface(ICommunicationDevice device, KwpTiming? timing = null,
        byte ecuAddress = 0x11, byte testerAddress = 0xF1)
    {
        _device = device;
        _frameDevice = device as IKLineFrameDevice;
        if (_frameDevice != null) ExpectEcho = false; // o interface J2534 não devolve o eco
        Timing = timing ?? KwpTiming.Normal();
        EcuAddress = ecuAddress;
        TesterAddress = testerAddress;
    }

    // ---------------------------------------------------------------- Init

    /// <summary>
    /// Inicialização. Fast-init: tenta os endereços de <see cref="FastInitTargets"/> (cada um
    /// <see cref="FastInitAttemptsPerTarget"/> vezes) com StartCommunication. Slow init (5 baud):
    /// envia o endereço a 5 baud e lê sync 0x55 + key bytes (ISO 14230-2 §5.2.4.2). Guarda os key bytes.
    /// </summary>
    public void Connect(KLineInit init = KLineInit.FastInit)
    {
        try { ConnectOnce(init); }
        catch (Kwp2000Exception) when (RetryAfterP3Max)
        {
            // A ECU pode estar ainda na sessão anterior, ao baud de trabalho (ligação que não terminou, ou uma ECU que
            // recusa o StopCommunication): ignora o fast-init até o P3max expirar. Espera o que falta e tenta outra vez.
            long since = LastEcuActivity == 0 ? long.MaxValue : (Stopwatch.GetTimestamp() - LastEcuActivity) * 1000 / Stopwatch.Frequency;
            int wait = (int)Math.Clamp(LastP3MaxMs + 500 - Math.Min(since, int.MaxValue), 500, LastP3MaxMs + 500);
            LogMsg(T($"A ECU pode estar ainda na sessão anterior: espero {wait / 1000.0:0.0} s (P3max) e tento de novo…",
                     $"The ECU may still be in the previous session: waiting {wait / 1000.0:0.0} s (P3max) and trying again…"));
            Thread.Sleep(wait);
            ConnectOnce(init);
        }
    }

    /// <summary>true = se o init falhar, espera o P3max e tenta mais uma vez (o "Ligar"; a procura não usa).</summary>
    public bool RetryAfterP3Max { get; set; }

    /// <summary>Última resposta da ECU neste processo (Stopwatch ticks) e o P3max em vigor — para a nova tentativa.</summary>
    public static long LastEcuActivity { get; private set; }
    public static int LastP3MaxMs { get; private set; } = 5000;

    private void ConnectOnce(KLineInit init)
    {
        if (!_device.IsOpen) _device.Open();
        // Init + StartCommunication são SEMPRE a 10400 (norma KWP2000).
        _device.SetBaudRate(InitBaudRate);
        _rxCount = 0; _rxPos = 0;

        var targets = FastInitTargets is { Length: > 0 } ? FastInitTargets : new[] { EcuAddress };
        string list = string.Join("/", Array.ConvertAll(targets, t => "0x" + t.ToString("X2")));

        if (_frameDevice != null)
        {
            ConnectViaInterface(_frameDevice, init, targets, list);
            return;
        }

        if (init == KLineInit.FiveBaud)
        {
            LogMsg(T($"Slow init (5 baud) a tentar {list}.", $"Slow init (5 baud) trying {list}."));
            foreach (var target in targets)
            {
                try { FiveBaudInit(target); return; }
                catch (Kwp2000Exception ex) { LogMsg(T($"0x{target:X2} sem resposta ao 5 baud: {ex.Message}", $"0x{target:X2} no answer at 5 baud: {ex.Message}")); }
            }
            throw new Kwp2000TimeoutException(T($"Sem resposta ao slow init (5 baud) em {list}.", $"No answer to slow init (5 baud) on {list}."));
        }

        LogMsg(T($"Init a {InitBaudRate} baud; fast-init a tentar {list}.", $"Init at {InitBaudRate} baud; fast-init trying {list}."));
        foreach (var target in targets)
        {
            for (int attempt = 1; attempt <= Math.Max(1, FastInitAttemptsPerTarget); attempt++)
            {
                try
                {
                    LogMsg($"Fast-init → 0x{target:X2}" + (attempt > 1 ? T($" (tentativa {attempt})…", $" (attempt {attempt})…") : "…"));
                    FastInitPulse();
                    _device.DiscardBuffers(); // limpa o ruído da transição do wake
                    _rxCount = 0; _rxPos = 0;
                    var req = new Kwp2000Message(InitAddressMode(target), target, TesterAddress,
                        (byte)Kwp2000Service.StartCommunication);
                    var resp = SendRequest(req, FastInitResponseTimeoutMs);
                    LogMsg(T($"Resposta de 0x{resp.Source:X2}: {Convert.ToHexString(resp.Encode())}", $"Response from 0x{resp.Source:X2}: {Convert.ToHexString(resp.Encode())}"));
                    if (resp.Data.Length >= 3 && resp.Data[0] == 0xC1)
                    {
                        KeyBytes = new[] { resp.Data[1], resp.Data[2] };
                        LogMsg($"StartComm OK · key bytes {resp.Data[1]:X2} {resp.Data[2]:X2}.");
                    }
                    // A ECU pode responder a partir do seu endereço físico (resp.Source),
                    // que não tem de coincidir com o alvo do pedido. Usa-o nos pedidos seguintes.
                    EcuAddress = resp.Source != 0 ? resp.Source : target;
                    LogMsg(T($"Ligado. Endereço ECU = 0x{EcuAddress:X2}, tester = 0x{TesterAddress:X2}.", $"Connected. ECU address = 0x{EcuAddress:X2}, tester = 0x{TesterAddress:X2}."));
                    // NB: o baud de trabalho NÃO se muda aqui. A troca faz-se no
                    // StartDiagnosticSession (byte de baud no 0x10), como o NefMoto.
                    return;
                }
                catch (Kwp2000Exception ex) { LogMsg(T($"0x{target:X2} sem resposta: {ex.Message}", $"0x{target:X2} no answer: {ex.Message}")); }
            }
        }
        if (!RetryAfterP3Max) LogMsg(T("Dica: se a ligação anterior não terminou (programa fechado/caiu com a ECU ligada), a ECU pode estar ainda nessa sessão ao baud de trabalho e ignora o fast-init até o P3max expirar (até ~60 s em algumas ECUs). Espera e volta a tentar.",
                 "Hint: if the previous connection did not end (program closed/crashed with the ECU connected), the ECU may still be in that session at the working baud and ignores the fast-init until P3max expires (up to ~60 s on some ECUs). Wait and try again."));
        throw new Kwp2000TimeoutException(T($"Sem resposta ao fast-init em {list}.", $"No answer to fast-init on {list}."));
    }

    /// <summary>
    /// Init feito pelo interface J2534 (IOCTL FAST_INIT / FIVE_BAUD_INIT): mesmo resultado que o init por
    /// break — key bytes e endereço da ECU guardados — mas os tempos do pulso ficam a cargo do interface.
    /// </summary>
    private void ConnectViaInterface(IKLineFrameDevice dev, KLineInit init, byte[] targets, string list)
    {
        // P1max: o interface espera este tempo de linha calada depois de cada resposta antes de deixar sair o
        // pedido seguinte (visto no Scanmatik 2: 40 ms → ~17 Hz). É o campo P1max da UI, não o timeout do PC.
        dev.SetTiming(Math.Max(1, Timing.P1Max), EnforceP3Min ? Timing.P3Min : 0, Timing.P4Min);
        string how = init == KLineInit.FiveBaud ? T("slow init (5 baud)", "slow init (5 baud)") : "fast-init";
        LogMsg(T($"J2534 ({dev.Name}): {how} a {InitBaudRate} baud, a tentar {list}.", $"J2534 ({dev.Name}): {how} at {InitBaudRate} baud, trying {list}."));
        foreach (var target in targets)
        {
            for (int attempt = 1; attempt <= Math.Max(1, FastInitAttemptsPerTarget); attempt++)
            {
                try
                {
                    LogMsg($"{how} → 0x{target:X2}" + (attempt > 1 ? T($" (tentativa {attempt})…", $" (attempt {attempt})…") : "…"));
                    if (init == KLineInit.FiveBaud)
                    {
                        KeyBytes = dev.FiveBaudInit(target);
                        EcuAddress = target;
                        LogMsg($"Key bytes {KeyBytes[0]:X2} {KeyBytes[1]:X2}.");
                    }
                    else
                    {
                        var req = new Kwp2000Message(InitAddressMode(target), target, TesterAddress,
                            (byte)Kwp2000Service.StartCommunication);
                        var raw = dev.FastInit(req.Encode());
                        var resp = Kwp2000Message.TryParse(raw)
                            ?? throw new Kwp2000Exception(T($"Resposta inválida ao fast-init: {Convert.ToHexString(raw)}", $"Invalid fast-init response: {Convert.ToHexString(raw)}"));
                        LogMsg(T($"Resposta de 0x{resp.Source:X2}: {Convert.ToHexString(raw)}", $"Response from 0x{resp.Source:X2}: {Convert.ToHexString(raw)}"));
                        if (resp.IsNegativeResponse)
                            throw new Kwp2000Exception(T($"StartCommunication recusado ({resp.NegativeResponseCode}).", $"StartCommunication refused ({resp.NegativeResponseCode})."));
                        if (resp.Data.Length >= 3 && resp.Data[0] == 0xC1)
                        {
                            KeyBytes = new[] { resp.Data[1], resp.Data[2] };
                            LogMsg($"StartComm OK · key bytes {resp.Data[1]:X2} {resp.Data[2]:X2}.");
                        }
                        EcuAddress = resp.Source != 0 ? resp.Source : target;
                    }
                    _rxCount = 0; _rxPos = 0; _lastResponseEndTicks = 0;
                    LogMsg(T($"Ligado. Endereço ECU = 0x{EcuAddress:X2}, tester = 0x{TesterAddress:X2}.", $"Connected. ECU address = 0x{EcuAddress:X2}, tester = 0x{TesterAddress:X2}."));
                    return;
                }
                catch (Exception ex) when (ex is Kwp2000Exception or IOException)
                {
                    LogMsg(T($"0x{target:X2} sem resposta: {ex.Message}", $"0x{target:X2} no answer: {ex.Message}"));
                }
            }
        }
        throw new Kwp2000TimeoutException(T($"Sem resposta ao {how} em {list}.", $"No answer to {how} on {list}."));
    }

    /// <summary>
    /// Muda os timings com a ligação feita (P1–P4; o baud e o timeout entre bytes ficam). No J2534 passa-os logo
    /// ao interface. Chamar sem pedidos em curso.
    /// </summary>
    public void ApplyTiming(KwpTiming t)
    {
        Timing.P1Max = t.P1Max;
        Timing.P2Min = t.P2Min; Timing.P2Max = t.P2Max;
        Timing.P3Min = t.P3Min; Timing.P3Max = t.P3Max;
        Timing.P4Min = t.P4Min;
        _frameDevice?.SetTiming(Math.Max(1, Timing.P1Max), EnforceP3Min ? Timing.P3Min : 0, Timing.P4Min);
        LogMsg($"Timings: P1max {Timing.P1Max} · P2 {Timing.P2Min}-{Timing.P2Max} · P3 {Timing.P3Min}-{Timing.P3Max} · P4min {Timing.P4Min} ms.");
    }

    /// <summary>
    /// 0x33 é o endereço funcional OBD-II (ISO 14230-4): o StartCommunication vai em modo funcional e a ECU
    /// responde do seu endereço físico, que passa a ser o usado. Os outros vão em modo físico.
    /// </summary>
    private static Kwp2000AddressMode InitAddressMode(byte target)
        => target == KLinePresets.ObdFunctional ? Kwp2000AddressMode.Functional : Kwp2000AddressMode.Physical;

    /// <summary>
    /// Tentativas de fast-init por endereço. A 1.ª tentativa logo após abrir o cabo FTDI falha de forma
    /// consistente (visto na Thesis: o logger só ligava à 2.ª tentativa); 2 cobre isso.
    /// </summary>
    public int FastInitAttemptsPerTarget { get; set; } = 2;

    /// <summary>
    /// Tempo de repouso (linha alta) antes do wake-up / do endereço a 5 baud — ISO 14230-2 W5 ≥ 300 ms.
    /// </summary>
    public int FastInitIdleMs { get; set; } = 300;

    /// <summary>Duração de cada bit no slow init (200 ms = 5 baud). Ajustável só para testes.</summary>
    public double FiveBaudBitMs { get; set; } = 200;

    /// <summary>Pulso de fast-init (ISO 14230): idle alto ≥ W5, 25 ms baixo, 25 ms alto. Busy-wait preciso.</summary>
    private void FastInitPulse()
    {
        _device.SetBreak(false);
        IdleWait(FastInitIdleMs);
        _device.SetBreak(true); BusyWaitMs(25);    // TiniL (baixo)
        _device.SetBreak(false); BusyWaitMs(25);   // alto antes do StartCommunication
    }

    /// <summary>
    /// Slow init (ISO 14230-2 / ISO 9141): endereço a 5 baud (start, 8 bits LSB primeiro, stop) por
    /// break; depois a 10400: ECU envia 0x55 (W1 60–300 ms), KB1, KB2 (W2/W3 ≤ 20 ms); o tester
    /// responde ~KB2 após W4 (25–50 ms) e a ECU confirma com ~endereço.
    /// </summary>
    private void FiveBaudInit(byte address)
    {
        LogMsg(T($"5 baud → endereço 0x{address:X2}…", $"5 baud → address 0x{address:X2}…"));
        _device.SetBreak(false);
        IdleWait(FastInitIdleMs);

        // 10 bits (start=0, dados LSB primeiro, stop=1) com horário absoluto (sem acumular erro).
        var sw = Stopwatch.StartNew();
        for (int bit = 0; bit < 10; bit++)
        {
            bool level = bit switch
            {
                0 => false,                          // start
                9 => true,                           // stop
                _ => ((address >> (bit - 1)) & 1) != 0,
            };
            _device.SetBreak(!level);               // break = linha baixa
            if (bit == 9) break;                     // o stop bit fica alto; não é preciso esperar por ele
            WaitUntil(sw, (bit + 1) * FiveBaudBitMs);
        }

        // Durante o stop bit: limpar o lixo do eco do bit-banging e esperar o 0x55.
        _device.SetBaudRate(InitBaudRate);
        _device.DiscardBuffers();
        _rxCount = 0; _rxPos = 0;

        var one = new byte[1];
        long deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * (FiveBaudBitMs + 400) / 1000);
        while (true)
        {
            if (Stopwatch.GetTimestamp() > deadline)
                throw new Kwp2000TimeoutException(T("sem byte de sincronismo 0x55.", "no 0x55 sync byte."));
            try { ReadInto(one, 50); } catch (Kwp2000TimeoutException) { continue; }
            if (one[0] == 0x55) break;               // ignora lixo antes do sync
        }

        var kb = new byte[2];
        ReadInto(kb, 100);
        LogMsg($"Sync 0x55 · key bytes {kb[0]:X2} {kb[1]:X2}.");

        BusyWaitMs(30);                              // W4 (25–50 ms)
        byte inv = (byte)~kb[1];
        _device.Write(new[] { inv });
        if (ExpectEcho) ReadInto(one, 100);          // eco do ~KB2

        ReadInto(one, 200);                          // ~endereço (W4)
        if (one[0] != (byte)~address)
            throw new Kwp2000Exception(T($"ECU respondeu 0x{one[0]:X2} em vez de ~endereço 0x{(byte)~address:X2}.", $"ECU answered 0x{one[0]:X2} instead of ~address 0x{(byte)~address:X2}."));

        KeyBytes = kb;
        EcuAddress = address;
        _lastResponseEndTicks = 0;
        LogMsg(T($"Ligado por 5 baud. Endereço ECU = 0x{EcuAddress:X2}, tester = 0x{TesterAddress:X2}.", $"Connected by 5 baud. ECU address = 0x{EcuAddress:X2}, tester = 0x{TesterAddress:X2}."));
    }

    /// <summary>Repouso sem ocupar o CPU, a acabar com precisão (busy-wait nos últimos 20 ms).</summary>
    private static void IdleWait(int ms)
    {
        if (ms > 20) Thread.Sleep(ms - 20);
        BusyWaitMs(Math.Min(ms, 20));
    }

    private static void WaitUntil(Stopwatch sw, double ms)
    {
        double remaining = ms - sw.Elapsed.TotalMilliseconds;
        if (remaining > 20) Thread.Sleep((int)(remaining - 15));
        while (sw.Elapsed.TotalMilliseconds < ms) { }
    }

    /// <summary>Espera precisa (busy-wait) — o Thread.Sleep do Windows tem jitter que estraga o fast-init.</summary>
    private static void BusyWaitMs(double ms)
    {
        long ticks = (long)(Stopwatch.Frequency * ms / 1000.0);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedTicks < ticks) { }
    }

    // ------------------------------------------------------- Request/Response

    /// <summary>
    /// Pedido cru (serviço + parâmetros em <paramref name="request"/>): devolve os dados da resposta — positiva ou
    /// <c>7F sid nrc</c> — ou null se não houver resposta. Não lança nem escreve no log (para o "Explorar").
    /// </summary>
    public byte[]? TryRaw(byte[] request, int timeoutMs)
    {
        var log = OnLog;
        OnLog = null;
        try
        {
            var msg = new Kwp2000Message(ShortHeaders ? Kwp2000AddressMode.None : Kwp2000AddressMode.Physical, EcuAddress, TesterAddress, request);
            return SendRequest(msg, timeoutMs).Data;
        }
        catch (Kwp2000NegativeResponseException ex) { return new byte[] { 0x7F, ex.RequestServiceId, (byte)ex.ResponseCode }; }
        catch (Kwp2000Exception) { return null; }
        finally { OnLog = log; }
    }

    /// <summary>Envia um serviço com parâmetros; devolve a resposta positiva (lança em negativa).</summary>
    public Kwp2000Message SendService(Kwp2000Service service, params byte[] parameters)
        => SendServiceWithTimeout(null, service, parameters);

    /// <summary>Como <see cref="SendService"/>, com timeout de resposta próprio (também após 0x78).</summary>
    public Kwp2000Message SendServiceWithTimeout(int? responseTimeoutMs, Kwp2000Service service, params byte[] parameters)
    {
        var msg = Kwp2000Message.Request(EcuAddress, TesterAddress, service, parameters);
        if (ShortHeaders) msg.AddressMode = Kwp2000AddressMode.None;
        return SendRequest(msg, responseTimeoutMs);
    }

    /// <summary>
    /// Header de 1 byte (sem target/source): "fmt dados cs". Poupa 2 bytes no pedido e (se a ECU
    /// responder no mesmo formato) 2 na resposta. A ME7 anuncia suporte nos key bytes (KB1 bit HB0).
    /// Só usar depois de ligado — o StartCommunication vai sempre com endereços.
    /// </summary>
    public bool ShortHeaders { get; set; }

    /// <summary>true se os key bytes recebidos dizem que a ECU aceita header sem endereços.</summary>
    public bool EcuSupportsShortHeaders => KeyBytes.Length >= 1 && (KeyBytes[0] & 0x04) != 0;

    /// <summary>
    /// Nº de pedidos seguintes a registar em detalhe via <see cref="OnLog"/>: bytes enviados, e
    /// instantes (ms desde o fim da escrita) e tamanho de cada bloco recebido do driver.
    /// Mostra se a ECU envia os bytes seguidos ou espaçados.
    /// </summary>
    public int TraceNextRequests { get; set; }

    /// <summary>Trace do último pedido (sempre recolhido; formatado só quando pedido).</summary>
    public string LastRequestTrace => FormatTrace();

    private readonly long[] _traceTicks = new long[128];
    private readonly int[] _traceCounts = new int[128];
    private int _traceN;
    private long _traceT1;
    private byte[] _traceTx = Array.Empty<byte>();
    private byte[]? _traceRx;

    private string FormatTrace()
    {
        double f = 1000.0 / Stopwatch.Frequency;
        var sb = new System.Text.StringBuilder();
        sb.Append("TX ").Append(Convert.ToHexString(_traceTx)).Append(" | RX (ms:bytes) ");
        for (int i = 0; i < _traceN; i++)
            sb.Append($"{(_traceTicks[i] - _traceT1) * f:0.0}:{_traceCounts[i]} ");
        if (_traceRx != null)
            sb.Append("| ").Append(Convert.ToHexString(_traceRx.AsSpan(0, Math.Min(_traceRx.Length, 16))))
              .Append(_traceRx.Length > 16 ? $"…({_traceRx.Length})" : "");
        return sb.ToString();
    }

    /// <summary>
    /// true = FT_Purge antes de CADA pedido (comportamento antigo). O purge é um control-transfer
    /// USB que custa ~1–3 ms por pedido; com framing exato não é preciso. Por omissão só se purga
    /// em erro (timeout/frame inválido) para ressincronizar.
    /// </summary>
    public bool PurgeBeforeEachRequest { get; set; }

    /// <summary>
    /// true = esperar <see cref="KwpTiming.P3Min"/> entre o fim de uma resposta e o pedido seguinte.
    /// Por omissão false (comportamento antigo: pedido imediato). O logger liga-o com o valor da UI,
    /// para testar se a ECU precisa de uma pausa antes de aceitar o pedido seguinte.
    /// </summary>
    public bool EnforceP3Min { get; set; }

    /// <summary>Estatísticas de tempo por fase dos pedidos (diagnóstico de cadência).</summary>
    public KwpTransactionStats Stats { get; } = new();

    private long _lastResponseEndTicks;

    /// <summary>
    /// Envia um pedido e devolve a resposta. Trata eco e responsePending (0x78).
    /// Lança <see cref="Kwp2000NegativeResponseException"/> em resposta negativa (exceto 0x78).
    /// </summary>
    public Kwp2000Message SendRequest(Kwp2000Message request, int? responseTimeoutMs = null)
    {
        byte[] frame = request.Encode();
        if (PurgeBeforeEachRequest) { _device.DiscardBuffers(); _rxCount = 0; _rxPos = 0; }

        try
        {
            // P3min: tempo mínimo entre o fim da resposta e o próximo pedido (só se pedido).
            if (EnforceP3Min && Timing.P3Min > 0 && _lastResponseEndTicks != 0)
            {
                long until = _lastResponseEndTicks + Stopwatch.Frequency * Timing.P3Min / 1000;
                while (Stopwatch.GetTimestamp() < until) { }
            }

            long t0 = Stopwatch.GetTimestamp();
            WriteWithInterByte(frame);
            long t1 = Stopwatch.GetTimestamp();
            _traceN = 0; _traceT1 = t1; _traceTx = frame; _traceRx = null;

            if (ExpectEcho)
                ReadExact(frame.Length, Timing.ResponseTimeoutMs); // descarta eco
            long t2 = Stopwatch.GetTimestamp();

            int timeout = responseTimeoutMs ?? Timing.ResponseTimeoutMs;
            while (true)
            {
                var resp = ReadFrame(timeout, out long firstByteTicks);
                long t3 = Stopwatch.GetTimestamp();
                if (resp.IsNegativeResponse &&
                    resp.NegativeResponseCode == Kwp2000ResponseCode.ResponsePending)
                {
                    // ECU pediu para esperar: resetar o relógio e voltar a ler.
                    continue;
                }
                LastEcuActivity = t3;
                Stats.Add(_lastResponseEndTicks == 0 ? -1 : t0 - _lastResponseEndTicks,
                    t1 - t0, t2 - t1, firstByteTicks - t2, t3 - firstByteTicks);
                _lastResponseEndTicks = t3;
                if (TraceNextRequests > 0)
                {
                    TraceNextRequests--;
                    _traceRx = _lastRxFrame;
                    LogMsg("Trace: " + FormatTrace());
                }
                if (resp.IsNegativeResponse)
                {
                    // "ainda em curso"/"ocupada": respostas normais enquanto se espera por uma rotina — quem
                    // pede repete e resume no log; não vale a pena um trace por cada uma.
                    if (resp.NegativeResponseCode is not (Kwp2000ResponseCode.RoutineNotCompleteOrServiceInProgress
                                                          or Kwp2000ResponseCode.BusyRepeatRequest))
                    LogMsg($"{T("Resposta negativa", "Negative response")}. Trace: {FormatTrace()}{(_traceRx == null ? " | " + Convert.ToHexString(_lastRxFrame ?? Array.Empty<byte>()) : "")}");
                    throw new Kwp2000NegativeResponseException(
                        resp.NegativeRequestServiceId ?? 0,
                        resp.NegativeResponseCode ?? Kwp2000ResponseCode.GeneralReject);
                }
                return resp;
            }
        }
        catch (Kwp2000Exception ex) when (ex is not Kwp2000NegativeResponseException)
        {
            // Timeout / frame inválido: pode haver lixo ou uma resposta atrasada no buffer.
            // Espera a linha calar e purga, para o próximo pedido começar limpo.
            _lastResponseEndTicks = 0;
            _traceRx = null;
            LogMsg(T($"Erro no pedido ({ex.Message}). Trace: {FormatTrace()}", $"Request error ({ex.Message}). Trace: {FormatTrace()}"));
            Resync();
            throw;
        }
    }

    /// <summary>Espera a linha ficar em silêncio (≥ interbyte timeout) e limpa os buffers.</summary>
    private void Resync()
    {
        try
        {
            var scratch = new byte[256];
            var total = Stopwatch.StartNew();
            var quiet = Stopwatch.StartNew();
            int quietMs = Math.Max(Timing.InterByteTimeoutMs, 5);
            while (quiet.ElapsedMilliseconds < quietMs && total.ElapsedMilliseconds < 500)
            {
                if (_device.Read(scratch, 1) > 0) quiet.Restart();
            }
        }
        catch { /* best effort */ }
        _rxCount = 0; _rxPos = 0;
        _device.DiscardBuffers();
    }

    private void WriteWithInterByte(byte[] frame)
    {
        // J2534: o interface respeita o P4 sozinho (SET_CONFIG P4_MIN) — envia-se a frame inteira.
        if (Timing.P4Min <= 0 || _frameDevice != null)
        {
            _device.Write(frame);
            return;
        }
        // Respeita P4 entre bytes (alguns ECUs são sensíveis). Busy-wait: o Thread.Sleep do
        // Windows arredonda para 1–15 ms e chegava a custar dezenas de ms por pedido.
        var one = new byte[1];
        for (int i = 0; i < frame.Length; i++)
        {
            one[0] = frame[i];
            _device.Write(one);
            if (i < frame.Length - 1) BusyWaitMs(Timing.P4Min);
        }
    }

    // ------------------------------------------------------- Frame reader

    // Buffer de receção: cada chamada ao driver traz o que já chegou e o parser consome daqui
    // (menos chamadas ao driver por frame).
    private readonly byte[] _rxBuf = new byte[1024];
    private byte[]? _lastRxFrame;
    private int _rxPos, _rxCount;

    private Kwp2000Message ReadFrame(int firstByteTimeoutMs, out long firstByteTicks)
    {
        // 1) Fmt
        Span<byte> hdr = stackalloc byte[4];
        ReadInto(hdr[..1], firstByteTimeoutMs);
        firstByteTicks = Stopwatch.GetTimestamp();
        byte fmt = hdr[0];
        var mode = (Kwp2000AddressMode)(fmt & 0xC0);
        int lenInFmt = fmt & 0x3F;
        int hlen = 1;

        // 2) Tgt/Src
        if (mode != Kwp2000AddressMode.None)
        {
            ReadInto(hdr.Slice(hlen, 2), Timing.InterByteTimeoutMs);
            hlen += 2;
        }

        // 3) Len separado
        int len = lenInFmt;
        if (len == 0)
        {
            ReadInto(hdr.Slice(hlen, 1), Timing.InterByteTimeoutMs);
            len = hdr[hlen];
            hlen++;
        }

        // 4) Data + checksum
        var full = new byte[hlen + len + 1];
        hdr[..hlen].CopyTo(full);
        ReadInto(full.AsSpan(hlen), Timing.InterByteTimeoutMs);
        _lastRxFrame = full;

        var msg = Kwp2000Message.TryParse(full);
        if (msg == null)
            throw new Kwp2000Exception(T($"Frame inválido ou checksum errado: {Convert.ToHexString(full)}", $"Invalid frame or wrong checksum: {Convert.ToHexString(full)}"));
        return msg;
    }

    /// <summary>Lê exatamente <paramref name="count"/> bytes ou lança timeout.</summary>
    private byte[] ReadExact(int count, int timeoutMs)
    {
        var buf = new byte[count];
        ReadInto(buf, timeoutMs);
        return buf;
    }

    /// <summary>
    /// Enche <paramref name="dest"/> a partir do buffer de receção, pedindo mais ao driver só quando
    /// falta. <paramref name="timeoutMs"/> é o tempo máximo SEM receber bytes (reinicia a cada byte).
    /// O timeout passado ao driver é constante por chamada (não reconfigura o FTDI a cada leitura).
    /// </summary>
    private void ReadInto(Span<byte> dest, int timeoutMs)
    {
        int got = 0;
        long idleStart = Stopwatch.GetTimestamp();
        long idleLimit = Stopwatch.Frequency * timeoutMs / 1000;
        while (true)
        {
            if (_rxCount > 0)
            {
                int n = Math.Min(_rxCount, dest.Length - got);
                _rxBuf.AsSpan(_rxPos, n).CopyTo(dest[got..]);
                _rxPos += n; _rxCount -= n; got += n;
                if (_rxCount == 0) _rxPos = 0;
            }
            if (got == dest.Length) return;

            if (Stopwatch.GetTimestamp() - idleStart > idleLimit)
                throw new Kwp2000TimeoutException(T($"Timeout a ler ({got}/{dest.Length} bytes).", $"Read timeout ({got}/{dest.Length} bytes)."));

            // Pede o que falta; o driver devolve logo o que já tiver chegado (até esse tamanho).
            int want = Math.Min(dest.Length - got, _rxBuf.Length);
            int r = _device.Read(_rxBuf.AsSpan(0, want), timeoutMs);
            if (r > 0)
            {
                _rxPos = 0; _rxCount = r; idleStart = Stopwatch.GetTimestamp();
                if (_traceN < _traceTicks.Length) { _traceTicks[_traceN] = idleStart; _traceCounts[_traceN++] = r; }
            }
        }
    }

    // ------------------------------------------------------- Serviços de alto nível

    /// <summary>
    /// StartDiagnosticSession (0x10). Opcionalmente negoceia um baud de trabalho mais rápido:
    /// envia "10 &lt;session&gt; &lt;baudByte&gt;" a 10400; a ECU responde a 10400 e só DEPOIS
    /// ambos os lados mudam para o novo baud (mecanismo KWP2000, igual ao NefMoto).
    /// Se a ECU recusar o baud, tenta de novo sem byte de baud (fica a 10400).
    /// </summary>
    /// <param name="workingBaud">0 ou 10400 = sem troca de baud.</param>
    public void StartDiagnosticSession(Kwp2000SessionType session, int workingBaud = 0)
    {
        bool wantBaud = workingBaud > 0
            && workingBaud != InitBaudRate
            && session != Kwp2000SessionType.Standard;

        if (wantBaud && _frameDevice != null)
        {
            // Se o interface J2534 não aceitar o baud, a ECU mudava e nós não: perdia-se a ligação.
            // Experimenta-o (e volta ao de init) antes de o pedir.
            int probe = (int)KwpBaud.FromByte(KwpBaud.ToByte((uint)workingBaud));
            try { _device.SetBaudRate(probe); _device.SetBaudRate(InitBaudRate); }
            catch (Exception ex)
            {
                try { _device.SetBaudRate(InitBaudRate); } catch { }
                LogMsg(T($"O interface J2534 não aceita {probe} baud ({ex.Message}); fica a {InitBaudRate}.", $"The J2534 interface does not accept {probe} baud ({ex.Message}); staying at {InitBaudRate}."));
                wantBaud = false;
            }
        }

        if (wantBaud)
        {
            byte baudByte = KwpBaud.ToByte((uint)workingBaud);
            try
            {
                var resp = SendService(Kwp2000Service.StartDiagnosticSession, (byte)session, baudByte);
                // resposta: 50 <session> <baudByte> [waitTime*100ms]
                uint newBaud = resp.Data.Length >= 3 ? KwpBaud.FromByte(resp.Data[2]) : (uint)workingBaud;
                CurrentSession = session;
                _device.SetBaudRate((int)newBaud); // troca SÓ depois do ack
                LogMsg(T($"Sessão 0x{(byte)session:X2} OK · baud de trabalho {newBaud}.", $"Session 0x{(byte)session:X2} OK · working baud {newBaud}."));
                return;
            }
            catch (Kwp2000NegativeResponseException ex) when (
                ex.ResponseCode is Kwp2000ResponseCode.SubFunctionNotSupportedOrInvalidFormat
                    or Kwp2000ResponseCode.RequestOutOfRange
                    or Kwp2000ResponseCode.ConditionsNotCorrectOrRequestSequenceError)
            {
                LogMsg(T($"ECU recusou baud {workingBaud} ({ex.ResponseCode}); continua a {InitBaudRate}.", $"ECU refused baud {workingBaud} ({ex.ResponseCode}); staying at {InitBaudRate}."));
                // continua para a tentativa sem baud
            }
        }

        try { SendService(Kwp2000Service.StartDiagnosticSession, (byte)session); }
        catch (Kwp2000NegativeResponseException ex) when (!wantBaud && session != Kwp2000SessionType.Standard
            && ex.ResponseCode == Kwp2000ResponseCode.SubFunctionNotSupportedOrInvalidFormat)
        {
            // O programador da IROM (sub_4396) exige o byte de baud no 10 85 (Len ≥ 3): repete com o
            // byte do baud atual (10400 → 0x14), que mantém a velocidade.
            LogMsg(T($"A ECU exige o byte de baud no 10 {(byte)session:X2}; a repetir com {InitBaudRate}.", $"The ECU requires the baud byte in 10 {(byte)session:X2}; retrying with {InitBaudRate}."));
            SendService(Kwp2000Service.StartDiagnosticSession, (byte)session, KwpBaud.ToByte((uint)InitBaudRate));
        }
        CurrentSession = session;
        LogMsg(T($"Sessão 0x{(byte)session:X2} OK · {InitBaudRate} baud.", $"Session 0x{(byte)session:X2} OK · {InitBaudRate} baud."));
    }

    public void StopDiagnosticSession()
    {
        SendService(Kwp2000Service.StopDiagnosticSession);
        CurrentSession = Kwp2000SessionType.Standard;
    }

    /// <summary>
    /// StopCommunication (0x82): termina a ligação de forma ordeira. Volta a 10400 para a
    /// próxima ligação começar no baud de init. Tolera falhas (a ECU pode já estar calada).
    /// </summary>
    public void StopCommunication()
    {
        string Try(Kwp2000Service svc)
        {
            try { SendRequest(Kwp2000Message.Request(EcuAddress, TesterAddress, svc)); return "ok"; }
            catch (Kwp2000NegativeResponseException ex) { return $"NRC 0x{(byte)ex.ResponseCode:X2} ({ex.ResponseCode})"; }
            catch (Kwp2000Exception) { return T("sem resposta", "no answer"); }
        }
        try
        {
            string r = Try(Kwp2000Service.StopCommunication);
            if (r != "ok" && r.StartsWith("NRC"))
            {
                // há ECUs que só terminam a comunicação depois de sair da sessão de diagnóstico
                string r2 = Try(Kwp2000Service.StopDiagnosticSession);
                string r3 = Try(Kwp2000Service.StopCommunication);
                LogMsg($"StopCommunication: {r} · StopDiagnosticSession: {r2} · StopCommunication: {r3}.");
            }
            else LogMsg($"StopCommunication: {r}.");
        }
        finally
        {
            try { _device.SetBaudRate(InitBaudRate); } catch { }
            CurrentSession = Kwp2000SessionType.Standard;
        }
    }

    public void TesterPresent(bool responseRequired = true)
    {
        if (!responseRequired) { SendService(Kwp2000Service.TesterPresent, 0x02); return; }
        // Fiat 07223 §6.4: o pedido é só "3E" (sem parâmetro). A ME7 aceita também "3E 01" (ISO 14230), mas a
        // EDC16C8 recusa-o com 0x12. Começa pelo da norma Fiat; se a ECU o recusar, passa a "3E 01" e fica.
        if (TesterPresentWithParameter != true)
        {
            try { SendService(Kwp2000Service.TesterPresent); TesterPresentWithParameter = false; return; }
            catch (Kwp2000NegativeResponseException ex) when (TesterPresentWithParameter == null && ex.ResponseCode is
                Kwp2000ResponseCode.SubFunctionNotSupportedOrInvalidFormat or Kwp2000ResponseCode.GeneralReject)
            { TesterPresentWithParameter = true; }
        }
        SendService(Kwp2000Service.TesterPresent, 0x01);
    }

    /// <summary>Formato do TesterPresent que a ECU aceita: false = "3E", true = "3E 01", null = ainda por saber.</summary>
    public bool? TesterPresentWithParameter { get; private set; }

    /// <summary>
    /// AccessTimingParameters (0x83): lê os limites de timing da ECU e aplica os mais rápidos.
    /// Por omissão a ECU impõe um P2min (atraso de resposta) de dezenas de ms — este é o maior
    /// limitador da cadência do logging, independente do baud. Chamar DEPOIS do StartDiagnosticSession
    /// (que repõe os timings por omissão). Formato: 83 &lt;TPI&gt; P2min P2max P3min P3max P4min.
    /// Unidades: P2/P3min e P4 = ×0.5 ms, P2max = ×25 ms, P3max = ×250 ms.
    /// </summary>
    public void NegotiateFastTiming()
    {
        // TPI 0x00 = ReadLimits. Resposta: C3 00 p2min p2max p3min p3max p4min
        var limits = SendService(Kwp2000Service.AccessTimingParameters, 0x00);
        if (limits.Data.Length < 7)
        {
            LogMsg(T("AccessTimingParameters: resposta curta, timings por omissão mantidos.", "AccessTimingParameters: short response, default timings kept."));
            return;
        }
        byte p2min = limits.Data[2], p2max = limits.Data[3],
             p3min = limits.Data[4], p3max = limits.Data[5], p4min = limits.Data[6];

        // TPI 0x03 = SetValues: aplica os limites (mins = mais rápido; P2max fica no máximo
        // para não cortar respostas grandes, como faz o NefMoto).
        // P3max NÃO vai ao máximo: é quanto tempo a ECU mantém a sessão sem mensagens. A EDC16C8 anuncia
        // 63,75 s — se o PC fechasse sem StopCommunication, a ECU ficava um minuto na sessão (ao baud rápido)
        // a ignorar o fast-init seguinte. 5 s (0x14 × 250 ms) chega: o keep-alive manda de 2 em 2 s.
        p3max = Math.Min(p3max, (byte)0x14);
        LastP3MaxMs = Math.Max(500, p3max * 250);
        SendService(Kwp2000Service.AccessTimingParameters, 0x03, p2min, p2max, p3min, p3max, p4min);
        LogMsg($"{T("Timing nos limites", "Timing at the limits")}: P2 {p2min * 0.5:0}-{p2max * 25}ms · P3 {p3min * 0.5:0}-{p3max * 250}ms · P4min {p4min * 0.5:0}ms.");
    }

    /// <summary>ReadECUIdentification (0x1A). Devolve os dados após o eco do serviço/opção.</summary>
    public byte[] ReadEcuIdentification(EcuIdentificationOption option)
    {
        var resp = SendService(Kwp2000Service.ReadECUIdentification, (byte)option);
        // resposta: 5A <option> <dados...>
        return resp.Data.Length >= 2 ? resp.Data[2..] : Array.Empty<byte>();
    }

    /// <summary>
    /// ReadDiagnosticTroubleCodesByStatus (0x18): lê os DTCs. Pedido Fiat 07223 §8.2:
    /// <c>18 00 FF 00</c> (requestIdentifiedDTCAndStatus, todos os grupos). Resposta
    /// <c>58 numberOfDTC (hi lo status)*</c>; sem avarias a ECU responde <c>58 00</c>.
    /// </summary>
    public List<DtcRecord> ReadDtcByStatus(byte statusMask = 0x00, ushort group = 0xFF00)
    {
        var resp = SendService(Kwp2000Service.ReadDiagnosticTroubleCodesByStatus,
            statusMask, (byte)(group >> 8), (byte)group);

        var list = new List<DtcRecord>();
        var d = resp.Data;
        if (d.Length < 2) return list;
        int count = d[1];
        int p = 2;
        for (int i = 0; i < count && p + 3 <= d.Length; i++, p += 3)
            list.Add(new DtcRecord((ushort)(d[p] << 8 | d[p + 1]), d[p + 2]));
        if (list.Count < count)
            LogMsg(T($"AVISO DTC: a ECU anunciou {count} DTC(s) mas a resposta só trazia {list.Count} ({d.Length - 2} bytes de lista). Resposta: {Convert.ToHexString(d)}",
                     $"DTC WARNING: the ECU announced {count} DTC(s) but the response only had {list.Count} ({d.Length - 2} list bytes). Response: {Convert.ToHexString(d)}"));
        return list;
    }

    /// <summary>ClearDiagnosticInformation (0x14): apaga DTCs. group 0xFF00/0xFFFF = todos.</summary>
    public void ClearDtc(ushort group = 0xFF00)
        => SendService(Kwp2000Service.ClearDiagnosticInformation, (byte)(group >> 8), (byte)group);

    /// <summary>ReadDataByLocalIdentifier (0x21): devolve os dados após "61 &lt;lid&gt;".</summary>
    public byte[] ReadDataByLocalIdentifier(byte localId)
    {
        var resp = SendService(Kwp2000Service.ReadDataByLocalIdentifier, localId);
        return resp.Data.Length >= 2 ? resp.Data[2..] : Array.Empty<byte>();
    }

    /// <summary>ReadMemoryByAddress (0x23): addr de 24 bits + tamanho.</summary>
    public byte[] ReadMemoryByAddress(uint address, int size)
    {
        var resp = SendService(Kwp2000Service.ReadMemoryByAddress,
            (byte)(address >> 16), (byte)(address >> 8), (byte)address, (byte)size);
        return resp.Data.Length >= 1 ? resp.Data[1..] : Array.Empty<byte>(); // após 0x63
    }

    /// <summary>
    /// WriteMemoryByAddress (0x3D): "3D a2 a1 a0 len dados" → "7D a2 a1 a0". Na ME7 só escreve RAM
    /// (XRAM 0x380000–0x387FFF, 0xE000–0xE7FF, IRAM 0xF600–0xFDFF) com a aplicação a correr.
    /// </summary>
    public void WriteMemoryByAddress(uint address, ReadOnlySpan<byte> data)
    {
        var p = new byte[4 + data.Length];
        p[0] = (byte)(address >> 16); p[1] = (byte)(address >> 8); p[2] = (byte)address; p[3] = (byte)data.Length;
        data.CopyTo(p.AsSpan(4));
        SendService(Kwp2000Service.WriteMemoryByAddress, p);
    }

    /// <summary>ILogChannel: envia um serviço e devolve os dados após o byte de resposta.</summary>
    public byte[] SendRaw(byte service, params byte[] parameters)
    {
        var resp = SendService((Kwp2000Service)service, parameters);
        return resp.Data.Length >= 1 ? resp.Data[1..] : Array.Empty<byte>();
    }

    /// <summary>
    /// SecurityAccess completo (Fiat ME7): request seed (0x27 seedSub) → calcula key → send key.
    /// keySub = seedSub + 1. O polinómio é escolhido pelo nível derivado de seedSub.
    /// </summary>
    public void SecurityAccessUnlock(byte seedSubFunction = 0x01, byte seedParameter = 0,
        uint perEcuPoly = SecurityAccess.PerEcuPolyThesis)
    {
        // Request seed. O byte do parâmetro (PP) vai SEMPRE: a aplicação da ME7.3 exige o pedido de
        // seed com 3 bytes (27 <sub> PP). Sem ele responde 7F 27 12 (confirmado na IROM sub_82DF62:
        // o ramo do pedido de seed faz `cmpb Len,#3; jmpa cc_C, <NRC 0x12>`). PP controla também o
        // número de iterações da key (N = PP + 0x23), que o ComputeFiatKey já usa.
        var seedResp = SendService(Kwp2000Service.SecurityAccess, seedSubFunction, seedParameter);

        // Spec Fiat 07223 §6.3: se a ECU já estiver desbloqueada, responde com seed a zeros.
        if (seedResp.Data.Length >= 3 && seedResp.Data.Skip(2).All(b => b == 0))
        {
            LogMsg(T("SecurityAccess: seed a zeros — ECU já desbloqueada.", "SecurityAccess: zero seed — ECU already unlocked."));
            return;
        }

        // resposta: 67 <sub> s3 s2 s1 s0
        if (seedResp.Data.Length < 6)
            throw new Kwp2000Exception(T("Seed demasiado curto na resposta 0x27.", "Seed too short in the 0x27 response."));

        uint seed = (uint)(seedResp.Data[2] << 24 | seedResp.Data[3] << 16
                         | seedResp.Data[4] << 8 | seedResp.Data[5]);

        uint key = SecurityAccess.ComputeFiatKey(seed, seedSubFunction, seedParameter, perEcuPoly);

        byte keySub = (byte)(seedSubFunction + 1);
        SendService(Kwp2000Service.SecurityAccess, keySub,
            (byte)(key >> 24), (byte)(key >> 16), (byte)(key >> 8), (byte)key);
    }

    /// <summary>
    /// Escreve a data de programação (RLI 0x99, BCD Ano-Ano-Mês-Dia) via 0x3B. Spec Fiat §6.6.2.1.
    /// </summary>
    public void WriteProgrammingDate(DateTime date)
    {
        byte Bcd(int v) => (byte)(((v / 10) << 4) | (v % 10));
        SendService(Kwp2000Service.WriteDataByLocalIdentifier,
            (byte)EcuIdentificationOption.ProgrammingDate,
            Bcd(date.Year / 100), Bcd(date.Year % 100), Bcd(date.Month), Bcd(date.Day));
    }

    /// <summary>Escreve o tester code (RLI 0x98, 10 ASCII, alinhado à esquerda c/ 0x20) via 0x3B.</summary>
    public void WriteTesterCode(string code)
    {
        var buf = new byte[10];
        Array.Fill(buf, (byte)0x20);
        for (int i = 0; i < Math.Min(code.Length, 10); i++) buf[i] = (byte)code[i];
        WriteTesterCodeRaw(buf);
    }

    /// <summary>
    /// Escreve o tester code com os bytes exatos (ex.: o lido da ECU, para o manter). A ME7.3 exige
    /// 3B 98 + 10 bytes (12 no total); menos é completado com 0x20, mais é cortado.
    /// </summary>
    public void WriteTesterCodeRaw(ReadOnlySpan<byte> code)
    {
        var p = new byte[11];
        p[0] = (byte)EcuIdentificationOption.TesterCode;
        Array.Fill(p, (byte)0x20, 1, 10);
        code[..Math.Min(code.Length, 10)].CopyTo(p.AsSpan(1));
        SendService(Kwp2000Service.WriteDataByLocalIdentifier, p);
    }
}
