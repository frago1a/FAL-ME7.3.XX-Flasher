namespace ME7Tool.Core.Logging;

/// <summary>
/// Canal de diagnóstico que o logger usa, abstraindo o dialeto (KWP2000 ou UDS) e o transporte
/// (K-line ou CAN/ISO-TP). Assim o mesmo logger funciona em qualquer ECU (R5).
/// </summary>
public interface ILogChannel
{
    /// <summary>Lê <paramref name="size"/> bytes de RAM no endereço dado (0x23).</summary>
    byte[] ReadMemoryByAddress(uint address, int size);

    /// <summary>Lê um identificador local/DID (KWP 0x21 / UDS 0x22).</summary>
    byte[] ReadDataByLocalIdentifier(byte localId);

    /// <summary>
    /// Envia um serviço com parâmetros e devolve os dados da resposta (após o byte de resposta).
    /// Lança em resposta negativa. Usado p.ex. para o define 0x2C.
    /// </summary>
    byte[] SendRaw(byte service, params byte[] parameters);
}
