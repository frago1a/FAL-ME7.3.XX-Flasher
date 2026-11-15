using static ME7Tool.Core.Common.Lang;
namespace ME7Tool.Core.Kwp2000;

/// <summary>
/// Identificadores de serviço KWP2000 (ISO 14230-3) / UDS (ISO 14229).
/// A resposta positiva = ServiceId + 0x40. Confirmados contra firmware ME7.3.1,
/// spec Fiat 07223 e NefMoto (KWP2000Constants.cs).
/// </summary>
public enum Kwp2000Service : byte
{
    StartDiagnosticSession = 0x10,
    StopDiagnosticSession = 0x20,
    ClearDiagnosticInformation = 0x14,
    ReadDiagnosticTroubleCodesByStatus = 0x18,
    ReadStatusOfDTC = 0x17,
    ReadECUIdentification = 0x1A,
    ReadDataByLocalIdentifier = 0x21,
    ReadDataByCommonIdentifier = 0x22,
    ReadMemoryByAddress = 0x23,
    SecurityAccess = 0x27,
    DynamicallyDefineLocalIdentifier = 0x2C,
    WriteDataByCommonIdentifier = 0x2E,
    InputOutputControlByLocalIdentifier = 0x30,
    StartRoutineByLocalIdentifier = 0x31,
    StopRoutineByLocalIdentifier = 0x32,
    RequestRoutineResultsByLocalIdentifier = 0x33,
    RequestDownload = 0x34,
    RequestUpload = 0x35,
    TransferData = 0x36,
    RequestTransferExit = 0x37,
    WriteDataByLocalIdentifier = 0x3B,
    WriteMemoryByAddress = 0x3D,
    TesterPresent = 0x3E,

    // Camada de comunicação (ISO 14230-2)
    StartCommunication = 0x81,
    StopCommunication = 0x82,
    AccessTimingParameters = 0x83,

    NegativeResponse = 0x7F,
}

/// <summary>
/// Modos de sessão de diagnóstico (sub-função do serviço 0x10).
/// A ME7.3.1 Fiat só aceita 0x85; a VAG usa 0x86 (development). Ambos listados.
/// </summary>
public enum Kwp2000SessionType : byte
{
    Standard = 0x81,
    EndOfLineFiat = 0x83,
    EndOfLineSystemSupplier = 0x84,
    /// <summary>ECUProgrammingMode — flash + ReadMemoryByAddress sem seed (ME7.3.1 Fiat).</summary>
    Programming = 0x85,
    /// <summary>Development — logging/RAM read (VAG ME7.5).</summary>
    Development = 0x86,
    Adjustment = 0x87,
    DedicatedForComponentStarting = 0x89,
}

/// <summary>
/// Modo de endereçamento (bits altos do format byte, ISO 14230-2).
/// </summary>
public enum Kwp2000AddressMode : byte
{
    None = 0x00,
    CARB = 0x40,
    Physical = 0x80,
    Functional = 0xC0,
}

/// <summary>
/// Códigos de resposta negativa (NRC). Mensagem: 7F &lt;sid&gt; &lt;nrc&gt;.
/// </summary>
public enum Kwp2000ResponseCode : byte
{
    GeneralReject = 0x10,
    ServiceNotSupported = 0x11,
    SubFunctionNotSupportedOrInvalidFormat = 0x12,
    BusyRepeatRequest = 0x21,
    ConditionsNotCorrectOrRequestSequenceError = 0x22,
    RoutineNotCompleteOrServiceInProgress = 0x23,
    RequestOutOfRange = 0x31,
    SecurityAccessDenied = 0x33,
    InvalidKey = 0x35,
    ExceedNumberOfAttempts = 0x36,
    RequiredTimeDelayNotExpired = 0x37,
    DownloadNotAccepted = 0x40,
    UploadNotAccepted = 0x50,
    TransferSuspended = 0x71,
    BlockTransferDataChecksumError = 0x77,
    /// <summary>responsePending: aguardar nova resposta, resetar P3.</summary>
    ResponsePending = 0x78,
    ServiceNotSupportedInActiveSession = 0x7F,
}

