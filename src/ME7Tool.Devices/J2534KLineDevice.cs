using static ME7Tool.Core.Common.Lang;
using ME7Tool.Core.Transport;
using ME7Tool.Devices.J2534;

namespace ME7Tool.Devices;

/// <summary>
/// K-line KWP2000 sobre J2534 (protocolo ISO14230, ex.: Scanmatik 2, Mongoose). O interface faz o init,
/// o framing, o checksum e os tempos; aqui só se converte para o fluxo de bytes que o
/// <see cref="ME7Tool.Core.Kwp2000.Kwp2000Interface"/> espera: Write recebe frames completas (com checksum,
/// que se retira — o interface acrescenta o seu), Read devolve as respostas já com checksum e sem eco.
/// </summary>
public sealed class J2534KLineDevice : IKLineFrameDevice, ME7Tool.Core.Transport.IBatteryMonitor
{
    private readonly string _dllPath;
    private J2534Native? _n;
    private uint _deviceId, _channelId;
    private int _baud = 10400;
    private int _p1MaxMs = 40, _p3MinMs = 0, _p4MinMs = 0;

    private readonly List<byte> _tx = new();
    private readonly Queue<byte> _rx = new();

    public string Name { get; }
    public bool IsOpen { get; private set; }

    /// <summary>Mensagens de diagnóstico (avisos de configuração do interface).</summary>
    public Action<string>? OnLog { get; set; }

    public J2534KLineDevice(string dllPath, string? name = null)
    {
        _dllPath = dllPath;
        Name = name ?? Path.GetFileNameWithoutExtension(dllPath);
    }

    public void Open()
    {
        if (IsOpen) return;
        _n = J2534Native.Load(_dllPath);
        try
        {
            Check(_n.Open(out _deviceId), "PassThruOpen");
            Check(_n.Connect(_deviceId, J2534Const.ISO14230, 0, (uint)_baud, out _channelId), "PassThruConnect (ISO14230)");

            // Filtro PASS que deixa passar tudo (máscara de 1 byte a zero).
            var mask = PassThruMsg.Create(J2534Const.ISO14230); mask.DataSize = 1;
            var pattern = PassThruMsg.Create(J2534Const.ISO14230); pattern.DataSize = 1;
            Check(_n.StartMsgFilter(_channelId, J2534Const.PASS_FILTER, ref mask, ref pattern, out _), "PassThruStartMsgFilter");

            Config((J2534Const.LOOP_BACK, 0));
            IsOpen = true;
            ApplyTiming();
        }
        catch
        {
            try { _n.Close(_deviceId); } catch { }
            _n.Dispose(); _n = null;
            throw;
        }
    }

    public void Close()
    {
        if (_n == null) return;
        try { _n.Disconnect(_channelId); } catch { }
        try { _n.Close(_deviceId); } catch { }
        _n.Dispose();
        _n = null;
        IsOpen = false;
        _lastConfigLine = null;
        _tx.Clear(); _rx.Clear();
    }

    public void Dispose() => Close();

    public void SetBaudRate(int baudRate)
    {
        if (baudRate == _baud) return;
        _baud = baudRate;
        if (!IsOpen) return;
        Check(_n!.SetConfig(_channelId, (J2534Const.DATA_RATE, (uint)baudRate)), $"SET_CONFIG DATA_RATE={baudRate}");
        // Alguns interfaces (visto no Scanmatik 2) repõem os tempos por omissão ao mudar o baud — P4_MIN
        // volta a 5 ms e cada pedido passava a demorar ~40 ms a sair. Volta a aplicá-los.
        Config((J2534Const.LOOP_BACK, 0));
        ApplyTiming();
    }

    public void SetTiming(int p1MaxMs, int p3MinMs, int p4MinMs)
    {
        _p1MaxMs = Math.Max(1, p1MaxMs); _p3MinMs = Math.Max(0, p3MinMs); _p4MinMs = Math.Max(0, p4MinMs);
        if (IsOpen) ApplyTiming();
    }

    /// <summary>Init (fast/5 baud) em curso: usa um P1max folgado.</summary>
    private bool _initMode;

