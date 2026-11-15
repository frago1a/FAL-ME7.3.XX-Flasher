using System.Runtime.InteropServices;

namespace ME7Tool.Devices.J2534;

/// <summary>Estrutura PASSTHRU_MSG (J2534 v04.04).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PassThruMsg
{
    public uint ProtocolID;
    public uint RxStatus;
    public uint TxFlags;
    public uint Timestamp;
    public uint DataSize;
    public uint ExtraDataIndex;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4128)]
    public byte[] Data;

    public static PassThruMsg Create(uint protocolId)
        => new() { ProtocolID = protocolId, Data = new byte[4128] };
}

/// <summary>Constantes J2534.</summary>
public static class J2534Const
{
    public const uint CAN = 5, ISO15765 = 6, ISO9141 = 3, ISO14230 = 4;
    public const uint CAN_29BIT_ID = 0x0100;
    public const uint PASS_FILTER = 1, FLOW_CONTROL_FILTER = 3;
    public const uint ISO15765_FRAME_PAD = 0x40, ISO15765_ADDR_TYPE = 0x80, TX_DONE = 0x08, ISO15765_PADDING_ERROR = 0x10;
    public const uint ERR_TIMEOUT = 0x09;
    public const uint CLEAR_TX_BUFFER = 7, CLEAR_RX_BUFFER = 8;
    public const uint STATUS_NOERROR = 0;
    // IOCTL
    public const uint GET_CONFIG = 0x01, SET_CONFIG = 0x02, FIVE_BAUD_INIT = 0x04, FAST_INIT = 0x05;
    // Parâmetros de configuração (SCONFIG)
    public const uint DATA_RATE = 0x01, LOOP_BACK = 0x03, P1_MAX = 0x07, P3_MIN = 0x0A, P4_MIN = 0x0C;
    // RxStatus
    public const uint TX_MSG_TYPE = 0x01, START_OF_MESSAGE = 0x02;
    public const uint ERR_BUFFER_EMPTY = 0x10;
}

/// <summary>
/// Liga dinamicamente à DLL J2534 de um fornecedor (nome varia — Scanmatik, Tactrix, etc.)
/// e expõe as funções PassThru. Carregamento via NativeLibrary (a DLL não tem nome fixo).
/// </summary>
public sealed class J2534Native : IDisposable
{
    private readonly IntPtr _lib;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint DOpen(IntPtr name, out uint deviceId);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint DClose(uint deviceId);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint DConnect(uint deviceId, uint protocolId, uint flags, uint baud, out uint channelId);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint DDisconnect(uint channelId);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint DReadMsgs(uint channelId, ref PassThruMsg msgs, ref uint numMsgs, uint timeout);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint DWriteMsgs(uint channelId, ref PassThruMsg msgs, ref uint numMsgs, uint timeout);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint DStartFilter(uint channelId, uint type, ref PassThruMsg mask, ref PassThruMsg pattern, IntPtr flowControl, out uint filterId);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint DIoctl(uint channelId, uint ioctlId, IntPtr input, IntPtr output);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint DGetLastError(byte[] err);

    private readonly DOpen _open;
    private readonly DClose _close;
    private readonly DConnect _connect;
    private readonly DDisconnect _disconnect;
    private readonly DReadMsgs _read;
    private readonly DWriteMsgs _write;
    private readonly DStartFilter _startFilter;
    private readonly DIoctl _ioctl;
    private readonly DGetLastError? _getLastError;

    private static readonly Dictionary<string, J2534Native> Loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// DLL do fornecedor carregada uma só vez por processo e nunca descarregada: carregar/descarregar seguido
    /// (como na procura de ECU, que abre e fecha o interface várias vezes) deixa alguns drivers num estado
    /// em que o PassThruOpen seguinte falha logo.
    /// </summary>
    public static J2534Native Load(string dllPath)
    {
        lock (Loaded)
        {
            if (!Loaded.TryGetValue(dllPath, out var n)) Loaded[dllPath] = n = new J2534Native(dllPath);
            return n;
        }
    }

