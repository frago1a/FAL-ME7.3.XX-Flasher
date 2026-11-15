namespace ME7Tool.Core.Kwp2000;

/// <summary>
/// Mensagem KWP2000 (ISO 14230-2). Frame:
///   [Fmt] [Tgt?] [Src?] [Len?] [Data...] [Checksum]
/// - Fmt: 2 bits altos = modo de endereçamento; 6 bits baixos = length (0 = usar byte Len).
/// - Tgt/Src presentes se o modo de endereçamento != None.
/// - Byte Len separado se o length embutido no Fmt for 0.
/// - Checksum = soma de todos os bytes anteriores (mod 256).
/// Data inclui o ServiceId como primeiro byte.
/// </summary>
public sealed class Kwp2000Message
{
    public Kwp2000AddressMode AddressMode { get; set; } = Kwp2000AddressMode.Physical;
    public byte Target { get; set; }
    public byte Source { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>Primeiro byte dos dados (serviço ou resposta).</summary>
    public byte ServiceId => Data.Length > 0 ? Data[0] : (byte)0;

    public bool IsNegativeResponse => Data.Length >= 1 && Data[0] == (byte)Kwp2000Service.NegativeResponse;

    /// <summary>Em resposta negativa (7F sid nrc): o serviço original (Data[1]).</summary>
    public byte? NegativeRequestServiceId => IsNegativeResponse && Data.Length >= 2 ? Data[1] : null;

    /// <summary>Em resposta negativa: o código NRC (Data[2]).</summary>
    public Kwp2000ResponseCode? NegativeResponseCode
        => IsNegativeResponse && Data.Length >= 3 ? (Kwp2000ResponseCode)Data[2] : null;

    /// <summary>True se for a resposta positiva (sid + 0x40) ao serviço dado.</summary>
    public bool IsPositiveResponseTo(Kwp2000Service service)
        => Data.Length >= 1 && Data[0] == (byte)((byte)service + 0x40);

    public Kwp2000Message() { }

    public Kwp2000Message(Kwp2000AddressMode mode, byte target, byte source, params byte[] data)
    {
        AddressMode = mode;
        Target = target;
        Source = source;
        Data = data;
    }

    /// <summary>Constrói uma mensagem de serviço (ServiceId + parâmetros).</summary>
    public static Kwp2000Message Request(byte target, byte source, Kwp2000Service service, params byte[] parameters)
    {
        var data = new byte[1 + parameters.Length];
        data[0] = (byte)service;
        Array.Copy(parameters, 0, data, 1, parameters.Length);
        return new Kwp2000Message(Kwp2000AddressMode.Physical, target, source, data);
    }

    /// <summary>
    /// Codifica o frame completo (com checksum).
    /// </summary>
    /// <param name="useSeparateLengthByte">
    /// true = sempre usar byte Len separado (header com additional length byte);
    /// false = embutir no Fmt quando length &lt;= 63.
    /// </param>
    public byte[] Encode(bool useSeparateLengthByte = false)
    {
        bool hasAddress = AddressMode != Kwp2000AddressMode.None;
        int len = Data.Length;
        bool separateLen = useSeparateLengthByte || len > 0x3F;

        var buf = new List<byte>(len + 5);

        byte fmt = (byte)((byte)AddressMode | (separateLen ? 0 : (byte)len));
        buf.Add(fmt);

        if (hasAddress)
        {
            buf.Add(Target);
            buf.Add(Source);
        }
        if (separateLen)
            buf.Add((byte)len);

        buf.AddRange(Data);

        byte checksum = 0;
        foreach (var b in buf) checksum += b;
        buf.Add(checksum);

        return buf.ToArray();
    }

    /// <summary>
    /// Descodifica um frame completo. Devolve null se o checksum falhar ou o frame for inválido.
    /// </summary>
    public static Kwp2000Message? TryParse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 3) return null;

        // valida checksum
        byte sum = 0;
        for (int i = 0; i < frame.Length - 1; i++) sum += frame[i];
        if (sum != frame[^1]) return null;

        int pos = 0;
        byte fmt = frame[pos++];
        var mode = (Kwp2000AddressMode)(fmt & 0xC0);
        int lenInFmt = fmt & 0x3F;

        byte target = 0, source = 0;
        if (mode != Kwp2000AddressMode.None)
        {
            if (frame.Length < pos + 2) return null;
            target = frame[pos++];
            source = frame[pos++];
        }

        int len = lenInFmt;
        if (len == 0)
        {
            if (frame.Length < pos + 1) return null;
            len = frame[pos++];
        }

        if (frame.Length < pos + len + 1) return null; // +1 checksum
        var data = frame.Slice(pos, len).ToArray();

        return new Kwp2000Message(mode, target, source, data);
    }

    public override string ToString()
        => $"{AddressMode} tgt={Target:X2} src={Source:X2} data={Convert.ToHexString(Data)}";
}