    /// <summary>
    /// P1max que o interface usa. Depois de cada resposta o interface espera este tempo de linha calada antes de
    /// deixar sair o pedido seguinte, por isso é o que manda na cadência (Scanmatik 2: 40 ms → 17 Hz, 5 ms → 50 Hz).
    /// Abaixo de 5 ms o Scanmatik 2 deixa de receber (a 10400 corta o fast-init, a 124800 perde as respostas):
    /// 5 ms é o mínimo. No init usa-se ≥ 20 ms por folga.
    /// </summary>
    private int EffectiveP1Ms => _initMode ? Math.Max(_p1MaxMs, 20) : Math.Max(_p1MaxMs, 5);

    /// <summary>Os tempos J2534 vão em unidades de 0,5 ms.</summary>
    private void ApplyTiming()
    {
        Config((J2534Const.P1_MAX, (uint)(EffectiveP1Ms * 2)));
        Config((J2534Const.P3_MIN, (uint)(_p3MinMs * 2)));
        Config((J2534Const.P4_MIN, (uint)(_p4MinMs * 2)));
        LogConfig();
    }

    /// <summary>Mostra no log o que o interface ficou realmente a usar (lido de volta com GET_CONFIG).</summary>
    private void LogConfig()
    {
        if (OnLog == null) return;
        var v = _n!.GetConfig(_channelId, J2534Const.DATA_RATE, J2534Const.P1_MAX, J2534Const.P3_MIN, J2534Const.P4_MIN);
        string line = v == null
            ? T("J2534: o interface não deixa ler a configuração (GET_CONFIG).", "J2534: the interface does not allow reading the configuration (GET_CONFIG).")
            : $"J2534: {v[0]} baud · P1max {v[1] / 2.0:0.#} ms · P3min {v[2] / 2.0:0.#} ms · P4min {v[3] / 2.0:0.#} ms.";
        if (line == _lastConfigLine) return; // só quando muda
        _lastConfigLine = line;
        OnLog(line);
    }

    private string? _lastConfigLine;

    /// <summary>Configuração não essencial: se o interface recusar, avisa e continua com o valor dele.</summary>
    private void Config((uint Param, uint Value) item)
    {
        uint r = _n!.SetConfig(_channelId, item);
        if (r != J2534Const.STATUS_NOERROR)
            OnLog?.Invoke(T($"J2534: SET_CONFIG 0x{item.Param:X2}={item.Value} recusado (status={r}) {_n.GetLastError()}",
                            $"J2534: SET_CONFIG 0x{item.Param:X2}={item.Value} refused (status={r}) {_n.GetLastError()}"));
    }

    public void SetBreak(bool on)
    {
        if (on) throw new NotSupportedException(T("J2534: o init da K-line é feito pelo interface (sem break).", "J2534: K-line init is done by the interface (no break)."));
    }

    public void DiscardBuffers()
    {
        _tx.Clear(); _rx.Clear();
        if (IsOpen) _n!.Ioctl(_channelId, J2534Const.CLEAR_RX_BUFFER);
    }

    // ------------------------------------------------------------ init

    public byte[] FastInit(byte[] request)
    {
        EnsureOpen();
        _tx.Clear(); _rx.Clear();
        var msg = ToMsg(request.AsSpan(0, FrameLength(request) ?? request.Length));
        uint r;
        PassThruMsg resp;
        _initMode = true; ApplyTiming();
        try { r = _n!.FastInit(_channelId, ref msg, out resp); }
        finally { _initMode = false; ApplyTiming(); }
        if (r != J2534Const.STATUS_NOERROR)
            throw new IOException(T($"FAST_INIT falhou (status={r}) {_n.GetLastError()}", $"FAST_INIT failed (status={r}) {_n.GetLastError()}"));
        return WithChecksum(resp.Data.AsSpan(0, (int)Math.Min(resp.DataSize, (uint)resp.Data.Length)));
    }

    public byte[] FiveBaudInit(byte address)
    {
        EnsureOpen();
        _tx.Clear(); _rx.Clear();
        uint r;
        byte[] kb;
        _initMode = true; ApplyTiming();
        try { r = _n!.FiveBaudInit(_channelId, address, out kb); }
        finally { _initMode = false; ApplyTiming(); }
        if (r != J2534Const.STATUS_NOERROR)
            throw new IOException(T($"FIVE_BAUD_INIT falhou (status={r}) {_n.GetLastError()}", $"FIVE_BAUD_INIT failed (status={r}) {_n.GetLastError()}"));
        return kb;
    }

    // ------------------------------------------------------------ bytes ⇄ frames

