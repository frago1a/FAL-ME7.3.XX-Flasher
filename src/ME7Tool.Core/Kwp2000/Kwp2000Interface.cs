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
        catch (Kwp2000Exception ex) when (RetryAfterP3Max && ex is not Kw1281Exception)
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
                catch (Kw1281Exception) { throw; } // a ECU existe mas fala outro protocolo: dizê-lo, sem mais tentativas
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
                    var pulse = FastInitPulse();
                    _device.DiscardBuffers(); // limpa o ruído da transição do wake
                    _rxCount = 0; _rxPos = 0;
                    var req = new Kwp2000Message(InitAddressMode(target), target, TesterAddress,
                        (byte)Kwp2000Service.StartCommunication);
                    Kwp2000Message resp;
                    try { resp = SendRequest(req, FastInitResponseTimeoutMs); }
                    catch (Kwp2000Exception)
                    {
                        // Tempos reais do pulso (incluem a demora das chamadas ao cabo): para comparar a 1.ª tentativa,
                        // que falha logo após abrir o cabo, com a 2.ª, que liga. Norma: baixo 25 ± 1 ms, total 50 ± 1 ms.
                        LogMsg(T($"Pulso medido: baixo {pulse.LowMs:0.0} ms · alto {pulse.HighMs:0.0} ms (chamadas ao cabo: break on {pulse.BreakOnCallMs:0.0} ms, break off {pulse.BreakOffCallMs:0.0} ms).",
                                 $"Measured pulse: low {pulse.LowMs:0.0} ms · high {pulse.HighMs:0.0} ms (cable calls: break on {pulse.BreakOnCallMs:0.0} ms, break off {pulse.BreakOffCallMs:0.0} ms)."));
                        throw;
                    }
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
        dev.SetTiming(FrameP1, EnforceP3Min ? Timing.P3Min : 0, Timing.P4Min);
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
        _frameDevice?.SetTiming(FrameP1, EnforceP3Min ? Timing.P3Min : 0, Timing.P4Min);
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
    /// consistente (Thesis, EDC16C8, EDC16C39: só o eco) e a 2.ª liga; 2 cobre isso. Não é o pulso: medido no
    /// carro, 25,5 ms baixo / 25,1 ms alto, chamadas ao cabo 0,5 / 0,1 ms — e 1 s de repouso extra antes também
    /// não ajudou. Provavelmente a ECU ignora o 1.º wake-up depois de estar parada.
    /// </summary>
    public int FastInitAttemptsPerTarget { get; set; } = 2;

    /// <summary>
    /// Tempo de repouso (linha alta) antes do wake-up / do endereço a 5 baud — ISO 14230-2 W5 ≥ 300 ms.
    /// </summary>
    public int FastInitIdleMs { get; set; } = 300;

    /// <summary>Duração de cada bit no slow init (200 ms = 5 baud). Ajustável só para testes.</summary>
    public double FiveBaudBitMs { get; set; } = 200;

    /// <summary>Tempos reais de um pulso de fast-init (Stopwatch), para o log quando a ECU não responde.</summary>
    private readonly record struct PulseTiming(double LowMs, double HighMs, double BreakOnCallMs, double BreakOffCallMs);

    /// <summary>
    /// Pulso de fast-init (ISO 14230): idle alto ≥ W5, 25 ms baixo, 25 ms alto. Busy-wait preciso a contar do fim
    /// de cada chamada ao cabo (como sempre funcionou na 2.ª tentativa); mede também quanto demorou cada chamada.
    /// </summary>
    private PulseTiming FastInitPulse()
    {
        _device.SetBreak(false);
        IdleWait(FastInitIdleMs);
        long t0 = Stopwatch.GetTimestamp();
        _device.SetBreak(true);                                    // TiniL (baixo)
        long c0 = Stopwatch.GetTimestamp();
        WaitUntilTicks(c0, 25);
        long t1 = Stopwatch.GetTimestamp();
        _device.SetBreak(false);                                   // alto antes do StartCommunication
        long c1 = Stopwatch.GetTimestamp();
        WaitUntilTicks(c1, 25);
        long t2 = Stopwatch.GetTimestamp();
        double f = 1000.0 / Stopwatch.Frequency;
        return new PulseTiming((t1 - t0) * f, (t2 - t1) * f, (c0 - t0) * f, (c1 - t1) * f);
    }

    /// <summary>Busy-wait até <paramref name="ms"/> depois de <paramref name="startTicks"/> (não acumula a demora da chamada).</summary>
    private static void WaitUntilTicks(long startTicks, double ms)
    {
        long end = startTicks + (long)(Stopwatch.Frequency * ms / 1000.0);
        while (Stopwatch.GetTimestamp() < end) { }
    }

    /// <summary>
    /// Slow init (ISO 14230-2 / ISO 9141): endereço a 5 baud (start, 8 bits LSB primeiro, stop) por
    /// break; depois a 10400: ECU envia 0x55 (W1 60–300 ms), KB1, KB2 (W2/W3 ≤ 20 ms); o tester
    /// responde ~KB2 após W4 (25–50 ms) e a ECU confirma com ~endereço.
    /// </summary>
    private void FiveBaudInit(byte address)
    {
        // Algumas ECUs (ex.: VAG antigas) respondem a 9600 e não a 10400: um 0x55 a 9600 lido a 10400 sai lixo.
        // Tenta cada baud de FiveBaudRates; o que falhou fica no log com os bytes que chegaram.
        var rates = FiveBaudRates is { Length: > 0 } ? FiveBaudRates : new[] { InitBaudRate };
        Kwp2000Exception? last = null;
        foreach (int rate in rates)
        {
            try { FiveBaudInitAt(address, rate); return; }
            catch (Kw1281Exception) { throw; } // a ECU respondeu: não vale a pena outro baud
            catch (Kwp2000Exception ex)
            {
                last = ex;
                if (rates.Length > 1) LogMsg(T($"5 baud a {rate}: {ex.Message}", $"5 baud at {rate}: {ex.Message}"));
                // A ECU respondeu (a outro baud) e ficou à espera do ~KB2: dá-lhe tempo para desistir antes de
                // outro endereço a 5 baud — na EDC15P, 0,3 s não chegava e a tentativa seguinte ficava calada.
                if (_fiveBaudGotBytes) Thread.Sleep(FiveBaudRetryPauseMs);
            }
        }
        throw last!;
    }

    /// <summary>Bauds a ouvir depois do endereço a 5 baud, por ordem. 10400 (KWP2000) e 9600 (algumas VAG antigas).</summary>
    public int[] FiveBaudRates { get; set; } = { 10400, 9600 };

    /// <summary>Pausa antes de outro endereço a 5 baud quando a ECU respondeu a outro baud (ficou à espera do ~KB2).</summary>
    public int FiveBaudRetryPauseMs { get; set; } = 3000;

    /// <summary>A última tentativa a 5 baud recebeu bytes (mesmo sem 0x55).</summary>
    private bool _fiveBaudGotBytes;

    /// <summary>A ECU respondeu ao 5 baud com key bytes de KW1281 (protocolo antigo da VAG), que este código não fala.</summary>
    public sealed class Kw1281Exception(string message, int rate, byte address) : Kwp2000Exception(message)
    {
        /// <summary>Baud a que a ECU respondeu (para ligar logo em KW1281).</summary>
        public int Rate => rate;
        public byte Address => address;
    }

    private void FiveBaudInitAt(byte address, int rate)
    {
        _fiveBaudGotBytes = false;
        LogMsg(T($"5 baud → endereço 0x{address:X2} (resposta a {rate} baud)…", $"5 baud → address 0x{address:X2} (answer at {rate} baud)…"));
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
        _device.SetBaudRate(rate);
        _device.DiscardBuffers();
        _rxCount = 0; _rxPos = 0;

        var one = new byte[1];
        var junk = new List<byte>();                 // o que chegou antes do 0x55 (para o log)
        long deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * (FiveBaudBitMs + 400) / 1000);
        while (true)
        {
            if (Stopwatch.GetTimestamp() > deadline)
            {
                _fiveBaudGotBytes = junk.Count > 0;
                // "x5 01 0A" a 10400 = "55 01 8A" de uma ECU KW1281 a 9600 (visto na EDC15P: B5 01 0A, A5 01 0A):
                // o 0x55 sai torto, mas KB1 = 01 e KB2 = 0A/8A passam inteiros. Não se gasta outra tentativa.
                if (rate != 9600 && junk.Count >= 3 && (junk[^3] & 0x0F) == 0x05 && junk[^2] == 0x01 && (junk[^1] & 0x7F) == 0x0A)
                    throw new Kw1281Exception(T(
                        $"a {rate} baud chegou {Convert.ToHexString(junk.ToArray())} = 55 01 8A de uma ECU a 9600 → KW1281 (protocolo antigo da VAG, o do VCDS), não KWP2000.",
                        $"at {rate} baud {Convert.ToHexString(junk.ToArray())} arrived = 55 01 8A from an ECU at 9600 → KW1281 (old VAG protocol, the one VCDS uses), not KWP2000."), 9600, address);
                string got = junk.Count == 0
                    ? T("não chegou nenhum byte", "no byte arrived")
                    : T($"chegaram {junk.Count} byte(s) sem 0x55: {Convert.ToHexString(junk.Take(16).ToArray())}{(junk.Count > 16 ? "…" : "")} (baud errado?)",
                        $"{junk.Count} byte(s) arrived without 0x55: {Convert.ToHexString(junk.Take(16).ToArray())}{(junk.Count > 16 ? "…" : "")} (wrong baud?)");
                throw new Kwp2000TimeoutException(T($"sem byte de sincronismo 0x55 — {got}.", $"no 0x55 sync byte — {got}."));
            }
            try { ReadInto(one, 50); } catch (Kwp2000TimeoutException) { continue; }
            if (one[0] == 0x55) break;
            junk.Add(one[0]);                        // ignora lixo antes do sync, mas guarda-o
        }
        if (junk.Count > 0) LogMsg(T($"Antes do 0x55 chegou: {Convert.ToHexString(junk.ToArray())}.", $"Before 0x55 arrived: {Convert.ToHexString(junk.ToArray())}."));

        var kb = new byte[2];
        ReadInto(kb, 100);
        LogMsg($"Sync 0x55 a {rate} baud · key bytes {kb[0]:X2} {kb[1]:X2}.");

        // KW1281 (VAG antigo): KB1 = 0x01, KB2 = 0x8A (o 0x80 do KB2 é a paridade). Depois do ~KB2 a ECU manda
        // blocos de identificação, não o ~endereço do KWP2000 — este código não fala esse protocolo.
        if ((kb[0] & 0x7F) == 0x01 && (kb[1] & 0x7F) == 0x0A)
            throw new Kw1281Exception(T(
                $"a ECU respondeu a {rate} baud com key bytes {kb[0]:X2} {kb[1]:X2} = KW1281 (protocolo antigo da VAG, o do VCDS), não KWP2000.",
                $"the ECU answered at {rate} baud with key bytes {kb[0]:X2} {kb[1]:X2} = KW1281 (old VAG protocol, the one VCDS uses), not KWP2000."), rate, address);

        BusyWaitMs(30);                              // W4 (25–50 ms)
        byte inv = (byte)~kb[1];
        _device.Write(new[] { inv });
        if (ExpectEcho) ReadInto(one, 100);          // eco do ~KB2

        ReadInto(one, 200);                          // ~endereço (W4)
        if (one[0] != (byte)~address)
            throw new Kwp2000Exception(T($"ECU respondeu 0x{one[0]:X2} em vez de ~endereço 0x{(byte)~address:X2}.", $"ECU answered 0x{one[0]:X2} instead of ~address 0x{(byte)~address:X2}."));

        if (rate != InitBaudRate)
        {
            LogMsg(T($"A ECU respondeu a {rate} baud (não {InitBaudRate}): passo a usar {rate}.", $"The ECU answered at {rate} baud (not {InitBaudRate}): using {rate} from now on."));
            InitBaudRate = rate;
        }
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

    /// <summary>Resultado de cada código na última <see cref="ReadIdentification"/> ("92:ok/40ms", "90:NRC12/30ms", "93:—800ms" = calada).</summary>
    public IReadOnlyList<string> LastIdentificationTrace { get; private set; } = Array.Empty<string>();

    /// <summary>Códigos do ReadEcuIdentification (0x1A) pedidos ao ligar, com o nome (ISO 14230-3).</summary>
    public static IReadOnlyList<(byte Id, string Pt, string En)> IdentificationIds { get; } = new (byte, string, string)[]
    {
        (0x90, "VIN", "VIN"),
        (0x91, "Nº de peça do fabricante (hardware)", "Vehicle manufacturer hardware number"),
        (0x87, "Nº de peça de substituição", "Spare part number"),
        (0x88, "Nº de software do fabricante", "Vehicle manufacturer software number"),
        (0x89, "Versão de software do fabricante", "Vehicle manufacturer software version"),
        (0x92, "Nº de hardware do fornecedor (ex.: Bosch 0281…)", "System supplier hardware number (e.g. Bosch 0281…)"),
        (0x93, "Versão de hardware do fornecedor", "System supplier hardware version"),
        (0x94, "Nº de software do fornecedor (ex.: 1037…)", "System supplier software number (e.g. 1037…)"),
        (0x95, "Versão de software do fornecedor", "System supplier software version"),
        (0x97, "Sistema / motor", "System name / engine type"),
        (0x8A, "Fornecedor", "System supplier"),
        (0x8C, "Nº de série da ECU", "ECU serial number"),
        (0x8B, "Data de fabrico", "Manufacturing date"),
        (0x99, "Data de programação", "Programming date"),
        (0x98, "Código da oficina / tester", "Repair shop / tester code"),
        (0x96, "Homologação", "Type approval number"),
        // registos completos: a EDC16C8 só dá o nº de hardware Bosch dentro do 83
        (0x83, "Registo de identificação (83)", "Identification record (83)"),
        (0x80, "Registo de identificação (80)", "Identification record (80)"),
    };

    /// <summary>
    /// Lê a identificação da ECU (0x1A) para os códigos de <see cref="IdentificationIds"/>. Devolve só os que
    /// respondem (texto se for legível, senão hex). Não lança: os que a ECU recusa ou ignora ficam de fora.
    /// </summary>
    public List<(byte Id, string Name, string Value)> ReadIdentification(int timeoutMs = 400)
    {
        var found = new List<(byte, string, string)>();
        // P3min normal (ISO/Fiat 07223: 55 ms) durante a leitura: é feita na sessão normal, antes de negociar os
        // timings rápidos. Com P3min 0 a EDC16C8 respondia ao 1.º pedido, calava-se e depois dava BusyRepeatRequest
        // (0x21) à sessão 0x86. O P4 (entre bytes do tester) fica como está.
        int p3 = Timing.P3Min;
        bool enforce = EnforceP3Min;
        Timing.P3Min = Math.Max(p3, KwpTiming.Normal().P3Min);
        EnforceP3Min = true;
        _frameDevice?.SetTiming(FrameP1, Timing.P3Min, Timing.P4Min);
        try
        {
            int silent = 0;
            var trace = new List<string>();
            LastIdentificationTrace = trace;
            // A EDC16C8 responde 7F 1A 21 ("ocupada") a alguns códigos e só dá a resposta verdadeira quando recebe o
            // pedido seguinte: depois de um 0x21 repete-se o mesmo pedido (ISO 14230-3). Cada resposta positiva conta
            // pelo código que traz (5A <código>), não pelo pedido em que chegou.
            var values = new Dictionary<byte, string>();
            void Take(byte[]? resp)
            {
                if (resp is { Length: >= 3 } && resp[0] == 0x5A && !values.ContainsKey(resp[1]))
                {
                    string v = FormatIdentification(resp.AsSpan(2));
                    if (v.Length > 0) values[resp[1]] = v;
                }
            }
            foreach (var (id, _, _) in IdentificationIds)
            {
                long t0 = Stopwatch.GetTimestamp();
                string what = "";
                byte[]? r = null;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    r = TryRaw(new byte[] { (byte)Kwp2000Service.ReadECUIdentification, id }, timeoutMs);
                    Take(r);
                    bool busy = r is { Length: >= 3 } && r[0] == 0x7F && r[2] == (byte)Kwp2000ResponseCode.BusyRepeatRequest;
                    what += (what.Length > 0 ? "→" : "") + (r == null ? "—" : r.Length >= 3 && r[0] == 0x7F ? $"NRC{r[2]:X2}" : r.Length >= 2 && r[1] != id ? $"ok({r[1]:X2})" : "ok");
                    if (!busy || values.ContainsKey(id)) break;
                }
                long ms = (Stopwatch.GetTimestamp() - t0) * 1000 / Stopwatch.Frequency;
                trace.Add($"{id:X2}:{what}/{ms}ms");
                if (r == null) { if (++silent >= 4) break; continue; } // ECU calada: não gastar mais tempo
                silent = 0;
            }
            foreach (var (id, pt, en) in IdentificationIds)
                if (values.TryGetValue(id, out var v)) found.Add((id, T(pt, en), v));
        }
        finally
        {
            BusyWaitMs(Timing.P3Min); // o pedido seguinte (sessão) também respeita o P3min da sessão normal
            Timing.P3Min = p3; EnforceP3Min = enforce;
            _frameDevice?.SetTiming(FrameP1, EnforceP3Min ? Timing.P3Min : 0, Timing.P4Min);
        }
        return found;
    }

    /// <summary>
    /// Texto se pelo menos 3/4 for ASCII imprimível (os outros bytes viram espaços — ex.: registo 83 da EDC16C8, texto
    /// com alguns bytes binários pelo meio); senão hex. Espaços repetidos e os das pontas são cortados.
    /// </summary>
    public static string FormatIdentification(ReadOnlySpan<byte> data)
    {
        int printable = 0, n = 0;
        foreach (byte b in data) { if (b is 0x00 or 0xFF) continue; n++; if (b >= 0x20 && b < 0x7F) printable++; }
        if (n == 0) return "";
        if (printable * 4 >= n * 3)
        {
            var sb = new System.Text.StringBuilder();
            foreach (byte b in data) sb.Append(b >= 0x20 && b < 0x7F ? (char)b : ' ');
            return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), " {2,}", " ").Trim();
        }
        return Convert.ToHexString(data.ToArray());
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
    private readonly byte[] _traceRxRaw = new byte[256];   // bytes crus recebidos no pedido (eco + resposta), p/ diagnóstico de timeout
    private int _traceRxRawN;

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
        else if (_traceRxRawN > 0)   // sem frame montado (ex.: timeout): mostra os bytes crus recebidos
            sb.Append("| cru ").Append(Convert.ToHexString(_traceRxRaw.AsSpan(0, _traceRxRawN)));
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
    /// Num interface J2534, subir o P1max para <see cref="FrameP1Fallback"/> quando um pedido fica sem nenhum byte de
    /// resposta (o interface descartou-a: a ECU deixou mais do que P1max entre bytes). Por omissão true.
    /// </summary>
    public bool AdaptiveFrameP1 { get; set; } = true;

    /// <summary>P1max (ms) a usar no interface depois de detetar respostas perdidas. A ME3.1 precisa de ~20 ms.</summary>
    public int FrameP1Fallback { get; set; } = 20;

    /// <summary>Piso do P1max nesta ligação (0 = nenhum); sobe para FrameP1Fallback ao detetar respostas perdidas.</summary>
    private int _frameP1Floor;

    /// <summary>P1max efetivo para o interface J2534: o da KwpTiming, nunca abaixo do piso desta ligação.</summary>
    private int FrameP1 => Math.Max(Math.Max(1, Timing.P1Max), _frameP1Floor);

    /// <summary>Serviços de leitura que se podem repetir sem efeitos (identificação, leituras, TesterPresent, pedido de seed).</summary>
    private static bool IsSafeToRepeat(Kwp2000Message request) => request.Data.Length > 0 && request.Data[0] switch
    {
        0x1A or 0x21 or 0x22 or 0x23 or 0x3E or 0x17 or 0x18 => true,
        0x27 => request.Data.Length > 1 && (request.Data[1] & 1) == 1,   // pedir seed (subfunção ímpar)
        _ => false,
    };

    /// <summary>
    /// Envia um pedido e devolve a resposta. Trata eco e responsePending (0x78).
    /// Lança <see cref="Kwp2000NegativeResponseException"/> em resposta negativa (exceto 0x78).
    /// Num J2534, se a resposta se perder por inteiro com P1max curto, sobe o P1max (ver AdaptiveFrameP1) e, se o
    /// pedido for só de leitura, repete-o — a ME3.1 deixa &gt; 5 ms entre bytes nas respostas longas (medido no carro:
    /// 1A 92 sem resposta a 5 ms, OK a 20 ms) e o Scanmatik deitava a resposta fora.
    /// </summary>
    public Kwp2000Message SendRequest(Kwp2000Message request, int? responseTimeoutMs = null)
    {
        try { return SendRequestStaleRetry(request, responseTimeoutMs); }
        catch (Kwp2000TimeoutException) when (AdaptiveFrameP1 && _frameDevice != null && FrameP1 < FrameP1Fallback && _traceRxRawN == 0)
        {
            int prevFloor = _frameP1Floor;
            _frameP1Floor = FrameP1Fallback;
            _frameDevice.SetTiming(FrameP1, EnforceP3Min ? Timing.P3Min : 0, Timing.P4Min);
            if (!IsSafeToRepeat(request))
            {
                LogMsg(T($"Sem resposta com P1max curto: P1max passa a {FrameP1} ms nesta ligação, por precaução.",
                         $"No reply with a short P1max: P1max set to {FrameP1} ms for this connection, as a precaution."));
                throw;
            }
            try
            {
                var r = SendRequestCore(request, responseTimeoutMs);
                LogMsg(T($"O interface perdeu a resposta (P1max curto para esta ECU): P1max passa a {FrameP1} ms nesta ligação.",
                         $"The interface lost the reply (P1max too short for this ECU): P1max set to {FrameP1} ms for this connection."));
                return r;
            }
            catch (Kwp2000TimeoutException)
            {
                // também calada com P1max folgado: não era o P1max (ex.: ECU a reiniciar depois do flash) — repõe
                _frameP1Floor = prevFloor;
                _frameDevice.SetTiming(FrameP1, EnforceP3Min ? Timing.P3Min : 0, Timing.P4Min);
                throw;
            }
        }
    }

    /// <summary>
    /// Se o pedido recebeu uma resposta atrasada de outro pedido (descartada) e depois ficou sem a sua, a ECU ignorou-o
    /// (estava a enviar a resposta antiga — visto na ME3.1 com pedidos seguidos sem P3min): repete-se uma vez, se for
    /// só de leitura.
    /// </summary>
    private Kwp2000Message SendRequestStaleRetry(Kwp2000Message request, int? responseTimeoutMs)
    {
        try { return SendRequestCore(request, responseTimeoutMs); }
        catch (Kwp2000TimeoutException) when (_lastRequestHadStale && IsSafeToRepeat(request))
        {
            LogMsg(T("A ECU respondeu com a resposta de um pedido anterior e não à deste: repete-se o pedido.",
                     "The ECU answered with the reply to an earlier request and not to this one: repeating the request."));
            return SendRequestCore(request, responseTimeoutMs);
        }
    }

    private bool _lastRequestHadStale;

    private Kwp2000Message SendRequestCore(Kwp2000Message request, int? responseTimeoutMs)
    {
        _lastRequestHadStale = false;
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
            _traceN = 0; _traceT1 = t1; _traceTx = frame; _traceRx = null; _traceRxRawN = 0;

            if (ExpectEcho)
                ReadExact(frame.Length, Timing.ResponseTimeoutMs); // descarta eco
            long t2 = Stopwatch.GetTimestamp();

            int timeout = responseTimeoutMs ?? Timing.ResponseTimeoutMs;
            int staleReplies = 0;
            while (true)
            {
                var resp = ReadFrame(timeout, out long firstByteTicks);
                long t3 = Stopwatch.GetTimestamp();
                if (resp.IsNegativeResponse &&
                    resp.NegativeResponseCode == Kwp2000ResponseCode.ResponsePending)
                {
                    // ECU pediu para esperar: voltar a ler, agora com P2* (a resposta final pode levar segundos,
                    // ex.: a ME3.1 a gravar o tester code na EEPROM respondeu 7B 98 ~2 s depois do 78).
                    timeout = Math.Max(timeout, Timing.ResponsePendingTimeoutMs);
                    continue;
                }
                if (IsStaleReply(request, resp))
                {
                    // resposta atrasada de um pedido anterior (ex.: a ME3.1 respondeu ao 1A 91 depois do timeout e a
                    // resposta foi lida como a do 1A 92): deita-se fora e espera-se pela deste pedido
                    LogMsg(T($"Descartada uma resposta atrasada de outro pedido ({Convert.ToHexString(resp.Data.AsSpan(0, Math.Min(resp.Data.Length, 8)))}…).",
                             $"Discarded a late reply to another request ({Convert.ToHexString(resp.Data.AsSpan(0, Math.Min(resp.Data.Length, 8)))}…)."));
                    _lastRequestHadStale = true;
                    if (++staleReplies > MaxStaleReplies)
                        throw new Kwp2000Exception(T("Respostas que não correspondem ao pedido.", "Replies that do not match the request."));
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

    private const int MaxStaleReplies = 3;

    /// <summary>
    /// true se a resposta é claramente de outro pedido: serviço diferente (positiva SID+0x40, negativa 7F SID) ou, no
    /// ReadECUIdentification (0x1A), outra opção — a norma manda ecoar a opção (5A opção …).
    /// </summary>
    private static bool IsStaleReply(Kwp2000Message request, Kwp2000Message resp)
    {
        if (request.Data.Length == 0 || resp.Data.Length == 0) return false;
        byte sid = request.Data[0];
        if (resp.IsNegativeResponse) return resp.NegativeRequestServiceId is byte n && n != sid;
        if (resp.Data[0] != (byte)(sid + 0x40)) return true;
        return sid == 0x1A && request.Data.Length > 1 && resp.Data.Length > 1 && resp.Data[1] != request.Data[1];
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
                if (_traceRxRawN < _traceRxRaw.Length)   // guarda os bytes crus p/ o trace (diagnóstico)
                {
                    int m = Math.Min(n, _traceRxRaw.Length - _traceRxRawN);
                    _rxBuf.AsSpan(_rxPos, m).CopyTo(_traceRxRaw.AsSpan(_traceRxRawN));
                    _traceRxRawN += m;
                }
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

        // ME3.1 (selecionada): vai direto ao esquema de códigos de baud, sem tentar o byte standard (que daria NRC).
        if (wantBaud && Me31BaudScheme)
        {
            if (TryMe31FastBaud(session, Me31BaudParams(workingBaud), workingBaud)) return;
            LogMsg(T($"Esquema ME3.1 não aceite; continua a {InitBaudRate}.", $"ME3.1 scheme not accepted; staying at {InitBaudRate}."));
            wantBaud = false;
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
                int finalBaud = ConfirmBaudSwitch((int)newBaud);
                LogMsg(T($"Sessão 0x{(byte)session:X2} OK · baud de trabalho {finalBaud}.", $"Session 0x{(byte)session:X2} OK · working baud {finalBaud}."));
                return;
            }
            catch (Kwp2000NegativeResponseException ex) when (
                ex.ResponseCode is Kwp2000ResponseCode.SubFunctionNotSupportedOrInvalidFormat
                    or Kwp2000ResponseCode.RequestOutOfRange
                    or Kwp2000ResponseCode.ConditionsNotCorrectOrRequestSequenceError)
            {
                LogMsg(T($"ECU recusou baud {workingBaud} ({ex.ResponseCode}); continua a {InitBaudRate}.", $"ECU refused baud {workingBaud} ({ex.ResponseCode}); staying at {InitBaudRate}."));
                // SEM fallback automático para o esquema da ME3.1: numa ME7.3.x o código 0x05 é um byte de baud
                // válido pela fórmula (7400 baud) — a ECU aceitava e ecoava 0x05, e nós trocávamos para 115200
                // → ligação perdida. O esquema da ME3.1 só se usa quando a ECU é escolhida (Me31BaudScheme).
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

    /// <summary>Código de baud da ME3.1 para 115200 no 0x85 (Stilo dump loc_2C70). Código 4 = 57600 se preciso.</summary>
    public const byte Me31Baud115200Code = 0x05;
    public const int Me31Baud115200 = 115200;

    /// <summary>
    /// Usa diretamente o esquema de códigos de baud da ME3.1 no 0x85 (sem tentar primeiro o byte KWP standard,
    /// que a ME3.1 recusa). Posto pelo flasher quando a ECU escolhida é ME3.1.
    /// </summary>
    public bool Me31BaudScheme { get; set; }

    /// <summary>
    /// Depois de trocar de baud, confirmar com um TesterPresent que a ECU responde ao baud novo (por omissão true).
    /// A resposta positiva ao 10 xx nem sempre traz o byte de baud (a ME7.3.1 responde só 50 85 e troca; a ME3.1 na
    /// sessão 0x86 responde 50 86 e NÃO troca), por isso não dá para decidir pela resposta.
    /// </summary>
    public bool VerifyBaudSwitch { get; set; } = true;

    /// <summary>Timeout de cada TesterPresent da verificação do baud.</summary>
    public int BaudCheckTimeoutMs { get; set; } = 500;

    /// <summary>A ECU responde a um TesterPresent ao baud atual? Uma resposta negativa também conta (a ligação está certa).</summary>
    private bool LinkAnswers()
    {
        try { SendRequest(Kwp2000Message.Request(EcuAddress, TesterAddress, Kwp2000Service.TesterPresent), BaudCheckTimeoutMs); return true; }
        catch (Kwp2000NegativeResponseException) { return true; }
        catch (Kwp2000Exception) { return false; }
    }

    /// <summary>
    /// Confirma a troca de baud: se a ECU não responder ao baud novo (2 tentativas), volta ao baud de init e confirma
    /// lá — caso da ME3.1 na sessão 0x86, que aceita o 10 86 &lt;baud&gt; mas fica a 10400. Devolve o baud com que ficou.
    /// </summary>
    private int ConfirmBaudSwitch(int newBaud)
    {
        if (!VerifyBaudSwitch || newBaud == InitBaudRate) return newBaud;
        if (LinkAnswers() || LinkAnswers()) return newBaud;
        _device.SetBaudRate(InitBaudRate);
        if (LinkAnswers())
        {
            LogMsg(T($"A ECU aceitou a sessão mas não trocou de baud (sem resposta a {newBaud}): continua a {InitBaudRate}.",
                     $"The ECU accepted the session but did not switch baud (no answer at {newBaud}): staying at {InitBaudRate}."));
            return InitBaudRate;
        }
        _device.SetBaudRate(newBaud);
        LogMsg(T($"AVISO: sem resposta ao TesterPresent nem a {newBaud} nem a {InitBaudRate}; fica a {newBaud}.",
                 $"WARNING: no answer to TesterPresent at {newBaud} nor at {InitBaudRate}; staying at {newBaud}."));
        return newBaud;
    }

    /// <summary>Baud recomendado para a ME3.1: 125000 é exato na UART a 24 MHz (24e6/32/6) — o mesmo da ME7.3.1.</summary>
    public const int Me31RecommendedBaud = 125000;

    /// <summary>
    /// Parâmetros do 10 85 para um baud no esquema da ME3.1 (Stilo dump loc_2C70): códigos fixos 1=9600, 2=19200,
    /// 3=38400, 4=57600, 5=115200, 0xFA=187500; qualquer outro vai pelo código 6 com o baud em 3 bytes (big-endian).
    /// Atenção: a ECU só aceita o baud se a UART (24 MHz) o gerar com erro pequeno — 115200 é recusado (NRC 0x31,
    /// confirmado no carro); 125000 e 187500 são exatos.
    /// </summary>
    public static byte[] Me31BaudParams(int baud)
    {
        // 124800 (o valor da ME7.3.x, byte 0x87) pede-se como 125000 exato: é o que a UART gera nas duas ECUs.
        if (baud == 124800) baud = Me31RecommendedBaud;
        return baud switch
        {
            9600 => new byte[] { 1 }, 19200 => new byte[] { 2 }, 38400 => new byte[] { 3 }, 57600 => new byte[] { 4 },
            115200 => new byte[] { 5 }, 187500 => new byte[] { 0xFA },
            _ => new byte[] { 6, (byte)(baud >> 16), (byte)(baud >> 8), (byte)baud },
        };
    }

    /// <summary>
    /// Troca de baud no esquema da ME3.1: 10 &lt;session&gt; &lt;código&gt; [baud em 3 bytes] → &lt;baud&gt;. A ECU responde a
    /// 10400 (50 85) e só depois ambos mudam. Se a resposta ecoar um byte de baud diferente do pedido, não troca.
    /// Devolve false (sem trocar) se a ECU recusar ou o interface não aceitar o baud.
    /// </summary>
    private bool TryMe31FastBaud(Kwp2000SessionType session, byte[] baudParams, int baud)
    {
        byte code = baudParams[0];
        if (_frameDevice != null)   // J2534: confirmar que o interface aceita o baud antes de pedir
        {
            try { _device.SetBaudRate(baud); _device.SetBaudRate(InitBaudRate); }
            catch (Exception ex)
            {
                try { _device.SetBaudRate(InitBaudRate); } catch { }
                LogMsg(T($"O interface não aceita {baud} baud ({ex.Message}).", $"The interface does not accept {baud} baud ({ex.Message})."));
                return false;
            }
        }
        try
        {
            var resp = SendService(Kwp2000Service.StartDiagnosticSession, new[] { (byte)session }.Concat(baudParams).ToArray());
            if (resp.Data.Length >= 3 && resp.Data[2] != code)
            {
                LogMsg(T($"ECU aceitou mas ecoou código 0x{resp.Data[2]:X2} ≠ 0x{code:X2}: não troco o baud (fica a {InitBaudRate}).",
                         $"ECU accepted but echoed code 0x{resp.Data[2]:X2} ≠ 0x{code:X2}: not switching baud (staying at {InitBaudRate})."));
                CurrentSession = session;
                return true;   // sessão feita, mas sem troca de baud
            }
            CurrentSession = session;
            _device.SetBaudRate(baud);   // troca SÓ depois do ack
            int finalBaud = ConfirmBaudSwitch(baud);
            LogMsg(T($"Sessão 0x{(byte)session:X2} OK · baud de trabalho {finalBaud} (esquema ME3.1, código 0x{code:X2}).",
                     $"Session 0x{(byte)session:X2} OK · working baud {finalBaud} (ME3.1 scheme, code 0x{code:X2})."));
            return true;
        }
        catch (Kwp2000NegativeResponseException)
        {
            return false;   // não é ME3.1 (ou não aceita): o chamador continua para 10400
        }
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
        // A ME3.1 (Stilo/Thesis 2.4, outro software no mesmo C167) tem outro nível de acesso: o handler 0x27
        // (Stilo Dump Origine: sub_31EC, filtro em ~0x2224) só aceita a subfunção se (sub+1)>>1 == 0x2B, ou seja
        // 0x55 (pedir seed) / 0x56 (enviar key); a subfunção 0x01 dá NRC 0x12. A key usa o MESMO algoritmo
        // (poly 0x564A4F25, N = 0x23), e ComputeFiatKey(…, 0x55, 0, …) já escolhe esse poly (nível 0x41).
        // Por isso, se a subfunção configurada der NRC 0x12, tenta-se a 0x55 da ME3.1. A subfunção errada é
        // recusada ANTES de pedir seed e NÃO gasta tentativas de key — por isso esta deteção é segura.
        byte effSub = seedSubFunction;
        byte keyParameter = seedParameter;
        Kwp2000Message seedResp;
        try { seedResp = SendService(Kwp2000Service.SecurityAccess, seedSubFunction, seedParameter); }
        catch (Kwp2000NegativeResponseException ex) when (ex.ResponseCode == Kwp2000ResponseCode.SubFunctionNotSupportedOrInvalidFormat
                                                          && seedSubFunction != SecurityAccess.Me31SeedSubFunction)
        {
            effSub = SecurityAccess.Me31SeedSubFunction;
            keyParameter = 0;
            LogMsg(T($"SecurityAccess: a ECU recusou 27 {seedSubFunction:X2} (NRC 0x12); tenta a subfunção 0x{effSub:X2} da ME3.1.",
                     $"SecurityAccess: the ECU rejected 27 {seedSubFunction:X2} (NRC 0x12); trying the ME3.1 sub-function 0x{effSub:X2}."));
            seedResp = SendService(Kwp2000Service.SecurityAccess, effSub, keyParameter);
        }

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

        uint key = SecurityAccess.ComputeFiatKey(seed, effSub, keyParameter, perEcuPoly);

        byte keySub = (byte)(effSub + 1);
        try
        {
            SendService(Kwp2000Service.SecurityAccess, keySub,
                (byte)(key >> 24), (byte)(key >> 16), (byte)(key >> 8), (byte)key);
        }
        catch (Kwp2000NegativeResponseException ex) when ((byte)ex.ResponseCode == 0x35)
        {
            // não repetir: cada key errada conta para o bloqueio da ECU
            throw new Kwp2000Exception(T($"A ECU recusou a key (NRC 0x35, key inválida): o seed foi aceite mas o cálculo da key não é o desta ECU. Não se repete para não bloquear a ECU. Seed {seed:X8}, key enviada {key:X8}.",
                                         $"The ECU rejected the key (NRC 0x35, invalid key): the seed was accepted but the key calculation is not this ECU's. Not retried, so the ECU does not lock out. Seed {seed:X8}, key sent {key:X8}."));
        }
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
