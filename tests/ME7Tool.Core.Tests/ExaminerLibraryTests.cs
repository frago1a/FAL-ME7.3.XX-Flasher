using System.Text;
using ME7Tool.Core.Flashing;

namespace ME7Tool.Core.Tests;

public class ExaminerLibraryTests
{
    // ---- dados sintéticos com o formato real (BO010109 da Thesis / BO010101 da Alfa 147) ----

    private static byte[] F(string s) => Encoding.ASCII.GetBytes(s.PadRight(11));

    /// <summary>Registo do Global.ndb: hw 000000 from 00 vv 00 to 00 vv 00 fiat 00 x(11) 00 NOME 00.</summary>
    private static byte[] Record(string hw, string from, byte[] fromVer, string to, byte[] toVer, string fiat, string file)
    {
        var b = new List<byte>();
        b.AddRange(F(hw)); b.AddRange(new byte[3]);
        b.AddRange(F(from)); b.Add(0); b.AddRange(fromVer); b.Add(0);
        b.AddRange(F(to)); b.Add(0); b.AddRange(toVer); b.Add(0);
        b.AddRange(F(fiat)); b.Add(0);
        b.AddRange(new byte[] { 0x06, 0x00, 0x20, 0x19, 0x00, 0x00, 0x7C, 0x86, 0xBC, 0x02, 0x15 }); b.Add(0);
        b.AddRange(Encoding.ASCII.GetBytes(file)); b.Add(0);
        b.AddRange(new byte[] { 0x14, 0x03, 0x07, 0x18, 0x00, 0x06, 0x00 });
        return b.ToArray();
    }

    /// <summary>Imagem de 512 KB com o bloco de identificação e o padrão do programador (para o FindKey).</summary>
    private static byte[] Image(string hw, string sw, byte[] ver, string fiat, bool spaceSeparators = false)
    {
        var img = new byte[ExaminerLibrary.ImageSize];
        Array.Fill(img, (byte)0xFF);
        byte sep = spaceSeparators ? (byte)' ' : (byte)0;
        var id = new List<byte> { 0x7C, 0x86, 0xBC, 0x02, 0x15 };
        id.AddRange(F(hw)); id.Add(sep); id.AddRange(F(sw)); id.Add(sep); id.AddRange(ver); id.AddRange(F(fiat));
        id.CopyTo(img, 0x101D7);
        // programador: mov r12,#0x8100; movbz r13,0x8200; calls 38h → chave em 0x8048+0x100, comprimento em 0x8048+0x200
        new byte[] { 0xE6, 0xFC, 0x00, 0x81, 0xC2, 0xFD, 0x00, 0x82, 0xDA, 0x38 }.CopyTo(img, 0x8048 + 0x10);
        FiatCipher.DefaultKey.CopyTo(img, 0x8048 + 0x100);
        img[0x8048 + 0x200] = (byte)FiatCipher.DefaultKey.Length;
        return img;
    }

    /// <summary>Imagem ME3.1 de 512 KB: bloco de identificação em 0x80C6 na ordem Fiat, HW, 0x00, SW, versão.</summary>
    private static byte[] Image31(string hw, string sw, byte[] ver, string fiat)
    {
        var img = new byte[ExaminerLibrary.ImageSize];
        Array.Fill(img, (byte)0xFF);
        var id = new List<byte>();
        id.AddRange(F(fiat)); id.AddRange(F(hw)); id.Add(0); id.AddRange(F(sw)); id.AddRange(ver);
        id.AddRange(new byte[] { 0x20, 0x20, 0x7C, 0x86, 0x29, 0x01 });
        id.CopyTo(img, 0x80C6);
        return img; // sem programador no ficheiro (na ME3.1 está no arranque, fora do payload)
    }

