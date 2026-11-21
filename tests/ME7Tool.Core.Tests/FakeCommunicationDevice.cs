using ME7Tool.Core.Kwp2000;
using ME7Tool.Core.Transport;

namespace ME7Tool.Core.Tests;

/// <summary>
/// Dispositivo falso em memória para testar a interface KWP2000 sem hardware.
/// Ecoa o que é escrito (como um K-line de fio único) e serve respostas pré-carregadas,
/// uma por pedido.
/// </summary>
public sealed class FakeCommunicationDevice : ICommunicationDevice
{
    private readonly Queue<byte[]> _responses = new();
    private readonly Queue<byte> _rx = new();

    public bool EchoWrites { get; set; } = true;
    public List<byte[]> WrittenFrames { get; } = new();

    public string Name => "Fake";
    public bool IsOpen { get; private set; }

    /// <summary>
    /// Responde automaticamente a cada pedido parseado (devolve o frame de resposta, ou null
    /// para cair na fila pré-carregada).
    /// </summary>
    public Func<Kwp2000Message, byte[]?>? AutoResponder { get; set; }

    /// <summary>Resposta a devolver no próximo pedido (frame completo, já com checksum).</summary>
    public void EnqueueResponse(byte[] frame) => _responses.Enqueue(frame);

    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;
    public List<int> BaudRates { get; } = new();   // trocas de baud pedidas, por ordem
    public void SetBaudRate(int baudRate) => BaudRates.Add(baudRate);
    public void SetBreak(bool enabled) => BreakLog.Add(enabled);
    public int DiscardCount { get; private set; }

    /// <summary>Bytes a pôr no RX logo a seguir a cada purge (ex.: sync 0x55 + key bytes do 5 baud).</summary>
    public Queue<byte[]> RxAfterDiscard { get; } = new();

    /// <summary>Sequência de níveis de break pedidos (true = linha baixa).</summary>
    public List<bool> BreakLog { get; } = new();

    public void DiscardBuffers()
    {
        DiscardCount++;
        _rx.Clear();
        if (RxAfterDiscard.Count > 0) foreach (var b in RxAfterDiscard.Dequeue()) _rx.Enqueue(b);
    }

    /// <summary>Injeta bytes no RX (lixo/resposta atrasada).</summary>
    public void InjectRx(params byte[] bytes) { lock (_rx) foreach (var b in bytes) _rx.Enqueue(b); }   // pode vir de outra thread

    public void Write(ReadOnlySpan<byte> data)
    {
        var frame = data.ToArray();
        WrittenFrames.Add(frame);
        if (EchoWrites)
            foreach (var b in frame) _rx.Enqueue(b);

        byte[]? resp = null;
        if (AutoResponder != null)
        {
            var req = Kwp2000Message.TryParse(frame);
            if (req != null) resp = AutoResponder(req);
        }
        if (resp == null && _responses.Count > 0)
            resp = _responses.Dequeue();
        if (resp != null)
            foreach (var b in resp) _rx.Enqueue(b);
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        int n = 0;
        lock (_rx)
            while (n < buffer.Length && _rx.Count > 0)
                buffer[n++] = _rx.Dequeue();
        return n;
    }

    public void Dispose() { }
}
