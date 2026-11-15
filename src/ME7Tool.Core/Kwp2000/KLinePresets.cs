using static ME7Tool.Core.Common.Lang;
using ME7Tool.Core.Transport;

namespace ME7Tool.Core.Kwp2000;

/// <summary>Endereço KWP2000 da ECU na K-line para uma marca, com o init e a sessão habituais.</summary>
/// <param name="SessionIndex">Posição na lista de sessões do logger: 0 nenhuma, 1 0x81, 2 0x85, 3 0x86.</param>
public sealed record KLinePreset(string Name, byte Address, KLineInit Init, int SessionIndex)
{
    public override string ToString() => Name;
}

/// <summary>
/// Endereços KWP2000 (ISO 14230) conhecidos na K-line. 0x33 é o endereço funcional OBD-II: qualquer ECU de
/// motor responde ao StartCommunication a partir do seu endereço físico, que passa a ser usado.
/// </summary>
public static class KLinePresets
{
    public const byte ObdFunctional = 0x33;

    public static IReadOnlyList<KLinePreset> All { get; } = new[]
    {
        new KLinePreset(T("Fiat/Alfa/Lancia · motor (0x10, fast)", "Fiat/Alfa/Lancia · engine (0x10, fast)"), 0x10, KLineInit.FastInit, 3),
        new KLinePreset(T("VAG · motor (0x01, 5 baud)", "VAG · engine (0x01, 5 baud)"), 0x01, KLineInit.FiveBaud, 3),
        new KLinePreset(T("VAG · caixa (0x02, 5 baud)", "VAG · gearbox (0x02, 5 baud)"), 0x02, KLineInit.FiveBaud, 1),
        new KLinePreset(T("Opel/GM · motor (0x11, fast)", "Opel/GM · engine (0x11, fast)"), 0x11, KLineInit.FastInit, 1),
        new KLinePreset(T("BMW KWP · DME (0x12, fast)", "BMW KWP · DME (0x12, fast)"), 0x12, KLineInit.FastInit, 1),
        new KLinePreset(T("Renault/Dacia · injeção (0x7A, fast)", "Renault/Dacia · injection (0x7A, fast)"), 0x7A, KLineInit.FastInit, 0),
        new KLinePreset(T("Genérico ISO 14230 · motor (0x10, fast)", "Generic ISO 14230 · engine (0x10, fast)"), 0x10, KLineInit.FastInit, 1),
        new KLinePreset(T("OBD-II funcional (0x33, fast)", "OBD-II functional (0x33, fast)"), ObdFunctional, KLineInit.FastInit, 0),
    };

    /// <summary>Endereços distintos a tentar na procura: primeiro os de fast-init (rápidos), depois os de 5 baud.</summary>
    public static IReadOnlyList<(byte Address, KLineInit Init)> ScanOrder { get; } =
        All.Select(p => (p.Address, p.Init)).Distinct()
           .OrderBy(x => x.Init == KLineInit.FiveBaud ? 1 : 0).ToArray();

    public static KLinePreset? Find(byte address, KLineInit init)
        => All.FirstOrDefault(p => p.Address == address && p.Init == init);
}
