using static ME7Tool.Core.Common.Lang;
namespace ME7Tool.Core.Kwp2000;

/// <summary>Erro genérico da camada KWP2000.</summary>
public class Kwp2000Exception : Exception
{
    public Kwp2000Exception(string message) : base(message) { }
}

/// <summary>Timeout à espera de resposta da ECU.</summary>
public sealed class Kwp2000TimeoutException : Kwp2000Exception
{
    public Kwp2000TimeoutException(string message) : base(message) { }
}

/// <summary>A ECU respondeu com resposta negativa (7F sid nrc).</summary>
public sealed class Kwp2000NegativeResponseException : Kwp2000Exception
{
    public byte RequestServiceId { get; }
    public Kwp2000ResponseCode ResponseCode { get; }

    public Kwp2000NegativeResponseException(byte requestServiceId, Kwp2000ResponseCode code)
        : base(T($"Resposta negativa ao serviço 0x{requestServiceId:X2}: {code} (0x{(byte)code:X2})", $"Negative response to service 0x{requestServiceId:X2}: {code} (0x{(byte)code:X2})"))
    {
        RequestServiceId = requestServiceId;
        ResponseCode = code;
    }
}
