using System.Text;
using static ME7Tool.Core.Common.Lang;

namespace ME7Tool.Core.Flashing;

/// <summary>
/// Identificação Bosch/Fiat de uma ECU ME7.3.1: número Bosch (hardware, "0261…"), software ("1037…" ou
/// "1277…"), versão do software (2 bytes) e número de desenho Fiat. Vem do 0x1A da ECU (0x92/0x94/0x95/0x91)
/// ou do bloco de identificação dentro da imagem.
/// </summary>
public sealed record EcuIdent(string Hardware, string Software, byte[]? SoftwareVersion = null, string? FiatDrawing = null)
{
    /// <summary>Versão no formato do Examiner ("0404"), ou "" se não houver.</summary>
    public string VersionHex => SoftwareVersion is { Length: 2 } v ? $"{v[0]:X2}{v[1]:X2}" : "";

    public override string ToString() =>
        $"HW {Hardware} · SW {Software}" + (VersionHex.Length > 0 ? $" v{VersionHex}" : "") +
        (string.IsNullOrEmpty(FiatDrawing) ? "" : $" · Fiat {FiatDrawing}");

    /// <summary>Mesmo hardware (número Bosch), ignorando espaços e maiúsculas.</summary>
    public bool SameHardware(EcuIdent other) => Norm(Hardware) == Norm(other.Hardware);

    public bool SameSoftware(EcuIdent other) => Norm(Software) == Norm(other.Software);

    internal static string Norm(string? s) => (s ?? "").Trim().ToUpperInvariant();

    /// <summary>
    /// Bloco de identificação dentro da imagem desencriptada. Duas disposições conhecidas, ambas com
    /// hardware "0261…" (11) e software "1037…/1277…" (11) seguidos, mais a versão (2) e o desenho Fiat (11):
    /// <list type="bullet">
    /// <item>ME7.3.1 (perto de 0x10190): <c>7C 86 xx xx xx</c> HW sep SW sep versão desenho;</item>
    /// <item>ME3.1 (perto de 0x80C6): desenho HW sep SW versão … <c>7C 86</c> (o 7C86 vem no fim).</item>
    /// </list>
    /// Procura o par HW+SW em qualquer ordem e lê a versão/desenho à volta. null se não encontrar.
    /// </summary>
    public static EcuIdent? FromImage(ReadOnlySpan<byte> image) => FromImage(image, out _);

    /// <summary><paramref name="offset"/> = onde o bloco foi encontrado (−1 se nenhum), para distinguir os perfis.</summary>
    public static EcuIdent? FromImage(ReadOnlySpan<byte> image, out int offset)
    {
        offset = -1;
        int n = Math.Min(image.Length, 0x18000);
        for (int i = 0; i + 24 <= n; i++)
        {
            if (image[i] != (byte)'0' || image[i + 1] != (byte)'2' || image[i + 2] != (byte)'6' || image[i + 3] != (byte)'1') continue;
            if (!IsDigitsOrAlnum(image.Slice(i, 10))) continue; // HW = 0261 + 6
            // SW = 10 dígitos (1037…/1277…), logo a seguir ao HW (0 ou 1 byte separador)
            for (int sw = i + 10; sw <= i + 12 && sw + 10 <= n; sw++)
            {
                if (image[sw] is not ((byte)'1') || !IsDigits(image.Slice(sw, 10))) continue;
                int verAt = sw + 10;
                for (int k = 0; k < 2 && verAt < n && image[verAt] is 0x00 or 0x20; k++) verAt++; // enchimento/separador antes da versão
                byte[]? ver = verAt + 2 <= n && image[verAt] < 0x40 && image[verAt + 1] < 0x40
                    ? image.Slice(verAt, 2).ToArray() : null;
                offset = i;
                return new EcuIdent(Ascii(image.Slice(i, 10)), Ascii(image.Slice(sw, 10)), ver, FindFiatDrawing(image, i, sw));
            }
        }
        return null;
    }

