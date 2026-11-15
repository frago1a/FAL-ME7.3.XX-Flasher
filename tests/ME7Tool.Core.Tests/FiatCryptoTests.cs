using ME7Tool.Core.Flashing;
using Xunit;

namespace ME7Tool.Core.Tests;

public class FiatCryptoTests
{
    [Fact]
    public void Decrypt_MatchesReferenceVector()
    {
        // vetor gerado com o modelo independente (o mesmo que desencriptou o BO010109.BIN do Examiner)
        var plain = FiatCipher.DecryptAll(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        Assert.Equal("89A97410739B7D041502090AD6F766A28BB273505B1E79140002193617FFE7AB", Convert.ToHexString(plain));
    }

    [Fact]
    public void EncryptThenDecrypt_RoundTrips_AcrossThe16kReset()
    {
        var data = new byte[FiatCipher.ResetInterval * 2 + 123];
        new Random(5).NextBytes(data);
        var enc = FiatCipher.EncryptAll(data);
        Assert.NotEqual(data, enc);
        Assert.Equal(data, FiatCipher.DecryptAll(enc));
    }

    [Fact]
    public void StateRestartsEvery16k()
    {
        var block = new byte[FiatCipher.ResetInterval];
        new Random(9).NextBytes(block);
        var two = FiatCipher.EncryptAll(block.Concat(block).ToArray());
        Assert.Equal(two.AsSpan(0, block.Length).ToArray(), two.AsSpan(block.Length).ToArray());
    }

    [Fact]
    public void Encrypting_InBlocks_EqualsEncryptingAllAtOnce()
    {
        var data = new byte[5000];
        new Random(1).NextBytes(data);
        var c = new FiatCipher();
        var parts = new List<byte>();
        for (int i = 0; i < data.Length; i += 252)
        {
            int n = Math.Min(252, data.Length - i);
            var o = new byte[n];
            c.Encrypt(data.AsSpan(i, n), o);
            parts.AddRange(o);
        }
        Assert.Equal(FiatCipher.EncryptAll(data), parts.ToArray());
    }

    [Fact]
    public void FindKey_ReadsTheKeyFromThe34HandlerPattern()
    {
        var img = new byte[0x80000];
        // como na Thesis: mov r12,#A09Ah; movbz r13,A0B2h; calls 38h,2D3Ch (flash 0x808C12)
        new byte[] { 0xE6, 0xFC, 0x9A, 0xA0, 0xC2, 0xFD, 0xB2, 0xA0, 0xDA, 0x38, 0x3C, 0x2D }.CopyTo(img, 0x8C12);
        FiatCipher.DefaultKey.CopyTo(img, 0x8048 + 0x209A);
        img[0x8048 + 0x20B2] = (byte)FiatCipher.DefaultKey.Length;
        Assert.Equal(FiatCipher.DefaultKey, FiatFullFlash.FindKey(img));
        Assert.Null(FiatFullFlash.FindKey(new byte[0x80000]));
    }

    [Fact]
    public void Prepare_BlanksTheBackupZoneLikeTheExaminer()
    {
        var img = new byte[0x80000];
        new Random(2).NextBytes(img);
        var p = FiatFullFlash.Prepare(img);
        Assert.Equal(img.AsSpan(0, 0x38000).ToArray(), p.AsSpan(0, 0x38000).ToArray());
        Assert.Equal(img.AsSpan(0x40000).ToArray(), p.AsSpan(0x40000).ToArray());
        var nonFF = Enumerable.Range(0x38000, 0x8000).Where(i => p[i] != 0xFF).ToArray();
        Assert.Equal(new[] { 0x38000, 0x38001, 0x39FFE, 0x39FFF, 0x3A000, 0x3A001, 0x3BFFE, 0x3BFFF,
                             0x3C000, 0x3C001, 0x3DFFE, 0x3DFFF, 0x3E000, 0x3E001, 0x3FFFE, 0x3FFFF }, nonFF);
        Assert.Equal(0x00, p[0x38000]); Assert.Equal(0x80, p[0x38001]);
        Assert.Equal((ushort)(Flasher.Crc32(p) >> 16), FiatFullFlash.Checksum(p));
    }

    [Fact]
    public void Identify_FindsBoschNumbers()
    {
        var img = new byte[0x1000];
        System.Text.Encoding.ASCII.GetBytes("41/1/ME731/9/414_0/411C//11C0Xw00/181002/\0\00261206957 \01037368755 ").CopyTo(img, 0x100);
        var id = FiatFullFlash.Identify(img);
        Assert.NotNull(id);
        Assert.Contains("0261206957", id);
        Assert.Contains("1037368755", id);
        Assert.Null(FiatFullFlash.Identify(new byte[0x1000]));
    }
}
