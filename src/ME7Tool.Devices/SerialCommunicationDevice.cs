using System.IO.Ports;
using ME7Tool.Core.Transport;

namespace ME7Tool.Devices;

/// <summary>
/// Backend K-line sobre uma porta série (cabo KKL em COM virtual). Amplamente compatível.
/// O fast-init usa BreakState; o 5-baud não é fiável por série (preferir FTDI/J2534 para 5-baud).
/// </summary>
public sealed class SerialCommunicationDevice : ICommunicationDevice
{
    private readonly SerialPort _port;

    public string Name { get; }
    public bool IsOpen => _port.IsOpen;

    public SerialCommunicationDevice(string portName, int baudRate = 10400)
    {
        Name = portName;
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 1000,
            WriteTimeout = 1000,
            Handshake = Handshake.None,
        };
    }

    public static string[] GetPortNames() => SerialPort.GetPortNames();

    public void Open() { if (!_port.IsOpen) _port.Open(); }
    public void Close() { if (_port.IsOpen) _port.Close(); }

    public void SetBaudRate(int baudRate) => _port.BaudRate = baudRate;

    public void Write(ReadOnlySpan<byte> data)
    {
        var buf = data.ToArray();
        _port.Write(buf, 0, buf.Length);
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        int t = timeoutMs <= 0 ? 1 : timeoutMs;
        if (_port.ReadTimeout != t) _port.ReadTimeout = t; // cada set = SetCommTimeouts no driver
        var tmp = new byte[buffer.Length];
        try
        {
            int n = _port.Read(tmp, 0, tmp.Length); // bloqueia até >=1 byte ou timeout
            tmp.AsSpan(0, n).CopyTo(buffer);
            return n;
        }
        catch (TimeoutException)
        {
            return 0;
        }
    }

    public void DiscardBuffers()
    {
        if (!_port.IsOpen) return;
        _port.DiscardInBuffer();
        _port.DiscardOutBuffer();
    }

    public void SetBreak(bool enabled)
    {
        try { _port.BreakState = enabled; } catch { /* nem todos os drivers suportam */ }
    }

    public void Dispose() => _port.Dispose();
}
