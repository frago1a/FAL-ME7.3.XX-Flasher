namespace ME7Tool.Core.Kwp2000;

/// <summary>
/// Codificação do byte de baud rate do StartDiagnosticSession (0x10), conforme a
/// spec Fiat KWP2000 / ISO 14230: byte = XXXYYYYY, baud = (2^X · (Y+32) · 6400) / 32,
/// com X = 0..7 e Y = 0..31. A ECU responde a 10400 e só depois ambos trocam de baud.
/// </summary>
public static class KwpBaud
{
    /// <summary>Descodifica o byte de baud para a taxa real.</summary>
    public static uint FromByte(byte baudByte)
    {
        int x = (baudByte >> 5) & 0x7;
        int y = baudByte & 0x1F;
        return (uint)(((1 << x) * (y + 32) * 6400) / 32);
    }

    /// <summary>
    /// Codifica uma taxa de baud no byte XXXYYYYY mais próximo (escolhe o expoente que dá
    /// o Y em [0,31] com menor resto, como o NefMoto).
    /// </summary>
    public static byte ToByte(uint baudRate)
    {
        uint baseValue = baudRate * 32 / 6400; // = 2^X · (Y+32)
        uint bestExpResult = 1;
        uint bestExp = 0;
        double bestDist = 64.0;

        for (int exp = 7; exp >= 0; exp--)
        {
            uint expResult = (uint)1 << exp;
            if (expResult >= baseValue) continue;

            double scalar = (double)baseValue / expResult;
            if (scalar is < 64 and > 32)
            {
                double dist = scalar - Math.Floor(scalar);
                if (dist < bestDist) { bestDist = dist; bestExp = (uint)exp; bestExpResult = expResult; }
            }
        }

        uint z = (baseValue / bestExpResult) - 32;
        return (byte)(((bestExp & 0x7) << 5) | (z & 0x1F));
    }
}