/// <summary>
/// Um código de avaria (DTC) + o seu estado. Formato Fiat 07223 §8.2.2.4/§8.2.2.5:
/// o código tem 2 bytes (bits 15–14 = grupo P/C/B/U, resto = 4 dígitos, como no SAE J2012);
/// o statusOfDTC é <c>W BA T SSSS</c> (W = luz de avaria, BA = estado de memorização,
/// T = teste concluído, SSSS = sintoma).
/// </summary>
public readonly record struct DtcRecord(ushort Code, byte Status)
{
    /// <summary>Código no formato de oficina, ex.: 0x0335 → "P0335", 0x4123 → "C0123".</summary>
    public string DisplayCode => $"{"PCBU"[Code >> 14]}{Code & 0x3FFF:X4}";

    /// <summary>Bit 7: a luz de avaria (MIL) está acesa por causa deste DTC.</summary>
    public bool WarningLamp => (Status & 0x80) != 0;

    /// <summary>Bits 6–5: 0 = não detetado, 1 = memorizado mas não presente agora,
    /// 2 = intermitente / a amadurecer, 3 = presente agora (e memorizado).</summary>
    public int StorageState => (Status >> 5) & 0x03;

    /// <summary>Bit 4: o teste deste DTC está concluído (ou não se aplica).</summary>
    public bool TestComplete => (Status & 0x10) != 0;

    /// <summary>Bits 3–0: sintoma da avaria.</summary>
    public int Symptom => Status & 0x0F;

    public string StorageText => StorageState switch
    {
        0 => T("não detetado", "not detected"),
        1 => T("memorizado (não presente agora)", "stored (not present now)"),
        2 => T("intermitente", "intermittent"),
        _ => T("presente", "present"),
    };

    public string SymptomText => Symptom switch
    {
        0x0 => "",
        0x1 => T("acima do máximo", "above maximum"),
        0x2 => T("abaixo do mínimo", "below minimum"),
        0x4 => T("sem sinal", "no signal"),
        0x8 => T("sinal inválido", "invalid signal"),
        var s => T($"sintoma 0x{s:X}", $"symptom 0x{s:X}"),
    };

    /// <summary>Descrição legível do estado, ex.: "presente · MIL acesa · sem sinal".</summary>
    public string StatusText
    {
        get
        {
            var parts = new List<string> { StorageText };
            if (WarningLamp) parts.Add(T("MIL acesa", "MIL on"));
            if (SymptomText.Length > 0) parts.Add(SymptomText);
            if (!TestComplete) parts.Add(T("teste não concluído", "test not complete"));
            return string.Join(" · ", parts);
        }
    }

    public override string ToString() => $"{DisplayCode} — {StatusText} (0x{Code:X4}, status 0x{Status:X2})";
}

/// <summary>
/// IdentificationOption para ReadECUIdentification (0x1A). Spec Fiat 07223 §6.6.2.1.
/// </summary>
public enum EcuIdentificationOption : byte
{
    IdentificationCodeAll = 0x80,  // devolve 0x91..0x99
    FiatDrawingNumber = 0x91,      // 11 ASCII
    EcuHardwareNumber = 0x92,      // 11 ASCII
    EcuHardwareVersion = 0x93,     // 1  unsigned
    EcuSoftwareNumber = 0x94,      // 11 ASCII
    EcuSoftwareVersion = 0x95,     // 2  unsigned
    OmologationCode = 0x96,        // 6  ASCII
    IsoCode = 0x97,                // 5
    TesterCode = 0x98,             // 10 ASCII
    ProgrammingDate = 0x99,        // 4  BCD (Y-Y-M-D)
}
