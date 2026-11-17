namespace ME7Tool.Core.Transport;

/// <summary>
/// Interface que mede a tensão da bateria no pino 16 da ficha OBD (J2534: IOCTL READ_VBATT). Não fala com a ECU.
/// </summary>
public interface IBatteryMonitor
{
    /// <summary>Tensão em volts, ou null se o interface não estiver aberto ou não souber medir.</summary>
    double? ReadBatteryVolts();
}

/// <summary>Limites para avisar da bateria (motor parado ~12,6 V cheia; a trabalhar ~13,5–14,5 V).</summary>
public static class BatteryLevels
{
    /// <summary>Abaixo disto: bateria fraca (ECU pode desligar, flash arriscado).</summary>
    public const double Low = 11.5;

    /// <summary>Abaixo disto não se deve programar (flash) sem carregador.</summary>
    public const double FlashMin = 12.0;
}
