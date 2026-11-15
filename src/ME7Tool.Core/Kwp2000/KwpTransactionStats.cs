using System.Diagnostics;

namespace ME7Tool.Core.Kwp2000;

/// <summary>
/// Médias de tempo por fase de cada pedido/resposta KWP2000 — para perceber onde se gasta a
/// cadência do logging sem osciloscópio:
/// <list type="bullet">
/// <item><b>Gap</b>: fim da resposta anterior → início deste pedido (tempo do PC entre pedidos).</item>
/// <item><b>Tx</b>: chamada de escrita ao driver.</item>
/// <item><b>Echo</b>: até o eco do pedido estar todo lido (≈ tempo de linha do pedido + latência USB).</item>
/// <item><b>Wait</b>: fim do eco → 1º byte da resposta (≈ P2 da ECU + latência USB).</item>
/// <item><b>Rx</b>: 1º byte → frame completo (≈ tempo de linha da resposta).</item>
/// </list>
/// Thread-safe o suficiente para ser lido pela UI enquanto o logger escreve.
/// </summary>
public sealed class KwpTransactionStats
{
    private readonly object _lock = new();
    private long _n, _nGap, _gap, _tx, _echo, _wait, _rx;

    internal void Add(long gapTicks, long txTicks, long echoTicks, long waitTicks, long rxTicks)
    {
        lock (_lock)
        {
            _n++;
            // Gaps longos (keep-alive, pausas entre comandos) não são cadência — ignorar.
            if (gapTicks >= 0 && gapTicks < Stopwatch.Frequency / 5) { _nGap++; _gap += gapTicks; }
            _tx += txTicks; _echo += echoTicks; _wait += waitTicks; _rx += rxTicks;
        }
    }

    public void Reset()
    {
        lock (_lock) { _n = _nGap = _gap = _tx = _echo = _wait = _rx = 0; }
    }

    /// <summary>Médias (ms) desde o último Reset.</summary>
    public (long Count, double GapMs, double TxMs, double EchoMs, double WaitMs, double RxMs) Averages()
    {
        lock (_lock)
        {
            double f = 1000.0 / Stopwatch.Frequency;
            if (_n == 0) return (0, 0, 0, 0, 0, 0);
            return (_n, _nGap > 0 ? _gap * f / _nGap : 0, _tx * f / _n, _echo * f / _n, _wait * f / _n, _rx * f / _n);
        }
    }

    public override string ToString()
    {
        var a = Averages();
        double total = a.GapMs + a.TxMs + a.EchoMs + a.WaitMs + a.RxMs;
        return $"{a.Count} pedidos · média/pedido {total:0.0} ms = gap {a.GapMs:0.0} + tx {a.TxMs:0.0} + eco {a.EchoMs:0.0} + espera ECU {a.WaitMs:0.0} + rx {a.RxMs:0.0}";
    }
}
