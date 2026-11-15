namespace ME7Tool.Core.Transport;

/// <summary>
/// K-line num interface que faz o init e o framing sozinho (J2534 ISO14230, ex.: Scanmatik 2).
/// Continua a ser um <see cref="ICommunicationDevice"/> de bytes para o resto do KWP2000 (Write/Read
/// recebem e devolvem frames completas com checksum, sem eco), mas o init (fast-init / 5 baud) é pedido
/// ao interface em vez de feito por break. <see cref="ICommunicationDevice.SetBreak"/> não é suportado.
/// </summary>
public interface IKLineFrameDevice : ICommunicationDevice
{
    /// <summary>Fast-init + StartCommunication. <paramref name="request"/> é a frame completa (com checksum);
    /// devolve a resposta da ECU, também com checksum.</summary>
    byte[] FastInit(byte[] request);

    /// <summary>Slow init (5 baud) ao endereço; devolve os 2 key bytes.</summary>
    byte[] FiveBaudInit(byte address);

    /// <summary>Tempos que o interface aplica (ms): interbyte da ECU, pausa antes do pedido, interbyte do tester.</summary>
    void SetTiming(int p1MaxMs, int p3MinMs, int p4MinMs);
}