    /// <summary>.Prm do perfil: zona e tamanho do perfil, checksum em 0x28 (big-endian).</summary>
    private static byte[] Prm(FiatFlashProfile prof, ushort checksum)
    {
        var p = Convert.FromHexString("01000000000000000000000000000000" + "000000F110031E020214005526010133" +
                                      "010180000087FFFF9737000000080000" + "80000087FFFF80000008000000000000" + "00000000");
        void Addr3(int at, uint a) { p[at] = (byte)(a >> 16); p[at + 1] = (byte)(a >> 8); p[at + 2] = (byte)a; }
        Addr3(0x22, prof.EraseStart); Addr3(0x25, prof.EraseEnd);
        p[0x2C] = 0; Addr3(0x2D, (uint)prof.PayloadSize); // tamanho big-endian em 0x2D..0x2F, como no Examiner
        p[0x28] = (byte)(checksum >> 8); p[0x29] = (byte)checksum;
        return p;
    }

    private static string NewFolder()
    {
        var d = Path.Combine(Path.GetTempPath(), "me7tool_examiner_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Grava o .BIN cifrado (só o payload do perfil) + .Prm como o Examiner, em XX/aa/bb/.</summary>
    private static string WriteExaminerFile(string root, string name, byte[] image, bool goodPrm = true, FiatFlashProfile? profile = null)
    {
        var prof = profile ?? FiatFlashProfile.Me731;
        var dir = Path.Combine(root, name[..2], name[2..4], name[4..6]);
        Directory.CreateDirectory(dir);
        var bin = Path.Combine(dir, name + ".BIN");
        var payload = image.AsSpan(prof.PayloadOffsetInImage, prof.PayloadSize).ToArray();
        File.WriteAllBytes(bin, FiatCipher.EncryptAll(payload));
        ushort cs = prof.ChecksumOf(prof.Prepare(image));
        File.WriteAllBytes(Path.Combine(dir, name + ".Prm"), Prm(prof, goodPrm ? cs : (ushort)(cs ^ 1)));
        return bin;
    }

    private static readonly byte[] V0101 = { 0x01, 0x01 }, V0404 = { 0x04, 0x04 }, V0203 = { 0x02, 0x03 };

    // ---- testes ----

    [Fact]
    public void FromImage_ReadsThe1037Block()
    {
        var id = EcuIdent.FromImage(Image("0261206957", "1037368755", V0404, "55193141"));
        Assert.NotNull(id);
        Assert.Equal("0261206957", id!.Hardware);
        Assert.Equal("1037368755", id.Software);
        Assert.Equal("0404", id.VersionHex);
        Assert.Equal("55193141", id.FiatDrawing);
    }

    [Fact]
    public void FromImage_ReadsThe1277BlockWithSpaces()
    {
        var id = EcuIdent.FromImage(Image("0261206716", "1277356447", new byte[] { 1, 3 }, "46815365", spaceSeparators: true));
        Assert.Equal("1277356447", id!.Software);
        Assert.Equal("0103", id.VersionHex);
    }

    [Fact]
    public void FromImage_NullWithoutBlock() => Assert.Null(EcuIdent.FromImage(new byte[ExaminerLibrary.ImageSize]));

    [Fact]
    public void Database_ParsesRecords()
    {
        var ndb = new List<byte>(new byte[100]);
        ndb.AddRange(Record("0261206957", "1037363791", V0101, "1037368755", V0404, "55193141", "BO010109.BIN"));
        ndb.AddRange(Record("0261206957", "1037366279", new byte[] { 2, 2 }, "1037368755", V0404, "55193141", "BO010109.BIN"));
        ndb.AddRange(new byte[50]);
        var recs = ExaminerDatabase.Parse(ndb.ToArray());
        Assert.Equal(2, recs.Count);
        Assert.Equal(new ExaminerRecord("BO010109.BIN", "0261206957", "1037363791", "0101", "1037368755", "0404", "55193141"), recs[0]);
        Assert.Equal("1037366279", recs[1].FromSoftware);
    }

    [Fact]
    public void OpenImage_DecryptsAndChecksThePrmCrc()
    {
        var root = NewFolder();
        try
        {
            var plain = Image("0261206957", "1037368755", V0404, "55193141");
            var bin = WriteExaminerFile(root, "BO010109", plain);
            var img = ExaminerLibrary.OpenImage(bin, out var why);
            Assert.Null(why);
            Assert.Equal(plain, img!.Image);
            Assert.Equal("1037368755", img.Ident.Software);

            var bad = WriteExaminerFile(root, "BO010110", plain, goodPrm: false);
            Assert.Null(ExaminerLibrary.OpenImage(bad, out why));
            Assert.Contains("CRC", why);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void OpenImage_RejectsOtherFlashAreas()
    {
        var root = NewFolder();
        try
        {
            var bin = WriteExaminerFile(root, "BO010201", Image("0261206982", "1037362063", V0101, "46822116"));
            var prm = Path.ChangeExtension(bin, ".Prm");
            var p = File.ReadAllBytes(prm); p[0x22] = 0x00; p[0x23] = 0x40; File.WriteAllBytes(prm, p); // 00 40 00 – 07 FF FF
            Assert.Null(ExaminerLibrary.OpenImage(bin, out var why));
            Assert.NotNull(why);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string ThesisFolder(out string root)
    {
        root = NewFolder();
        var ndb = new List<byte>(new byte[64]);
        ndb.AddRange(Record("0261206957", "1037363791", V0101, "1037368755", V0404, "55193141", "BO010109.BIN"));
        ndb.AddRange(Record("0261206957", "1037366279", new byte[] { 2, 2 }, "1037368755", V0404, "55193141", "BO010109.BIN"));
        File.WriteAllBytes(Path.Combine(root, "Global.ndb"), ndb.ToArray());
        WriteExaminerFile(root, "BO010109", Image("0261206957", "1037368755", V0404, "55193141"));
        return root;
    }

    [Theory]
    [InlineData("1037368755", ExaminerMatchKind.UpToDate)]
    [InlineData("1037363791", ExaminerMatchKind.UpdateAvailable)]
    [InlineData("1037999999", ExaminerMatchKind.SoftwareNotListed)]
    public void Match_ByEcuIdentification(string ecuSw, ExaminerMatchKind expected)
    {
        ThesisFolder(out var root);
        try
        {
            var lib = ExaminerLibrary.Load(root);
            Assert.True(lib.HasDatabase);
            var m = lib.Match(new EcuIdent("0261206957", ecuSw));
            Assert.Equal(expected, m.Kind);
            Assert.NotNull(m.Image);
            Assert.EndsWith("BO010109.BIN", m.Image!.Path, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Match_UnknownHardware_NoFile()
    {
        ThesisFolder(out var root);
        try { Assert.Equal(ExaminerMatchKind.NoFile, ExaminerLibrary.Load(root).Match(new EcuIdent("0261208118", "1037368765")).Kind); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Match_WithoutDatabase_UsesTheImages()
    {
        var root = NewFolder();
        try
        {
            WriteExaminerFile(root, "BO010109", Image("0261206957", "1037368755", V0404, "55193141"));
            var lib = ExaminerLibrary.Load(root);
            Assert.False(lib.HasDatabase);
            Assert.Equal(ExaminerMatchKind.UpToDate, lib.Match(new EcuIdent("0261206957", "1037368755")).Kind);
            var other = lib.Match(new EcuIdent("0261206957", "1037363791"));
            Assert.Equal(ExaminerMatchKind.SoftwareNotListed, other.Kind);
            Assert.NotNull(other.Image);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("0261206957")]   // nº Bosch
    [InlineData("206957")]       // parte do nº Bosch
    [InlineData("1037368755")]   // software de destino
    [InlineData("1037363791")]   // software de origem
    [InlineData("55193141")]     // desenho Fiat
    [InlineData("bo010109")]     // nome do ficheiro
    [InlineData(" 0261 206957 ")] // espaços ignorados
    public void Search_FindsByAnyReference(string query)
    {
        ThesisFolder(out var root);
        try
        {
            var r = Assert.Single(ExaminerLibrary.Load(root).Search(query));
            Assert.Equal("BO010109.BIN", r.File);
            Assert.Equal("1037368755", r.Software);
            Assert.Equal(new[] { "1037363791", "1037366279" }, r.FromSoftware);
            Assert.NotNull(r.Path);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Search_NoHitOrEmpty()
    {
        ThesisFolder(out var root);
        try
        {
            var lib = ExaminerLibrary.Load(root);
            Assert.Empty(lib.Search("0261208118"));
            Assert.Empty(lib.Search("   "));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Search_WithoutDatabase_UsesTheImages()
    {
        var root = NewFolder();
        try
        {
            WriteExaminerFile(root, "BO010109", Image("0261206957", "1037368755", V0404, "55193141"));
            var r = Assert.Single(ExaminerLibrary.Load(root).Search("55193141"));
            Assert.Equal("0261206957", r.Hardware);
            Assert.Equal("0404", r.Version);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Match_FileMissing_SaysSo()
    {
        ThesisFolder(out var root);
        try
        {
            foreach (var f in Directory.GetFiles(root, "*.BIN", SearchOption.AllDirectories)) File.Delete(f);
            var m = ExaminerLibrary.Load(root).Match(new EcuIdent("0261206957", "1037363791"));
            Assert.Equal(ExaminerMatchKind.UpdateAvailable, m.Kind);
            Assert.Null(m.Image);
            Assert.Contains("BO010109.BIN", m.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    // ---- ME3.1 ----

    [Fact]
    public void Profile_Me31_Parameters()
    {
        var p = FiatFlashProfile.Me31;
        Assert.Equal(0x004000u, p.DownloadStart);
        Assert.Equal(0x004000u, p.EraseStart);
        Assert.Equal(0x07FFFFu, p.EraseEnd);
        Assert.Equal(0x7C000, p.PayloadSize);
        Assert.Equal(0x04000, p.PayloadOffsetInImage);
        Assert.Equal(CrcHalf.Low, p.Checksum);
        Assert.False(p.HasBackupZone);
        // o payload é exatamente 0x4000..0x7FFFF da imagem; a checksum é a metade de baixo
        var img = Image31("0261206655", "1277356595", V0203, "55181376");
        var prepared = p.Prepare(img);
        Assert.Equal(0x7C000, prepared.Length);
        Assert.Equal((ushort)(Flasher.Crc32(prepared) & 0xFFFF), p.ChecksumOf(prepared));
    }

    [Fact]
    public void FromImage_ReadsThe31Block()
    {
        var id = EcuIdent.FromImage(Image31("0261206655", "1277356595", V0203, "55181376"));
        Assert.NotNull(id);
        Assert.Equal("0261206655", id!.Hardware);
        Assert.Equal("1277356595", id.Software);
        Assert.Equal("0203", id.VersionHex);
        Assert.Equal("55181376", id.FiatDrawing);
    }

    [Fact]
    public void OpenImage_Me31_496kFile()
    {
        var root = NewFolder();
        try
        {
            var img = Image31("0261206655", "1277356595", V0203, "55181376");
            var bin = WriteExaminerFile(root, "BO010302", img, profile: FiatFlashProfile.Me31);
            Assert.Equal(FiatFlashProfile.Me31.PayloadSize, new FileInfo(bin).Length); // o ficheiro é 496 KB
            var open = ExaminerLibrary.OpenImage(bin, out var why);
            Assert.Null(why);
            Assert.Equal("ME3.1", open!.Profile.Name);
            Assert.Equal("1277356595", open.Ident.Software);
            Assert.Equal(FiatFlashProfile.FullImageSize, open.Image.Length); // imagem de 512 KB para o flasher
            Assert.Equal(FiatFlashProfile.Me31.PayloadSize, open.Payload.Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Match_Me31_ByIdentification()
    {
        var root = NewFolder();
        try
        {
            WriteExaminerFile(root, "BO010302", Image31("0261206655", "1277356595", V0203, "55181376"), profile: FiatFlashProfile.Me31);
            var lib = ExaminerLibrary.Load(root);
            var m = lib.Match(new EcuIdent("0261206655", "1277356595"));
            Assert.Equal(ExaminerMatchKind.UpToDate, m.Kind);
            Assert.NotNull(m.Image);
            Assert.Equal("ME3.1", m.Image!.Profile.Name);
        }
        finally { Directory.Delete(root, true); }
    }
}
