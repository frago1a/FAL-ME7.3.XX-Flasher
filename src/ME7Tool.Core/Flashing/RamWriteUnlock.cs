using static ME7Tool.Core.Common.Lang;

namespace ME7Tool.Core.Flashing;

/// <summary>Onde está, numa imagem ME7.3.1, o byte que libera o 0x3D (WriteMemoryByAddress) e que valor tem.</summary>
/// <param name="FileOffset">Offset no ficheiro de 512 KB.</param>
/// <param name="Address">Endereço na ECU (0x8xxxxx).</param>
/// <param name="Value">Valor atual (0x00 = bloqueado de fábrica, 0x01 = desbloqueado).</param>
public sealed record RamWriteUnlockInfo(int FileOffset, uint Address, byte Value)
{
    public bool Unlocked => Value == 0x01;
}

/// <summary>
/// Desbloqueio do serviço 0x3D (escrita de RAM com a aplicação a correr) na família Fiat ME7.3.1.
/// <para>
/// O handler do 0x3D só carrega a tabela das zonas de escrita (0x380000–0x387FFF, 0xF600–0xFDFF, 0xE000–0xE7FF)
/// quando o modo de operação é o especial de bancada OU quando um byte de calibração vale 1. De fábrica vale 0 →
/// lista vazia → <c>7F 3D 12</c>. Na Thesis o byte é 0x812AA4 (confirmado no IDA e no carro); noutros softwares
/// o endereço muda, por isso procura-se pelo código:
/// </para>
/// <code>
///   movb rl4,[byte]      F3 F8 lo hi
///   cmpb rl4,#1          49 81
///   jmpr cc_NZ, …        3D xx
///   …
///   mov  r4,#tabela      E6 F4 lo hi
///   mov  r5,#página      E6 F5 lo hi    → tabela que começa 00 00 E0 00 FF 3F E1 00 (0x380000–0x387FFF)
/// </code>
/// O byte é endereçado pelo DPP0, que na ME7.3.1 aponta à página da calibração 0x204 (0x810000).
/// Os softwares 1277… antigos (Alfa 147) não têm esta porta: <see cref="Find"/> devolve null.
/// <para>ATENÇÃO: o byte está na calibração, coberta pelas checksums da Bosch. Mudá-lo obriga a corrigir as
/// checksums (WinOLS) antes de gravar — este código não as corrige.</para>
/// </summary>
public static class RamWriteUnlock
{
    /// <summary>Primeira entrada da tabela das zonas de escrita: 0x380000–0x387FFF (endereços em página:offset).</summary>
    private static readonly byte[] WriteRegionTable = { 0x00, 0x00, 0xE0, 0x00, 0xFF, 0x3F, 0xE1, 0x00 };

    /// <summary>Página do DPP0 na ME7.3.1 (calibração em 0x810000).</summary>
    private const uint CalibrationPage = 0x204;

    /// <summary>
    /// Procura o byte numa imagem ME7.3.1 de 512 KB desencriptada. null se a imagem não for ME7.3.1, se este
    /// software não tiver a porta, ou se as ocorrências não concordarem (não se arrisca um endereço duvidoso).
    /// </summary>
    public static RamWriteUnlockInfo? Find(byte[] image)
    {
        if (image.Length != FiatFlashProfile.FullImageSize || FiatFlashProfile.Detect(image) != FiatFlashProfile.Me731) return null;

        var mems = new HashSet<ushort>();
        for (int t = 0; (t = IndexOf(image, WriteRegionTable, t)) >= 0; t++)
        {
            uint lin = 0x800000u + (uint)t;
            ushort off = (ushort)(lin & 0x3FFF), page = (ushort)(lin >> 14);
            byte[] load = { 0xE6, 0xF4, (byte)off, (byte)(off >> 8), 0xE6, 0xF5, (byte)page, (byte)(page >> 8) };
            for (int l = 0; (l = IndexOf(image, load, l)) >= 0; l++)
            {
                // a porta fica nos 24 bytes antes de carregar a tabela
                for (int g = Math.Max(0, l - 24); g + 8 <= l; g++)
                {
                    if (image[g] != 0xF3 || image[g + 1] != 0xF8 || image[g + 4] != 0x49 || image[g + 5] != 0x81 || image[g + 6] != 0x3D) continue;
                    mems.Add((ushort)(image[g + 2] | image[g + 3] << 8));
                }
            }
        }
        if (mems.Count != 1) return null;                 // nenhuma ou ambíguas → não arriscar
        ushort mem = mems.First();
        if (mem >> 14 != 0) return null;                  // tem de ser endereçado pelo DPP0
        uint address = (CalibrationPage << 14) | (uint)(mem & 0x3FFF);
        int fileOffset = (int)(address - 0x800000u);
        if (fileOffset < 0x10000 || fileOffset >= 0x20000) return null; // tem de cair na calibração
        byte value = image[fileOffset];
        if (value > 1) return null;                       // de fábrica 0, desbloqueado 1; outra coisa = não é este byte
        return new RamWriteUnlockInfo(fileOffset, address, value);
    }

    /// <summary>
    /// Cópia da imagem com o 0x3D desbloqueado (byte = 1). As checksums da Bosch ficam por corrigir.
    /// </summary>
    public static byte[] Apply(byte[] image, out RamWriteUnlockInfo info)
    {
        info = Find(image) ?? throw new InvalidOperationException(
            T("Não encontrei o byte de desbloqueio do 0x3D neste ficheiro (só existe nos softwares ME7.3.1 1037…; os 1277… antigos não o têm).",
              "Could not find the 0x3D unlock byte in this file (it only exists in ME7.3.1 1037… software; the older 1277… ones do not have it)."));
        var patched = (byte[])image.Clone();
        patched[info.FileOffset] = 0x01;
        return patched;
    }

    private static int IndexOf(byte[] data, byte[] pattern, int from)
    {
        for (int i = from; i + pattern.Length <= data.Length; i++)
        {
            int k = 0;
            while (k < pattern.Length && data[i + k] == pattern[k]) k++;
            if (k == pattern.Length) return i;
        }
        return -1;
    }
}