    /// <summary>Junta os bytes até haver uma frame completa (com checksum) e envia-a sem o checksum.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        EnsureOpen();
        foreach (var b in data) _tx.Add(b);
        while (_tx.Count > 0)
        {
            var buf = _tx.ToArray();
            int? len = FrameLength(buf);
            if (len == null || buf.Length < len + 1) return; // falta o resto da frame
            var msg = ToMsg(buf.AsSpan(0, len.Value));
            uint num = 1;
            Check(_n!.WriteMsgs(_channelId, ref msg, ref num, (uint)Math.Max(1000, len.Value * 3)), "PassThruWriteMsgs");
            _tx.RemoveRange(0, len.Value + 1);
        }
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (_rx.Count == 0 && IsOpen)
        {
            var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
            while (_rx.Count == 0)
            {
                var msg = PassThruMsg.Create(J2534Const.ISO14230);
                uint num = 1;
                long left = Math.Max(0, deadline - Environment.TickCount64);
                uint r = _n!.ReadMsgs(_channelId, ref msg, ref num, (uint)left);
                if (r == J2534Const.STATUS_NOERROR && num > 0 && msg.DataSize > 0
                    && (msg.RxStatus & (J2534Const.TX_MSG_TYPE | J2534Const.START_OF_MESSAGE)) == 0)
                {
                    foreach (var b in WithChecksum(msg.Data.AsSpan(0, (int)Math.Min(msg.DataSize, (uint)msg.Data.Length)))) _rx.Enqueue(b);
                    break;
                }
                if (r != J2534Const.STATUS_NOERROR && r != J2534Const.ERR_BUFFER_EMPTY && r != 0x09 /* ERR_TIMEOUT */)
                    throw new IOException(T($"PassThruReadMsgs falhou (status={r}) {_n.GetLastError()}", $"PassThruReadMsgs failed (status={r}) {_n.GetLastError()}"));
                if (Environment.TickCount64 >= deadline) break;
            }
        }
        int n = Math.Min(buffer.Length, _rx.Count);
        for (int i = 0; i < n; i++) buffer[i] = _rx.Dequeue();
        return n;
    }

    /// <summary>Tamanho da frame KWP2000 SEM checksum (cabeçalho + dados), ou null se ainda faltar o cabeçalho.</summary>
    private static int? FrameLength(ReadOnlySpan<byte> f)
    {
        if (f.Length < 1) return null;
        int len = f[0] & 0x3F;
        int hlen = 1 + ((f[0] & 0xC0) != 0 ? 2 : 0);
        if (len == 0)
        {
            if (f.Length < hlen + 1) return null;
            len = f[hlen];
            hlen++;
        }
        return hlen + len;
    }

    /// <summary>
    /// A norma não é clara sobre se a resposta traz o checksum (uns interfaces mandam, outros retiram-no):
    /// se faltar, acrescenta-se, para o parser do KWP2000 ver sempre a frame completa.
    /// </summary>
    private static byte[] WithChecksum(ReadOnlySpan<byte> data)
    {
        int? len = FrameLength(data);
        if (len == null || data.Length != len.Value) return data.ToArray();
        var full = new byte[data.Length + 1];
        data.CopyTo(full);
        byte cs = 0;
        foreach (var b in data) cs += b;
        full[^1] = cs;
        return full;
    }

    private static PassThruMsg ToMsg(ReadOnlySpan<byte> frame)
    {
        var msg = PassThruMsg.Create(J2534Const.ISO14230);
        frame.CopyTo(msg.Data);
        msg.DataSize = (uint)frame.Length;
        return msg;
    }

    private void EnsureOpen()
    {
        if (!IsOpen || _n == null) throw new InvalidOperationException(T("J2534 não aberto.", "J2534 not open."));
    }

    private void Check(uint status, string op)
    {
        if (status != J2534Const.STATUS_NOERROR)
            throw new IOException(T($"{op} falhou (status={status}): {_n?.GetLastError()}", $"{op} failed (status={status}): {_n?.GetLastError()}"));
    }

    /// <summary>Tensão da bateria no pino 16 (READ_VBATT); null se fechado, recusado ou 0.</summary>
    public double? ReadBatteryVolts()
    {
        var n = _n;
        if (n == null || !IsOpen) return null;
        try { return n.ReadVbatt(_deviceId, out uint mv) == J2534Const.STATUS_NOERROR && mv > 0 ? mv / 1000.0 : null; }
        catch { return null; }
    }
}
