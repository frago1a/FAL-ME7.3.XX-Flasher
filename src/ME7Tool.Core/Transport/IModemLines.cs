namespace ME7Tool.Core.Transport;

/// <summary>
/// Linhas de controlo de um cabo série/FTDI (DTR, RTS). Nos cabos KKL servem para pôr o adaptador no modo
/// K-line; o modo boot (BSL) do C167 mexe nelas antes de entrar. Capacidade opcional: só os devices que a têm.
/// </summary>
public interface IModemLines
{
    void SetDtr(bool on);
    void SetRts(bool on);
}