    private static bool IsDigits(ReadOnlySpan<byte> b) { foreach (byte c in b) if (c is < (byte)'0' or > (byte)'9') return false; return true; }
    private static bool IsDigitsOrAlnum(ReadOnlySpan<byte> b) { foreach (byte c in b) if (!char.IsAsciiLetterOrDigit((char)c)) return false; return true; }

    /// <summary>
    /// Desenho Fiat (8 dígitos) como campo delimitado nos 16 bytes antes do HW (ME3.1) ou depois da versão
    /// (ME7.3.1), fora dos campos HW e SW e rodeado por não-dígitos (para não apanhar um pedaço do HW/SW).
    /// </summary>
    private static string? FindFiatDrawing(ReadOnlySpan<byte> image, int hwAt, int swAt)
    {
        int lo = Math.Max(0, hwAt - 16), hi = Math.Min(image.Length, hwAt + 48);
        for (int at = lo; at + 8 <= hi; at++)
        {
            if (Overlaps(at, hwAt) || Overlaps(at, swAt)) continue;
            if (at > 0 && IsDigit(image[at - 1])) continue;                 // começa um campo
            if (at + 8 < image.Length && IsDigit(image[at + 8])) continue;  // e acaba aqui
            if (!IsDigits(image.Slice(at, 8))) continue;
            return Ascii(image.Slice(at, 8));
        }
        return null;

        static bool Overlaps(int at, int field) => at < field + 10 && at + 8 > field;
        static bool IsDigit(byte c) => c is >= (byte)'0' and <= (byte)'9';
    }

    internal static bool IsHardware(string s)
    {
        s = s.Trim();
        return s.Length == 10 && s.StartsWith("0261", StringComparison.Ordinal) && s.All(char.IsAsciiLetterOrDigit);
    }

    internal static bool IsSoftware(string s) { s = s.Trim(); return s.Length == 10 && IsDigits(s); }

    private static bool IsDigits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    internal static string Ascii(ReadOnlySpan<byte> b)
    {
        var sb = new StringBuilder(b.Length);
        foreach (byte c in b) sb.Append(c is >= 0x20 and < 0x7F ? (char)c : ' ');
        return sb.ToString();
    }
}

/// <summary>
/// Um registo do Global.ndb do Examiner: a ECU com o hardware <see cref="Hardware"/> e o software
/// <see cref="FromSoftware"/> passa, com o ficheiro <see cref="File"/>, ao software <see cref="ToSoftware"/>.
/// </summary>
public sealed record ExaminerRecord(string File, string Hardware, string FromSoftware, string FromVersion,
    string ToSoftware, string ToVersion, string FiatDrawing);

/// <summary>Leitura do Global.ndb (base de dados binária do Examiner que liga cada ECU ao ficheiro de flash).</summary>
public static class ExaminerDatabase
{
    /// <summary>
    /// Cada registo: <c>hardware(11) 00 00 00 swOrigem(11) 00 vv vv 00 swDestino(11) 00 vv vv 00 desenhoFiat(11) 00
    /// …(11) 00 NOME.BIN 00</c>. Lê todos os que encontrar (Bosch, Marelli, …); quem usa filtra.
    /// </summary>
    public static List<ExaminerRecord> Parse(ReadOnlySpan<byte> ndb)
    {
        var list = new List<ExaminerRecord>();
        // âncora: o nome do ficheiro "XX000000.BIN\0"; os campos ficam a distâncias fixas antes dele
        const int nameLen = 12, before = 11 + 3 + 11 + 4 + 11 + 4 + 11 + 1 + 11 + 1; // = 68
        for (int n = before; n + nameLen + 1 <= ndb.Length; n++)
        {
            if (ndb[n + 8] != (byte)'.' || ndb[n + 12] != 0 || !IsFileName(ndb.Slice(n, nameLen))) continue;
            int p = n - before;
            if (ndb[p + 11] != 0 || ndb[p + 12] != 0 || ndb[p + 13] != 0 || ndb[p + 25] != 0 || ndb[p + 28] != 0 ||
                ndb[p + 40] != 0 || ndb[p + 43] != 0 || ndb[p + 55] != 0 || ndb[p + 67] != 0) continue;
            string hw = Field(ndb, p), from = Field(ndb, p + 14), to = Field(ndb, p + 29), fiat = Field(ndb, p + 44);
            if (hw.Length == 0 || from.Length == 0 || to.Length == 0) continue;
            list.Add(new ExaminerRecord(Encoding.ASCII.GetString(ndb.Slice(n, nameLen)), hw, from,
                Hex(ndb.Slice(p + 26, 2)), to, Hex(ndb.Slice(p + 41, 2)), fiat));
        }
        return list;
    }

