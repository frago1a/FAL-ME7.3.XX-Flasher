namespace ME7Tool.Core.Kwp2000;

/// <summary>
/// Parâmetros de timing K-line (ISO 14230-2) + baud. Valores em milissegundos.
/// Sugestões com base na spec Fiat 07223 §4.3 e nos limites da sessão download §4.4.3.3.
/// </summary>
public sealed class KwpTiming
{
    public int BaudRate { get; set; } = 10400;

    /// <summary>P1: interbyte da ECU (máx a tolerar entre bytes da resposta).</summary>
    public int P1Max { get; set; } = 20;

    /// <summary>P2: tempo entre pedido e resposta (mín/máx a esperar).</summary>
    public int P2Min { get; set; } = 25;
    public int P2Max { get; set; } = 50;

    /// <summary>P3: tempo entre resposta e novo pedido.</summary>
    public int P3Min { get; set; } = 55;
    public int P3Max { get; set; } = 5000;

    /// <summary>P4: interbyte do tester (entre bytes que enviamos).</summary>
    public int P4Min { get; set; } = 5;

    /// <summary>Timeout total à espera do início da resposta.</summary>
    public int ResponseTimeoutMs { get; set; } = 1000;

    /// <summary>Timeout entre bytes consecutivos da resposta (deteta fim de frame).</summary>
    public int InterByteTimeoutMs { get; set; } = 40;

    /// <summary>Intervalo do keepalive TesterPresent (menor que P3Max).</summary>
    public int TesterPresentIntervalMs { get; set; } = 2000;

    /// <summary>Perfil normal (diagnóstico). Spec 07223 §4.3.</summary>
    public static KwpTiming Normal() => new();

    /// <summary>
    /// Perfil da sessão de download/programação (0x85). Spec 07223 §4.4.3.3:
    /// P1 0–10, P2 15–50, P3 0.5–5000, P4 0–20. P1=0 permitido em reprogramação.
    /// </summary>
    public static KwpTiming Download() => new()
    {
        // 5 ms: no J2534 o interface espera P1max de linha calada após cada resposta antes do bloco seguinte
        // (com 10 ms o flash por Scanmatik demorava +22 s). 5 ms é o mínimo do Scanmatik 2; FTDI não usa isto.
        P1Max = 5,
        P2Min = 15, P2Max = 50,
        P3Min = 1, P3Max = 5000,
        P4Min = 0,
    };

    /// <summary>Perfil agressivo para logging rápido (baud alto, interbyte mínimo).</summary>
    public static KwpTiming FastLog(int baud = 10400) => new()
    {
        BaudRate = baud,
        P1Max = 20,
        P2Min = 0, P2Max = 100,
        P3Min = 0, P3Max = 5000,
        P4Min = 0,
        InterByteTimeoutMs = 25,
    };

    public KwpTiming Clone() => (KwpTiming)MemberwiseClone();
}