    private J2534Native(string dllPath)
    {
        try
        {
            _lib = NativeLibrary.Load(dllPath);
        }
        catch (BadImageFormatException)
        {
            throw new BadImageFormatException(
                $"A DLL J2534 '{dllPath}' tem uma arquitetura diferente do processo. " +
                "Muitos interfaces (ex.: Scanmatik SM2) só têm DLL 32-bit — corre a app em x86 " +
                "(define <PlatformTarget>x86</PlatformTarget> ou usa a DLL 64-bit do fornecedor).");
        }
        _open = Bind<DOpen>("PassThruOpen");
        _close = Bind<DClose>("PassThruClose");
        _connect = Bind<DConnect>("PassThruConnect");
        _disconnect = Bind<DDisconnect>("PassThruDisconnect");
        _read = Bind<DReadMsgs>("PassThruReadMsgs");
        _write = Bind<DWriteMsgs>("PassThruWriteMsgs");
        _startFilter = Bind<DStartFilter>("PassThruStartMsgFilter");
        _ioctl = Bind<DIoctl>("PassThruIoctl");
        NativeLibrary.TryGetExport(_lib, "PassThruGetLastError", out var gle);
        _getLastError = gle != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<DGetLastError>(gle) : null;
    }

    private T Bind<T>(string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, name));

    public uint Open(out uint deviceId) => _open(IntPtr.Zero, out deviceId);
    public uint Close(uint deviceId) => _close(deviceId);
    public uint Connect(uint deviceId, uint protocolId, uint flags, uint baud, out uint channelId)
        => _connect(deviceId, protocolId, flags, baud, out channelId);
    public uint Disconnect(uint channelId) => _disconnect(channelId);
    public uint ReadMsgs(uint channelId, ref PassThruMsg msg, ref uint numMsgs, uint timeout)
        => _read(channelId, ref msg, ref numMsgs, timeout);
    public uint WriteMsgs(uint channelId, ref PassThruMsg msg, ref uint numMsgs, uint timeout)
        => _write(channelId, ref msg, ref numMsgs, timeout);
    public uint StartMsgFilter(uint channelId, uint type, ref PassThruMsg mask, ref PassThruMsg pattern, out uint filterId)
        => _startFilter(channelId, type, ref mask, ref pattern, IntPtr.Zero, out filterId);

    /// <summary>FLOW_CONTROL_FILTER (ISO15765): o interface responde com FlowControl no ID de <paramref name="flowControl"/>.</summary>
    public uint StartFlowControlFilter(uint channelId, ref PassThruMsg mask, ref PassThruMsg pattern, ref PassThruMsg flowControl, out uint filterId)
    {
        IntPtr fc = Marshal.AllocHGlobal(Marshal.SizeOf<PassThruMsg>());
        try
        {
            Marshal.StructureToPtr(flowControl, fc, false);
            return _startFilter(channelId, J2534Const.FLOW_CONTROL_FILTER, ref mask, ref pattern, fc, out filterId);
        }
        finally { Marshal.FreeHGlobal(fc); }
    }
    public uint Ioctl(uint channelId, uint ioctlId) => _ioctl(channelId, ioctlId, IntPtr.Zero, IntPtr.Zero);
    public uint Ioctl(uint channelId, uint ioctlId, IntPtr input, IntPtr output) => _ioctl(channelId, ioctlId, input, output);

    /// <summary>SET_CONFIG com uma lista de (parâmetro, valor).</summary>
    public uint SetConfig(uint channelId, params (uint Param, uint Value)[] items)
    {
        // SCONFIG_LIST { ULONG NumOfParams; SCONFIG* ConfigPtr; }  SCONFIG { ULONG Parameter; ULONG Value; }
        IntPtr cfg = Marshal.AllocHGlobal(8 * items.Length);
        IntPtr list = Marshal.AllocHGlobal(4 + IntPtr.Size + 4);
        try
        {
            for (int i = 0; i < items.Length; i++)
            {
                Marshal.WriteInt32(cfg, i * 8, (int)items[i].Param);
                Marshal.WriteInt32(cfg, i * 8 + 4, (int)items[i].Value);
            }
            Marshal.WriteInt32(list, 0, items.Length);
            Marshal.WriteIntPtr(list, IntPtr.Size == 8 ? 8 : 4, cfg); // alinhamento do ponteiro em 64-bit
            return _ioctl(channelId, J2534Const.SET_CONFIG, list, IntPtr.Zero);
        }
        finally { Marshal.FreeHGlobal(list); Marshal.FreeHGlobal(cfg); }
    }

    /// <summary>GET_CONFIG: lê os valores atuais dos parâmetros pedidos (null se o interface recusar).</summary>
    public uint[]? GetConfig(uint channelId, params uint[] parameters)
    {
        IntPtr cfg = Marshal.AllocHGlobal(8 * parameters.Length);
        IntPtr list = Marshal.AllocHGlobal(4 + IntPtr.Size + 4);
        try
        {
            for (int i = 0; i < parameters.Length; i++)
            {
                Marshal.WriteInt32(cfg, i * 8, (int)parameters[i]);
                Marshal.WriteInt32(cfg, i * 8 + 4, 0);
            }
            Marshal.WriteInt32(list, 0, parameters.Length);
            Marshal.WriteIntPtr(list, IntPtr.Size == 8 ? 8 : 4, cfg);
            if (_ioctl(channelId, J2534Const.GET_CONFIG, list, IntPtr.Zero) != J2534Const.STATUS_NOERROR) return null;
            var values = new uint[parameters.Length];
            for (int i = 0; i < parameters.Length; i++) values[i] = (uint)Marshal.ReadInt32(cfg, i * 8 + 4);
            return values;
        }
        finally { Marshal.FreeHGlobal(list); Marshal.FreeHGlobal(cfg); }
    }

    /// <summary>FAST_INIT (ISO14230): envia o StartCommunication (sem checksum) e devolve a resposta da ECU.</summary>
    public uint FastInit(uint channelId, ref PassThruMsg request, out PassThruMsg response)
    {
        int size = Marshal.SizeOf<PassThruMsg>();
        IntPtr inp = Marshal.AllocHGlobal(size), outp = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(request, inp, false);
            var empty = PassThruMsg.Create(request.ProtocolID);
            Marshal.StructureToPtr(empty, outp, false);
            uint r = _ioctl(channelId, J2534Const.FAST_INIT, inp, outp);
            response = Marshal.PtrToStructure<PassThruMsg>(outp);
            return r;
        }
        finally { Marshal.FreeHGlobal(inp); Marshal.FreeHGlobal(outp); }
    }

    /// <summary>FIVE_BAUD_INIT: endereço a 5 baud; devolve os 2 key bytes.</summary>
    public uint FiveBaudInit(uint channelId, byte address, out byte[] keyBytes)
    {
        // SBYTE_ARRAY { ULONG NumOfBytes; UCHAR* BytePtr; }
        int hdr = IntPtr.Size == 8 ? 16 : 8, ptrOff = IntPtr.Size == 8 ? 8 : 4;
        IntPtr inp = Marshal.AllocHGlobal(hdr + 4), outp = Marshal.AllocHGlobal(hdr + 4);
        try
        {
            Marshal.WriteInt32(inp, 0, 1); Marshal.WriteIntPtr(inp, ptrOff, inp + hdr); Marshal.WriteByte(inp, hdr, address);
            Marshal.WriteInt32(outp, 0, 2); Marshal.WriteIntPtr(outp, ptrOff, outp + hdr); Marshal.WriteInt16(outp, hdr, 0);
            uint r = _ioctl(channelId, J2534Const.FIVE_BAUD_INIT, inp, outp);
            keyBytes = new[] { Marshal.ReadByte(outp, hdr), Marshal.ReadByte(outp, hdr + 1) };
            return r;
        }
        finally { Marshal.FreeHGlobal(inp); Marshal.FreeHGlobal(outp); }
    }

    public string GetLastError()
    {
        if (_getLastError == null) return "";
        var buf = new byte[80];
        _getLastError(buf);
        int n = Array.IndexOf(buf, (byte)0); if (n < 0) n = buf.Length;
        return System.Text.Encoding.ASCII.GetString(buf, 0, n);
    }

    /// <summary>Não descarrega a DLL (ver <see cref="Load"/>); fica até o processo terminar.</summary>
    public void Dispose() { }
}