    private static string Field(ReadOnlySpan<byte> b, int at)
    {
        var f = b.Slice(at, 11);
        foreach (byte c in f) if (c is < 0x20 or >= 0x7F) return "";
        return Encoding.ASCII.GetString(f).Trim();
    }

    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b);

    private static bool IsFileName(ReadOnlySpan<byte> s) =>
        s[0] is >= (byte)'A' and <= (byte)'Z' && s[1] is >= (byte)'A' and <= (byte)'Z' &&
        AllDigits(s.Slice(2, 6)) && s[8] == '.' && s[9] == 'B' && s[10] == 'I' && s[11] == 'N';

    private static bool AllDigits(ReadOnlySpan<byte> s) { foreach (byte c in s) if (c is < (byte)'0' or > (byte)'9') return false; return true; }
}

/// <summary>
/// Resultado da pesquisa manual: um ficheiro do Examiner, o hardware a que se destina, o software que grava, o
/// desenho Fiat, os softwares de origem que ele atualiza e o caminho (null se o ficheiro não estiver na pasta).
/// </summary>
public sealed record ExaminerEntry(string File, string Hardware, string Software, string Version, string FiatDrawing,
    IReadOnlyList<string> FromSoftware, string? Path)
{
    public override string ToString() =>
        $"{File}  HW {Hardware}  SW {Software}" + (Version.Length > 0 ? $" v{Version}" : "") +
        (FiatDrawing.Length > 0 ? $"  Fiat {FiatDrawing}" : "") + (Path == null ? T("  (falta na pasta)", "  (missing from folder)") : "");
}

/// <summary>
/// Ficheiro do Examiner que este flasher sabe gravar (ME7.3.1 ou ME3.1), já desencriptado. <see cref="Image"/> é
/// sempre a imagem de 512 KB pronta para o flasher (na ME3.1 o arranque vem em branco, não é gravado).
/// </summary>
public sealed record ExaminerImage(string Path, EcuIdent Ident, byte[] Image, ushort Checksum, FiatFlashProfile Profile)
{
    /// <summary>O payload como está no ficheiro do Examiner (512 KB na ME7.3.1, 496 KB na ME3.1).</summary>
    public byte[] Payload => Image.AsSpan(Profile.PayloadOffsetInImage, Profile.PayloadSize).ToArray();
}

public enum ExaminerMatchKind
{
    /// <summary>A ECU já tem o software mais recente que o Examiner conhece para este hardware.</summary>
    UpToDate,
    /// <summary>O Examiner tem uma atualização para o software que a ECU tem.</summary>
    UpdateAvailable,
    /// <summary>O hardware é conhecido mas o software da ECU não está na lista (outra versão ou modificado).</summary>
    SoftwareNotListed,
    /// <summary>Não há ficheiro para este hardware.</summary>
    NoFile,
}

/// <summary>Resultado de procurar a ECU na pasta do Examiner.</summary>
public sealed record ExaminerMatch(ExaminerMatchKind Kind, ExaminerRecord? Record, ExaminerImage? Image, string Message);

/// <summary>
/// Pasta do Examiner (ex.: "Examiner Flesh REL 5.60"): o Global.ndb na raiz e os ficheiros em XX/aa/bb/XXaabbcc.BIN
/// + .Prm. Procura a ECU pela identificação (não pela pasta — a BO tem ficheiros Bosch de várias ECUs) e só aceita
/// ficheiros ME7.3.1 que este flasher grava: 512 KB, .Prm com a zona 0x800000–0x87FFFF, e o CRC do .Prm igual ao
/// da imagem desencriptada (prova que a chave e o ficheiro estão certos).
/// </summary>
public sealed class ExaminerLibrary
{
    public const int ImageSize = 0x80000;

