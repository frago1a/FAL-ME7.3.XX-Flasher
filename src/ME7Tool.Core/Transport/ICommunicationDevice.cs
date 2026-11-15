namespace ME7Tool.Core.Transport;

/// <summary>
/// Abstração de baixo nível de um dispositivo de comunicação por bytes (K-line/série).
/// Implementações: FTDI (D2XX), porta série, CH340. O backend J2534 (K-line) implementa
/// <see cref="IKLineFrameDevice"/>, porque faz o framing/init no próprio interface.
/// </summary>
public interface ICommunicationDevice : IDisposable
{
    string Name { get; }
    bool IsOpen { get; }

    void Open();
    void Close();

    /// <summary>Define a baud rate (ex.: 10400, 115200, 124800).</summary>
    void SetBaudRate(int baudRate);

    /// <summary>Escreve bytes para a linha.</summary>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>
    /// Lê até <paramref name="buffer"/>.Length bytes, esperando no máximo <paramref name="timeoutMs"/>.
    /// Devolve o número de bytes lidos (pode ser 0 em timeout).
    /// </summary>
    int Read(Span<byte> buffer, int timeoutMs);

    /// <summary>Limpa os buffers de RX/TX.</summary>
    void DiscardBuffers();

    /// <summary>
    /// Controla o estado de break da linha (TX a 0). Usado para o wake-up de fast init
    /// e para o bit-banging do 5-baud init.
    /// </summary>
    void SetBreak(bool enabled);
}

/// <summary>Tipo de inicialização K-line (ISO 14230-2).</summary>
public enum KLineInit
{
    FastInit,
    FiveBaud,
}
