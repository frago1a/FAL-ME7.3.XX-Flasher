using System.Text.RegularExpressions;

namespace ME7Tool.Core.Kwp2000;

/// <summary>
/// Lista de descrições de DTC (código → texto), lida de um ficheiro de texto simples, uma por linha:
/// <code>
/// P0335 Crankshaft position sensor circuit
/// P0500 – Vehicle Speed Sensor (VSS) Circuit
/// P0130;O2 sensor circuit        (também aceita ; , : TAB ou - como separador)
/// # comentário
/// </code>
/// Linhas sem código válido são ignoradas. Se um código aparecer repetido, fica a primeira descrição.
/// </summary>
public sealed partial class DtcDescriptions
{
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _map.Count;

    /// <summary>Linhas ignoradas na última leitura (sem código reconhecível).</summary>
    public int SkippedLines { get; private set; }

    /// <summary>Lista vazia (as descrições são um bónus — sem ficheiro mostra-se só o código).</summary>
    public static DtcDescriptions Empty { get; } = new();

    [GeneratedRegex(@"^\s*([PCBU][0-9A-F]{4})\b[\s\-–—:;,\t]*(.*?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LineRegex();

    public static DtcDescriptions Parse(TextReader reader)
    {
        var d = new DtcDescriptions();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            var m = LineRegex().Match(line);
            if (!m.Success || m.Groups[2].Length == 0) { d.SkippedLines++; continue; }
            d._map.TryAdd(m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value);
        }
        return d;
    }

    public static DtcDescriptions Parse(string text) => Parse(new StringReader(text));

    public static DtcDescriptions Load(string path)
    {
        using var r = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        return Parse(r);
    }

    /// <summary>Descrição do código (ex.: "P0335"), ou null se não estiver na lista.</summary>
    public string? Find(string displayCode) => _map.TryGetValue(displayCode, out var s) ? s : null;

    public string? Find(DtcRecord dtc) => Find(dtc.DisplayCode);
}
