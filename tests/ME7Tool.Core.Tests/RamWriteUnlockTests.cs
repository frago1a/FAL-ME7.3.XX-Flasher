using System.Text;
using ME7Tool.Core.Flashing;

namespace ME7Tool.Core.Tests;

public class RamWriteUnlockTests
{
    /// <summary>
    /// Imagem ME7.3.1 sintética com o mesmo código da Thesis: bloco de identificação, tabela das zonas de escrita
    /// em 0x19FC0 e, em 0x2B7E0, a porta (movb rl4,[0x2AA4]; cmpb rl4,#1; jmpr) seguida da carga da tabela.
    /// </summary>
    private static byte[] Me731Image(byte unlockValue = 0x00, ushort mem = 0x2AA4)
    {
        var img = new byte[FiatFlashProfile.FullImageSize];
        Array.Fill(img, (byte)0xFF);
        var id = new List<byte> { 0x7C, 0x86, 0xBC, 0x02, 0x15 };
        id.AddRange(Encoding.ASCII.GetBytes("0261206957 ")); id.Add(0);
        id.AddRange(Encoding.ASCII.GetBytes("1037368755 ")); id.Add(0);
        id.AddRange(new byte[] { 4, 4 }); id.AddRange(Encoding.ASCII.GetBytes("55193141   "));
        id.CopyTo(img, 0x101D7);
        new byte[] { 0x00, 0x00, 0xE0, 0x00, 0xFF, 0x3F, 0xE1, 0x00 }.CopyTo(img, 0x19FC0);
        new byte[]
        {
            0x46, 0xF4, 0x16, 0x1D, 0x2D, 0x04,             // cmp r4,#1D16h; jmpr cc_Z
            0xF3, 0xF8, (byte)mem, (byte)(mem >> 8),       // movb rl4,[mem]
            0x49, 0x81, 0x3D, 0x0C,                         // cmpb rl4,#1; jmpr cc_NZ
            0xE1, 0x0C, 0xE1, 0x3E, 0xE0, 0x08, 0xE0, 0x09,
            0xE6, 0xF4, 0xC0, 0x1F, 0xE6, 0xF5, 0x06, 0x02, // mov r4,#1FC0h; mov r5,#206h → 0x819FC0
        }.CopyTo(img, 0x2B7E0);
        img[0x12AA4] = unlockValue;
        return img;
    }

    [Fact]
    public void Find_TheThesisByte()
    {
        var info = RamWriteUnlock.Find(Me731Image());
        Assert.NotNull(info);
        Assert.Equal(0x812AA4u, info!.Address);
        Assert.Equal(0x12AA4, info.FileOffset);
        Assert.False(info.Unlocked);
    }

    [Fact]
    public void Apply_SetsTheByteAndLeavesTheRest()
    {
        var img = Me731Image();
        var patched = RamWriteUnlock.Apply(img, out var info);
        Assert.Equal(1, patched[0x12AA4]);
        Assert.Equal(0, img[0x12AA4]); // o original não é tocado
        int diffs = 0; for (int i = 0; i < img.Length; i++) if (img[i] != patched[i]) diffs++;
        Assert.Equal(1, diffs);
        Assert.True(RamWriteUnlock.Find(patched)!.Unlocked);
    }

    [Fact]
    public void Find_NullWithoutTheGate()
    {
        var img = Me731Image();
        img[0x2B7E0 + 10] = 0x48; // estraga o cmpb rl4,#1
        Assert.Null(RamWriteUnlock.Find(img));
    }

    [Fact]
    public void Find_NullForMe31Images()
    {
        // ME3.1: bloco de identificação na calibração (0x80C6) → não é ME7.3.1
        var img = new byte[FiatFlashProfile.FullImageSize];
        Array.Fill(img, (byte)0xFF);
        Encoding.ASCII.GetBytes("55181376   0261206655 \0" + "1277356595 ").CopyTo(img, 0x80C6);
        Assert.Null(RamWriteUnlock.Find(img));
    }

    [Fact]
    public void Find_NullIfTheByteIsNotZeroOrOne()
    {
        Assert.Null(RamWriteUnlock.Find(Me731Image(unlockValue: 0x5A)));
    }

    [Fact]
    public void Apply_ThrowsWhenNotFound()
    {
        var img = Me731Image(); img[0x19FC0] = 0x11; // sem tabela
        Assert.Throws<InvalidOperationException>(() => RamWriteUnlock.Apply(img, out _));
    }
}