    private readonly Dictionary<string, string> _files; // nome (maiúsculas) → caminho

    public string Folder { get; }
    public IReadOnlyList<ExaminerRecord> Records { get; }
    public bool HasDatabase { get; }

    private ExaminerLibrary(string folder, List<ExaminerRecord> records, bool hasDb, Dictionary<string, string> files)
    {
        Folder = folder; Records = records; HasDatabase = hasDb; _files = files;
    }

    /// <summary>Lê o Global.ndb (se houver) e faz o índice dos .BIN da pasta e subpastas.</summary>
    public static ExaminerLibrary Load(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(folder, "*.bin", SearchOption.AllDirectories))
            files.TryAdd(System.IO.Path.GetFileName(f), f);
        string? ndb = Directory.EnumerateFiles(folder, "*.ndb", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => System.IO.Path.GetFileName(f).Equals("Global.ndb", StringComparison.OrdinalIgnoreCase));
        var records = ndb != null ? ExaminerDatabase.Parse(File.ReadAllBytes(ndb)) : new List<ExaminerRecord>();
        return new ExaminerLibrary(folder, records, ndb != null, files);
    }

    /// <summary>Caminho de um ficheiro pelo nome (ex.: "BO010109.BIN"), ou null se não estiver na pasta.</summary>
    public string? FindFile(string name) => _files.TryGetValue(name, out var p) ? p : null;

    /// <summary>
    /// Abre um .BIN do Examiner e devolve a imagem (512 KB, pronta para o flasher) se a souber gravar; senão
    /// null e o motivo. Reconhece ME7.3.1 (512 KB) e ME3.1 (496 KB, só 0x4000–0x7FFFF). Não precisa de ECU.
    /// </summary>
    public static ExaminerImage? OpenImage(string path, out string? reason)
    {
        reason = null;
        var info = new FileInfo(path);
        if (!info.Exists) { reason = T("o ficheiro não existe", "the file does not exist"); return null; }
        var prof = FiatFlashProfile.All.FirstOrDefault(p => p.PayloadSize == info.Length);
        if (prof == null) { reason = T($"tem {info.Length} bytes (não é 512 KB ME7.3.1 nem 496 KB ME3.1)", $"it has {info.Length} bytes (not a 512 KB ME7.3.1 nor a 496 KB ME3.1)"); return null; }

        string prm = System.IO.Path.ChangeExtension(path, ".Prm");
        prm = File.Exists(prm) ? prm : Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(path)!)
            .FirstOrDefault(f => System.IO.Path.GetFileNameWithoutExtension(f).Equals(System.IO.Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)
                              && System.IO.Path.GetExtension(f).Equals(".prm", StringComparison.OrdinalIgnoreCase)) ?? "";
        if (prm.Length == 0) { reason = T("falta o .Prm ao lado (não dá para confirmar o ficheiro)", "the .Prm next to it is missing (cannot confirm the file)"); return null; }
        var p = File.ReadAllBytes(prm);
        if (!PrmMatches(p, prof, out ushort expected)) { reason = T($"o .Prm não é de um {prof.Name} (outra zona de flash)", $"the .Prm is not a {prof.Name} (another flash area)"); return null; }

        // imagem de 512 KB para o flasher: ME7.3.1 = o ficheiro; ME3.1 = payload em 0x4000, arranque em branco
        var payload = FiatCipher.DecryptAll(File.ReadAllBytes(path));
        var image = new byte[FiatFlashProfile.FullImageSize];
        if (prof.PayloadOffsetInImage > 0) Array.Fill(image, (byte)0xFF, 0, prof.PayloadOffsetInImage);
        payload.CopyTo(image, prof.PayloadOffsetInImage);

        ushort cs = prof.ChecksumOf(prof.Prepare(image));
        if (cs != expected) { reason = T($"o CRC não bate com o .Prm (0x{cs:X4} ≠ 0x{expected:X4}): outra chave ou ficheiro alterado", $"the CRC does not match the .Prm (0x{cs:X4} ≠ 0x{expected:X4}): another key or a modified file"); return null; }
        var ident = EcuIdent.FromImage(image);
        if (ident == null) { reason = T("sem bloco de identificação", "no identification block"); return null; }
        // na ME7.3.1 o programador está no ficheiro; na ME3.1 está no arranque, fora do ficheiro (chave partilhada)
        if (prof.HasBackupZone && FiatFullFlash.FindKey(image) == null) { reason = T("não tem o programador da Fiat (este flasher não o grava)", "it has no Fiat programmer (this flasher cannot write it)"); return null; }
        return new ExaminerImage(path, ident, image, cs, prof);
    }

    /// <summary>
    /// O .Prm do Examiner corresponde ao perfil: o 31 01/02 na zona do perfil (offset 0x22) e o download do
    /// tamanho do payload (offset 0x2C); o checksum (2 bytes, big-endian) em 0x28.
    /// ME7.3.1: 80 00 00 – 87 FF FF, 0x080000. ME3.1: 00 40 00 – 07 FF FF, 0x07C000.
    /// </summary>
    internal static bool PrmMatches(ReadOnlySpan<byte> prm, FiatFlashProfile prof, out ushort checksum)
    {
        checksum = 0;
        if (prm.Length < 0x30) return false;
        var start = prm.Slice(0x22, 3); var end = prm.Slice(0x25, 3); var size = prm.Slice(0x2C, 4);
        bool range = Addr3Eq(start, prof.EraseStart) && Addr3Eq(end, prof.EraseEnd);
        bool sz = size[0] == 0 && (size[1] << 16 | size[2] << 8 | size[3]) == prof.PayloadSize; // big-endian em 0x2D..0x2F
        checksum = (ushort)(prm[0x28] << 8 | prm[0x29]);
        return range && sz;
    }

    private static bool Addr3Eq(ReadOnlySpan<byte> b, uint a) => b[0] == (byte)(a >> 16) && b[1] == (byte)(a >> 8) && b[2] == (byte)a;

    /// <summary>
    /// Procura a ECU: primeiro no Global.ndb (hardware + software de origem/destino), depois, sem base de dados ou
    /// sem registo, nos próprios ficheiros (identificação dentro de cada imagem com o mesmo hardware).
    /// </summary>
    public ExaminerMatch Match(EcuIdent ecu)
    {
        string hw = EcuIdent.Norm(ecu.Hardware), sw = EcuIdent.Norm(ecu.Software);
        var recs = Records.Where(r => EcuIdent.Norm(r.Hardware) == hw).ToList();

        if (recs.Count > 0)
        {
            var upToDate = recs.FirstOrDefault(r => EcuIdent.Norm(r.ToSoftware) == sw);
            var update = recs.FirstOrDefault(r => EcuIdent.Norm(r.FromSoftware) == sw);
            var latest = recs.OrderByDescending(r => r.ToSoftware, StringComparer.Ordinal).First();
            var (kind, rec) = upToDate != null ? (ExaminerMatchKind.UpToDate, upToDate)
                            : update != null ? (ExaminerMatchKind.UpdateAvailable, update)
                            : (ExaminerMatchKind.SoftwareNotListed, latest);
            string? path = FindFile(rec.File);
            ExaminerImage? img = null; string? why = null;
            if (path != null) img = OpenImage(path, out why);
            else why = T($"o {rec.File} não está nesta pasta", $"{rec.File} is not in this folder");
            if (img != null && !img.Ident.SameHardware(ecu))
            { why = T($"o {rec.File} diz HW {img.Ident.Hardware}, não {ecu.Hardware}", $"{rec.File} says HW {img.Ident.Hardware}, not {ecu.Hardware}"); img = null; }
            return new ExaminerMatch(kind, rec, img, Describe(kind, ecu, rec.ToSoftware, rec.File, img, why));
        }

        // sem registo: procura nos ficheiros ME7.3.1 da pasta o mesmo hardware
        ExaminerImage? best = null;
        foreach (var path in _files.Values)
        {
            if (!FiatFlashProfile.All.Any(fp => fp.PayloadSize == new FileInfo(path).Length)) continue;
            var img = OpenImage(path, out _);
            if (img == null || !img.Ident.SameHardware(ecu)) continue;
            if (best == null || string.CompareOrdinal(img.Ident.Software, best.Ident.Software) > 0) best = img;
        }
        if (best == null)
            return new ExaminerMatch(ExaminerMatchKind.NoFile, null, null,
                T($"Não há ficheiro do Examiner para o hardware {ecu.Hardware} nesta pasta.", $"There is no Examiner file for hardware {ecu.Hardware} in this folder."));
        var k = best.Ident.SameSoftware(ecu) ? ExaminerMatchKind.UpToDate : ExaminerMatchKind.SoftwareNotListed;
        string file = System.IO.Path.GetFileName(best.Path);
        return new ExaminerMatch(k, null, best, Describe(k, ecu, best.Ident.Software, file, best, null));
    }

    /// <summary>
    /// Pesquisa manual por referência: nº Bosch (hardware), software (destino ou origem), desenho Fiat ou nome do
    /// ficheiro — basta parte do número; espaços ignorados. Um resultado por ficheiro. Com Global.ndb procura nos
    /// registos (todos os fornecedores; a gravação só aceita os ME7.3.1 — ver <see cref="OpenImage"/>); sem ele, na
    /// identificação dentro dos ficheiros ME7.3.1 da pasta.
    /// </summary>
    public List<ExaminerEntry> Search(string query)
    {
        string q = new string((query ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        if (q.Length == 0) return new();
        bool Hit(string? s) => !string.IsNullOrEmpty(s) && EcuIdent.Norm(s).Replace(" ", "").Contains(q, StringComparison.Ordinal);

        if (HasDatabase)
        {
            return Records.GroupBy(r => r.File, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Any(r => Hit(r.Hardware) || Hit(r.ToSoftware) || Hit(r.FromSoftware) || Hit(r.FiatDrawing) || Hit(r.File)))
                .Select(g =>
                {
                    var r = g.First();
                    return new ExaminerEntry(r.File, r.Hardware, r.ToSoftware, r.ToVersion, r.FiatDrawing,
                        g.Select(x => x.FromSoftware).Distinct().ToList(), FindFile(r.File));
                })
                .OrderBy(e => e.Hardware, StringComparer.Ordinal).ThenBy(e => e.File, StringComparer.Ordinal)
                .ToList();
        }

        var list = new List<ExaminerEntry>();
        foreach (var path in _files.Values.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (!FiatFlashProfile.All.Any(fp => fp.PayloadSize == new FileInfo(path).Length)) continue;
            var img = OpenImage(path, out _);
            if (img == null) continue;
            string file = System.IO.Path.GetFileName(path);
            var id = img.Ident;
            if (Hit(id.Hardware) || Hit(id.Software) || Hit(id.FiatDrawing) || Hit(file))
                list.Add(new ExaminerEntry(file, id.Hardware, id.Software, id.VersionHex, id.FiatDrawing ?? "", Array.Empty<string>(), path));
        }
        return list;
    }

    private static string Describe(ExaminerMatchKind kind, EcuIdent ecu, string target, string file, ExaminerImage? img, string? why)
    {
        string s = kind switch
        {
            ExaminerMatchKind.UpToDate => T($"✔ Software atual: a ECU já tem o {ecu.Software}, o mais recente do Examiner para este hardware ({file}).",
                                            $"✔ Software up to date: the ECU already has {ecu.Software}, the latest the Examiner has for this hardware ({file})."),
            ExaminerMatchKind.UpdateAvailable => T($"⬆ Há atualização: {ecu.Software} → {target} ({file}).",
                                                   $"⬆ Update available: {ecu.Software} → {target} ({file})."),
            _ => T($"? O software {ecu.Software} não está na lista do Examiner para este hardware (outra versão ou modificado). O original mais recente é o {target} ({file}).",
                   $"? Software {ecu.Software} is not in the Examiner's list for this hardware (another version or modified). The latest original is {target} ({file})."),
        };
        return img != null ? s : s + T($" Ficheiro não utilizável: {why}.", $" File not usable: {why}.");
    }
}
