using static ME7Tool.Core.Common.Lang;
using System.Runtime.InteropServices;
using ME7Tool.Core.Transport;

namespace ME7Tool.Devices;

/// <summary>
/// Backend K-line sobre FTDI D2XX (ftd2xx.dll). Suporta baud não-standard (10400, 124800)
/// e break preciso para fast/5-baud init. Requer o driver FTDI D2XX instalado.
/// </summary>
public sealed class FtdiCommunicationDevice : ICommunicationDevice, IModemLines
{
    private IntPtr _handle = IntPtr.Zero;
    private readonly int _index;
    private uint _lastReadTimeout;

    /// <summary>
    /// Latency timer do FTDI (ms). O default de 16 ms limita o logging a ~15 Hz porque o chip
    /// só entrega os bytes recebidos a cada 16 ms. 1 ms permite dezenas de Hz.
    /// </summary>
    public byte LatencyTimerMs { get; set; } = 1;

    public string Name { get; }
    public bool IsOpen => _handle != IntPtr.Zero;

    public FtdiCommunicationDevice(int deviceIndex = 0, string? name = null)
    {
        _index = deviceIndex;
        Name = name ?? $"FTDI#{deviceIndex}";
    }

    /// <summary>Nº de dispositivos FTDI ligados.</summary>
    public static int DeviceCount()
    {
        Check(FT_CreateDeviceInfoList(out uint count), "FT_CreateDeviceInfoList");
        return (int)count;
    }

    public void Open()
    {
        if (IsOpen) return;
        Check(FT_Open(_index, out _handle), "FT_Open");
        Check(FT_SetDataCharacteristics(_handle, FT_BITS_8, FT_STOP_BITS_1, FT_PARITY_NONE), "FT_SetDataCharacteristics");
        Check(FT_SetTimeouts(_handle, 1000, 1000), "FT_SetTimeouts"); _lastReadTimeout = 1000;
        // Latency timer baixo = entrega imediata dos bytes (crítico para a cadência do logging).
        FT_SetLatencyTimer(_handle, LatencyTimerMs == 0 ? (byte)1 : LatencyTimerMs);
        // Transferências USB pequenas reduzem ainda mais a latência das respostas curtas.
        FT_SetUSBParameters(_handle, 64, 64);
        Check(FT_Purge(_handle, FT_PURGE_RX | FT_PURGE_TX), "FT_Purge");
    }

    public void Close()
    {
        if (!IsOpen) return;
        FT_Close(_handle);
        _handle = IntPtr.Zero;
    }

    public void SetBaudRate(int baudRate)
        => Check(FT_SetBaudRate(_handle, (uint)baudRate), "FT_SetBaudRate");

    public void Write(ReadOnlySpan<byte> data)
    {
        var buf = data.ToArray();
        Check(FT_Write(_handle, buf, (uint)buf.Length, out uint written), "FT_Write");
        if (written != buf.Length)
            throw new IOException(T($"FTDI escreveu {written}/{buf.Length} bytes.", $"FTDI wrote {written}/{buf.Length} bytes."));
    }

    private byte[] _readBuf = new byte[1024];

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        uint t = (uint)Math.Max(1, timeoutMs);
        if (t != _lastReadTimeout) // o Kwp2000Interface passa timeouts constantes → raramente muda
        {
            Check(FT_SetTimeouts(_handle, t, 1000), "FT_SetTimeouts");
            _lastReadTimeout = t;
        }
        // Se já há bytes na fila, lê só esses (não bloqueia). Se a fila está vazia, pede o que
        // falta e o FT_Read bloqueia até chegarem (ou timeout) — sem polling.
        Check(FT_GetQueueStatus(_handle, out uint queued), "FT_GetQueueStatus");
        int want = queued > 0 ? Math.Min((int)queued, buffer.Length) : buffer.Length;
        if (_readBuf.Length < want) _readBuf = new byte[want];
        Check(FT_Read(_handle, _readBuf, (uint)want, out uint read), "FT_Read");
        _readBuf.AsSpan(0, (int)read).CopyTo(buffer);
        return (int)read;
    }

    public void DiscardBuffers()
    {
        if (IsOpen) FT_Purge(_handle, FT_PURGE_RX | FT_PURGE_TX);
    }

    public void SetBreak(bool enabled)
    {
        if (!IsOpen) return;
        if (enabled) FT_SetBreakOn(_handle);
        else FT_SetBreakOff(_handle);
    }

    public void SetDtr(bool on) => Check(on ? FT_SetDtr(_handle) : FT_ClrDtr(_handle), on ? "FT_SetDtr" : "FT_ClrDtr");
    public void SetRts(bool on) => Check(on ? FT_SetRts(_handle) : FT_ClrRts(_handle), on ? "FT_SetRts" : "FT_ClrRts");

    public void Dispose() => Close();

    private static void Check(uint status, string op)
    {
        if (status != 0) throw new IOException(T($"{op} falhou (FT_STATUS={status}).", $"{op} failed (FT_STATUS={status})."));
    }

    // ------------------------------------------------- P/Invoke ftd2xx.dll
    private const byte FT_BITS_8 = 8, FT_STOP_BITS_1 = 0, FT_PARITY_NONE = 0;
    private const uint FT_PURGE_RX = 1, FT_PURGE_TX = 2;

    [DllImport("ftd2xx.dll")] private static extern uint FT_CreateDeviceInfoList(out uint numDevs);
    [DllImport("ftd2xx.dll")] private static extern uint FT_Open(int index, out IntPtr handle);
    [DllImport("ftd2xx.dll")] private static extern uint FT_Close(IntPtr handle);
    [DllImport("ftd2xx.dll")] private static extern uint FT_SetBaudRate(IntPtr handle, uint baud);
    [DllImport("ftd2xx.dll")] private static extern uint FT_SetDataCharacteristics(IntPtr handle, byte wordLength, byte stopBits, byte parity);
    [DllImport("ftd2xx.dll")] private static extern uint FT_SetTimeouts(IntPtr handle, uint readMs, uint writeMs);
    [DllImport("ftd2xx.dll")] private static extern uint FT_Purge(IntPtr handle, uint mask);
    [DllImport("ftd2xx.dll")] private static extern uint FT_Write(IntPtr handle, byte[] buffer, uint bytesToWrite, out uint written);
    [DllImport("ftd2xx.dll")] private static extern uint FT_Read(IntPtr handle, byte[] buffer, uint bytesToRead, out uint read);
    [DllImport("ftd2xx.dll")] private static extern uint FT_SetBreakOn(IntPtr handle);
    [DllImport("ftd2xx.dll")] private static extern uint FT_SetBreakOff(IntPtr handle);
    [DllImport("ftd2xx.dll")] private static extern uint FT_SetLatencyTimer(IntPtr handle, byte ms);
    [DllImport("ftd2xx.dll")] private static extern uint FT_SetUSBParameters(IntPtr handle, uint inTransferSize, uint outTransferSize);
    [DllImport("ftd2xx.dll")] private static extern uint FT_GetQueueStatus(IntPtr handle, out uint rxBytes);
    [DllImport("ftd2xx.dll")] private static extern uint FT_SetDtr(IntPtr handle);
    [DllImport("ftd2xx.dll")] private static extern uint FT_ClrDtr(IntPtr handle);
    [DllImport("ftd2xx.dll")] private static extern uint FT_SetRts(IntPtr handle);
    [DllImport("ftd2xx.dll")] private static extern uint FT_ClrRts(IntPtr handle);
}
