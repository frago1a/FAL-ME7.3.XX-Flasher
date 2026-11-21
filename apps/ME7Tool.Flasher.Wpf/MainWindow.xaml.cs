using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using ME7Tool.Core.Bsl;
using ME7Tool.Core.Common;
using ME7Tool.Core.Flashing;
using ME7Tool.Core.Kwp2000;
using ME7Tool.Core.Transport;
using ME7Tool.Devices;
using ME7Tool.Devices.J2534;
using ME7Tool.Wpf.Shared;
using static ME7Tool.Core.Common.Lang;
using CoreFlasher = ME7Tool.Core.Flashing.Flasher;

namespace ME7Tool.Flasher.Wpf;

public partial class MainWindow : Window
{
    private ICommunicationDevice? _device;
    private Kwp2000Interface? _kwp;
    private CoreFlasher? _flasher;
    private readonly FlashLayout _layout = FlashLayout.Am29F400B_BottomBoot_512k();
    private readonly object _ioLock = new();
    private CancellationTokenSource? _keepAlive;
    private bool _busy;
    private bool _loadingLang;
    private readonly List<J2534DeviceInfo> _j2534 = new();

    public MainWindow()
    {
        Lang.Load();
        InitializeComponent();
        UiLocalizer.Apply(this);
        _loadingLang = true;
        cmbLang.SelectedIndex = Lang.English ? 1 : 0;
        _loadingLang = false;
        RefreshPorts();
        LoadSavedExaminerFolder();
    }

    private void cmbLang_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingLang || cmbLang.SelectedIndex < 0) return;
        UiLocalizer.SwitchLanguage(this, cmbLang.SelectedIndex == 1, () =>
        {
            if (!_busy) return true;
            MessageBox.Show(this, T("Espera o fim da operação para mudar de língua.", "Wait for the operation to finish before changing the language."),
                "ME7Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
            _loadingLang = true; cmbLang.SelectedIndex = Lang.English ? 1 : 0; _loadingLang = false;
            return false;
        });
    }

    private void btnRefresh_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void btnAbout_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void RefreshPorts()
    {
        string prev = cmbPort.Text;
        cmbPort.Items.Clear();
        foreach (var p in SerialCommunicationDevice.GetPortNames()) cmbPort.Items.Add(p);
        if (!string.IsNullOrEmpty(prev) && cmbPort.Items.Contains(prev)) cmbPort.Text = prev;
        else if (cmbPort.Items.Count > 0) cmbPort.SelectedIndex = 0;

        string prevJ = cmbJ2534.Text;
        cmbJ2534.Items.Clear(); _j2534.Clear();
        try { _j2534.AddRange(J2534Registry.List()); } catch { /* registry indisponível */ }
        foreach (var d in _j2534) cmbJ2534.Items.Add(d.Name);
        cmbJ2534.SelectedIndex = _j2534.Count == 0 ? -1 : Math.Max(0, cmbJ2534.Items.IndexOf(prevJ));
    }

    private void cmbIface_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (cmbJ2534 == null) return; // durante InitializeComponent
        bool j = cmbIface.SelectedIndex == 2;
        cmbPort.Visibility = j ? Visibility.Collapsed : Visibility.Visible;
        cmbJ2534.Visibility = j ? Visibility.Visible : Visibility.Collapsed;
        lblPort.Text = j ? T("Dispositivo:", "Device:") : T("Porta:", "Port:");
    }

    private void Log(string msg)
    {
        var t = DateTime.Now;
        Dispatcher.BeginInvoke(() => { txtLog.AppendText($"{t:HH:mm:ss}  {msg}\n"); txtLog.ScrollToEnd(); });
    }

    private void Status(string s, bool ok = false) => Dispatcher.BeginInvoke(() =>
    {
        lblStatus.Text = s;
        lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(
            ok ? System.Windows.Media.Color.FromRgb(0x3C, 0x9A, 0x3C)
               : System.Windows.Media.Color.FromRgb(0xC0, 0xA0, 0x00));
    });

    private int WorkingBaud() => int.TryParse(cmbBaud.Text.Trim(), out var b) && b > 0 ? b : 10400;

    private ICommunicationDevice CreateDevice()
    {
        switch (cmbIface.SelectedIndex)
        {
            case 1: return new FtdiCommunicationDevice(0);
            case 2:
                if (_j2534.Count == 0 || cmbJ2534.SelectedIndex < 0)
                    throw new InvalidOperationException(T("Nenhum dispositivo J2534 instalado/escolhido.", "No J2534 device installed/selected."));
                var d = _j2534[cmbJ2534.SelectedIndex];
                return new J2534KLineDevice(d.FunctionLibrary, d.Name) { OnLog = Log };
            default: return new SerialCommunicationDevice(cmbPort.Text, 10400);
        }
    }

    // ------------------------------------------------- ligação

    private async void btnConnect_Click(object sender, RoutedEventArgs e)
    {
        Disconnect();
        try
        {
            _device = CreateDevice();
            byte ecu;
            try { ecu = Convert.ToByte(txtEcuAddr.Text.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16); }
            catch { Log(T("Endereço ECU inválido (hex, ex.: 10).", "Invalid ECU address (hex, e.g. 10).")); return; }
            bool onlyEcu = chkOnlyEcu.IsChecked == true;
            var init = cmbInit.SelectedIndex == 1 ? KLineInit.FiveBaud : KLineInit.FastInit;
            _kwp = new Kwp2000Interface(_device, KwpTiming.Download(), ecu)
            {
                OnLog = Log,
                FastInitTargets = onlyEcu ? new[] { ecu } : new[] { ecu, (byte)0x10, (byte)0x01, (byte)0x11 }.Distinct().ToArray(),
                RetryAfterP3Max = true, // ECU ainda na sessão anterior → espera o P3max e tenta outra vez
            };
            var kwp = _kwp;
            _flasher = new CoreFlasher(_kwp, _layout) { OnLog = Log };
            _flasher.Progress += OnProgress;
            Status(T("A ligar...", "Connecting..."));
            await Task.Run(() => { lock (_ioLock) kwp.Connect(init); });
            Status(T($"Ligado (ECU 0x{_kwp.EcuAddress:X2})", $"Connected (ECU 0x{_kwp.EcuAddress:X2})"), ok: true);
            await ReadInfo();
            await Task.Run(() => { lock (_ioLock) ReadBattery(); });
            StartKeepAlive();
            btnDisconnect.IsEnabled = true;
            btnTest.IsEnabled = true;
            UpdatePlan();
        }
        catch (Exception ex) { Status(T("Falha", "Failed")); Log(T($"ERRO ligação: {ex.Message}", $"ERROR connecting: {ex.Message}")); Disconnect(); }
    }

    private void btnDisconnect_Click(object sender, RoutedEventArgs e)
    {
        var kwp = _kwp;
        StopKeepAlive();
        if (kwp != null) Task.Run(() => { lock (_ioLock) kwp.StopCommunication(); }).Wait(2000);
        Disconnect();
        Log(T("Desligado.", "Disconnected."));
    }

    /// <summary>
    /// Lê a bateria no interface (J2534: READ_VBATT) e mostra-a. Chamar com o <c>_ioLock</c> e nunca durante o flash.
    /// null se o interface não souber medir (FTDI/série).
    /// </summary>
    private double? ReadBattery()
    {
        double? v = (_device as IBatteryMonitor)?.ReadBatteryVolts();
        Dispatcher.BeginInvoke(() => ShowBattery(v));
        return v;
    }

    private void ShowBattery(double? v)
    {
        if (v is not double volts) { lblBattery.Visibility = Visibility.Collapsed; return; }
        lblBattery.Visibility = Visibility.Visible;
        lblBattery.Foreground = new System.Windows.Media.SolidColorBrush(volts < 8 ? System.Windows.Media.Color.FromRgb(0xA0, 0xA0, 0xA0)
            : volts < BatteryLevels.FlashMin ? System.Windows.Media.Color.FromRgb(0xFF, 0x7B, 0x72)
            : System.Windows.Media.Color.FromRgb(0x3C, 0xC0, 0x5C));
        lblBattery.Text = volts < 8 ? T($"🔌 {volts:0.0} V (sem 12 V na ficha OBD?)", $"🔌 {volts:0.0} V (no 12 V on the OBD plug?)")
                        : $"🔋 {volts:0.0} V" + (volts < BatteryLevels.FlashMin ? T(" fraca para programar", " too low to program") : "");
    }

    private void Disconnect()
    {
        StopKeepAlive();
        lblBattery.Visibility = Visibility.Collapsed;
        try { _device?.Close(); _device?.Dispose(); } catch { }
        _device = null; _kwp = null; _flasher = null; _ecuIdent = null;
        btnDisconnect.IsEnabled = false; btnTest.IsEnabled = false; btnFlash.IsEnabled = false;
        Status(T("Desligado", "Disconnected"));
        UpdateMatch();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy && MessageBox.Show(
                T("Há um flash a decorrer. Fechar agora deixa a ECU por programar (recuperável em modo boot). Fechar mesmo?",
                  "A flash is in progress. Closing now leaves the ECU unprogrammed (recoverable in boot mode). Close anyway?"),
                T("Flash a decorrer", "Flash in progress"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        { e.Cancel = true; return; }
        // Termina a comunicação de forma ordeira (senão a ECU fica na sessão e ignora o fast-init seguinte).
        StopKeepAlive();
        if (!_busy && _kwp is { } kwp)
            try { Task.Run(() => { lock (_ioLock) kwp.StopCommunication(); }).Wait(1500); } catch { }
        Disconnect();
    }

    /// <summary>TesterPresent de 2 em 2 s enquanto ligado e parado — a ECU fecha a sessão após ~5 s sem mensagens.</summary>
    private void StartKeepAlive()
    {
        StopKeepAlive();
        _keepAlive = new CancellationTokenSource();
        var tok = _keepAlive.Token;
        Task.Run(async () =>
        {
            while (!tok.IsCancellationRequested)
            {
                try { await Task.Delay(2000, tok); } catch { break; }
                if (_busy) continue;
                try { lock (_ioLock) { _kwp?.TesterPresent(true); ReadBattery(); } }
                catch (Exception ex)
                {
                    Log(T($"Keep-alive parou: {ex.Message}", $"Keep-alive stopped: {ex.Message}"));
                    Status(T("Ligação perdida", "Connection lost"));
                    break;
                }
            }
        });
    }

    private void StopKeepAlive() { try { _keepAlive?.Cancel(); } catch { } _keepAlive = null; }

    private async Task ReadInfo()
    {
        var kwp = _kwp;
        if (kwp == null) return;
        var sb = new System.Text.StringBuilder();
        var got = new Dictionary<EcuIdentificationOption, byte[]>();
        // P3min normal do KWP2000 (55 ms) entre os pedidos de identificação: o perfil Download usa 1 ms e a ME3.1, com
        // o pedido seguinte logo atrás da resposta, devolvia às vezes a resposta anterior (SW = nº de hardware)
        bool prevEnforce = kwp.EnforceP3Min;
        int prevP3 = kwp.Timing.P3Min;
        kwp.EnforceP3Min = true;
        kwp.Timing.P3Min = Math.Max(prevP3, 55);
        try
        {
        foreach (var (opt, label) in new (EcuIdentificationOption, string)[]
        {
            (EcuIdentificationOption.FiatDrawingNumber, "Drawing"),
            (EcuIdentificationOption.EcuHardwareNumber, "Hardware"),
            (EcuIdentificationOption.EcuSoftwareNumber, "Software"),
            (EcuIdentificationOption.EcuSoftwareVersion, T("Versão SW", "SW version")),
            (EcuIdentificationOption.TesterCode, "Tester"),
            (EcuIdentificationOption.ProgrammingDate, T("Data prog.", "Prog. date")),
        })
        {
            try
            {
                var d = await Task.Run(() => { lock (_ioLock) return kwp.ReadEcuIdentification(opt); });
                got[opt] = d;
                sb.AppendLine($"{label}: {(opt == EcuIdentificationOption.EcuSoftwareVersion ? Convert.ToHexString(d) : Ascii(d))}");
            }
            catch (Exception ex) { sb.AppendLine(T($"{label}: (erro {ex.Message})", $"{label}: (error {ex.Message})")); }
        }
        }
        finally
        {
            kwp.EnforceP3Min = prevEnforce;
            kwp.Timing.P3Min = prevP3;
        }
        lblInfo.Text = sb.ToString();

        // identificação para procurar o original no Examiner (só com hardware e software legíveis)
        string hw = got.TryGetValue(EcuIdentificationOption.EcuHardwareNumber, out var h) ? Ascii(h) : "";
        string sw = got.TryGetValue(EcuIdentificationOption.EcuSoftwareNumber, out var s) ? Ascii(s) : "";
        _ecuIdent = hw.Length > 0 && sw.Length > 0
            ? new EcuIdent(hw, sw, got.GetValueOrDefault(EcuIdentificationOption.EcuSoftwareVersion),
                           got.TryGetValue(EcuIdentificationOption.FiatDrawingNumber, out var f) ? Ascii(f) : null)
            : null;
        UpdateMatch();
    }

    // ------------------------------------------------- originais do Examiner

    private EcuIdent? _ecuIdent;
    private ExaminerLibrary? _examiner;
    private ExaminerMatch? _match;

    /// <summary>Pasta do Examiner lembrada entre arranques.</summary>
    private static string ExaminerSettingFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ME7Tool", "flasher_examiner.txt");

    private void LoadSavedExaminerFolder()
    {
        try
        {
            if (File.Exists(ExaminerSettingFile) && File.ReadAllText(ExaminerSettingFile).Trim() is { Length: > 0 } dir && Directory.Exists(dir))
                LoadExaminer(dir, save: false);
        }
        catch { /* sem pasta lembrada */ }
    }

    private void btnExaminerFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = T("Pasta do Examiner (com o Global.ndb)", "Examiner folder (with Global.ndb)"),
            InitialDirectory = _examiner?.Folder ?? "",
        };
        if (dlg.ShowDialog() == true) LoadExaminer(dlg.FolderName, save: true);
    }

    private void LoadExaminer(string folder, bool save)
    {
        try
        {
            _examiner = ExaminerLibrary.Load(folder);
            int me7 = _examiner.Records.Select(r => r.File).Distinct().Count(n => _examiner.FindFile(n) != null);
            lblExaminer.Text = _examiner.HasDatabase
                ? T($"{folder}\n{_examiner.Records.Count} registos no Global.ndb, {me7} ficheiros presentes.",
                    $"{folder}\n{_examiner.Records.Count} records in Global.ndb, {me7} files present.")
                : T($"{folder}\nSem Global.ndb: procura pela identificação dentro de cada ficheiro.",
                    $"{folder}\nNo Global.ndb: searches the identification inside each file.");
            Log(T($"Examiner: {folder} ({_examiner.Records.Count} registos).", $"Examiner: {folder} ({_examiner.Records.Count} records)."));
            if (save)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ExaminerSettingFile)!);
                File.WriteAllText(ExaminerSettingFile, folder);
            }
        }
        catch (Exception ex)
        {
            _examiner = null;
            lblExaminer.Text = T($"Erro a ler a pasta: {ex.Message}", $"Error reading the folder: {ex.Message}");
        }
        UpdateMatch();
    }

    /// <summary>Procura a ECU ligada na pasta do Examiner e mostra o resultado.</summary>
    private void UpdateMatch()
    {
        _match = null;
        btnUseOriginal.IsEnabled = false;
        btnSearchOriginal.IsEnabled = _examiner != null;
        if (_examiner == null) { lblMatch.Text = ""; return; }
        var ident = MatchIdent;
        if (ident == null)
        {
            lblMatch.Text = BslMode ? T("\"Ligar e identificar\" em modo boot para procurar a ECU pela identificação.", "\"Connect and identify\" in boot mode to look the ECU up by its identification.")
                : _kwp == null ? T("Liga à ECU para a procurar pela identificação.", "Connect to the ECU to look it up by its identification.")
                               : T("A ECU não deu o nº Bosch e o software (0x1A): não dá para procurar.", "The ECU did not give its Bosch number and software (0x1A): cannot look it up.");
            return;
        }
        try { _match = _examiner.Match(ident); }
        catch (Exception ex) { lblMatch.Text = T($"Erro na procura: {ex.Message}", $"Search error: {ex.Message}"); return; }
        lblMatch.Text = _match.Message;
        lblMatch.Foreground = new System.Windows.Media.SolidColorBrush(_match.Kind switch
        {
            ExaminerMatchKind.UpToDate => System.Windows.Media.Color.FromRgb(0x3C, 0x9A, 0x3C),
            ExaminerMatchKind.UpdateAvailable => System.Windows.Media.Color.FromRgb(0x50, 0xA0, 0xE0),
            _ => System.Windows.Media.Color.FromRgb(0xC0, 0xA0, 0x00),
        });
        btnUseOriginal.IsEnabled = _match.Image != null;
        Log("Examiner: " + _match.Message);
    }

    /// <summary>Pesquisa manual (sem ECU): parte do nº Bosch da ECU ligada, se houver.</summary>
    private void btnSearchOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (_examiner == null) return;
        new ExaminerSearchWindow(_examiner, path =>
        {
            FileToWriteBox.Text = path;
            Log(T($"Ficheiro a gravar: original do Examiner {Path.GetFileName(path)} (pesquisa manual).",
                  $"File to write: Examiner original {Path.GetFileName(path)} (manual search)."));
        }, MatchIdent?.Hardware) { Owner = this }.ShowDialog();
    }

    /// <summary>Identificação usada para procurar no Examiner: a lida por OBD ou, em modo BSL, a do bloco da flash.</summary>
    private EcuIdent? MatchIdent => BslMode ? _bslIdent : _ecuIdent;

    /// <summary>Ficheiro a gravar do modo atual (OBD ou BSL).</summary>
    private System.Windows.Controls.TextBox FileToWriteBox => BslMode ? txtBslFile : txtNew;

    private EcuIdent? _bslIdent;

    private static bool SameIdent(EcuIdent? a, EcuIdent? b) => a == null ? b == null : b != null && SameSoftware(a, b);

    private void btnUseOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (_match?.Image is not { } img) return;
        FileToWriteBox.Text = img.Path; // o LoadImage reconhece o original cifrado e desencripta-o em memória
        Log(T($"Ficheiro a gravar: original do Examiner {Path.GetFileName(img.Path)} ({img.Ident}).",
              $"File to write: Examiner original {Path.GetFileName(img.Path)} ({img.Ident})."));
    }

    private static string Ascii(byte[] d)
    {
        bool p = d.Length > 0 && d.All(c => c is >= 0x20 and < 0x7F);
        return p ? System.Text.Encoding.ASCII.GetString(d).Trim() : Convert.ToHexString(d);
    }

    // ------------------------------------------------- ficheiro e plano

    private void btnBrowseNew_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = T("Binário ECU (*.bin;*.ori)|*.bin;*.ori|Todos|*.*", "ECU binary (*.bin;*.ori)|*.bin;*.ori|All|*.*") };
        if (dlg.ShowDialog() == true) txtNew.Text = dlg.FileName;
    }

    private void txtFiles_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdatePlan();

    /// <summary>Lê e valida o ficheiro a gravar (null + motivo se não serve).</summary>
    private byte[]? LoadImage(out string? error) => LoadImage(out error, out _, out _);

    /// <summary>
    /// Imagem de 512 KB a gravar, mais o perfil (ME7.3.1/ME3.1). Aceita um original do Examiner (cifrado, com
    /// .Prm — 512 KB ME7.3.1 ou 496 KB ME3.1, desencriptado em memória e confirmado pelo CRC) ou um ficheiro
    /// próprio já desencriptado de 512 KB.
    /// </summary>
    private byte[]? LoadImage(out string? error, out FiatFlashProfile profile, out bool examinerOriginal)
        => LoadImage(txtNew.Text, out error, out profile, out examinerOriginal);

    private byte[]? LoadImage(string path, out string? error, out FiatFlashProfile profile, out bool examinerOriginal)
    {
        error = null;
        examinerOriginal = false;
        profile = FiatFlashProfile.Me731;
        if (!File.Exists(path)) { error = T("Escolhe o ficheiro a gravar.", "Choose the file to write."); return null; }

        // 1) original do Examiner (tem .Prm e o CRC confirma): decide o perfil e desencripta em memória
        if (ExaminerLibrary.OpenImage(path, out string? openWhy) is { } orig)
        {
            examinerOriginal = true;
            profile = orig.Profile;
            return orig.Image;
        }

        // 2) ficheiro próprio: 512 KB já desencriptados
        var img = File.ReadAllBytes(path);
        if (img.Length != _layout.TotalSize)
        {
            error = openWhy != null
                ? T($"Não dá para usar este ficheiro: {openWhy}.", $"Cannot use this file: {openWhy}.")
                : T($"O ficheiro tem {img.Length} bytes; esperado {_layout.TotalSize} (512 KB) ou um original do Examiner com o .Prm.",
                    $"The file has {img.Length} bytes; expected {_layout.TotalSize} (512 KB) or an Examiner original with its .Prm.");
            return null;
        }
        if (FiatFullFlash.Identify(img) == null && FiatFullFlash.Identify(FiatCipher.DecryptAll(img)) != null)
        {
            error = T("Este ficheiro está CIFRADO (Examiner) e não deu para o confirmar (falta o .Prm ao lado). Desencripta-o primeiro (botão à esquerda) e grava o _dec.bin.",
                      "This file is ENCRYPTED (Examiner) and could not be confirmed (its .Prm is missing). Decrypt it first (button on the left) and write the _dec.bin.");
            return null;
        }
        profile = FiatFlashProfile.Detect(img) ?? FiatFlashProfile.Me731;
        return img;
    }

    /// <summary>Linhas do plano que comparam a identificação do ficheiro com a da ECU ligada.</summary>
    private IEnumerable<string> CompareWithEcu(byte[] img, FiatFlashProfile profile, bool examinerOriginal)
    {
        var file = EcuIdent.FromImage(img);
        yield return examinerOriginal
            ? T($"Origem: original do Examiner ({Path.GetFileName(txtNew.Text)}) · perfil {profile.Name}, desencriptado em memória, CRC do .Prm confirmado",
                $"Source: Examiner original ({Path.GetFileName(txtNew.Text)}) · {profile.Name} profile, decrypted in memory, .Prm CRC confirmed")
            : T($"Origem: ficheiro próprio · perfil {profile.Name}", $"Source: own file · {profile.Name} profile");
        if (file == null) { yield return T("⚠ O ficheiro não tem bloco de identificação Bosch.", "⚠ The file has no Bosch identification block."); yield break; }
        yield return T($"Ficheiro: {file}", $"File: {file}");
        if (_ecuIdent == null) yield break;
        yield return T($"ECU:      {_ecuIdent}", $"ECU:      {_ecuIdent}");
        if (!file.SameHardware(_ecuIdent))
            yield return T($"⚠ HARDWARE DIFERENTE: o ficheiro é para o {file.Hardware}, a ECU é {_ecuIdent.Hardware}.",
                           $"⚠ DIFFERENT HARDWARE: the file is for {file.Hardware}, the ECU is {_ecuIdent.Hardware}.");
        else if (file.SameSoftware(_ecuIdent))
            yield return T("ⓘ A ECU já tem este número de software.", "ⓘ The ECU already has this software number.");
    }

    /// <summary>true se a ECU ligada tem outro hardware que o ficheiro.</summary>
    private bool HardwareMismatch(byte[] img) =>
        _ecuIdent != null && EcuIdent.FromImage(img) is { } f && !f.SameHardware(_ecuIdent);

    /// <summary>Chave do programador da Fiat: lida do ficheiro (é a mesma na Thesis, Stilo e Alfa) ou a conhecida.</summary>
    private static byte[] FiatKey(byte[]? image, out string source)
    {
        if (image != null && FiatFullFlash.FindKey(image) is byte[] k)
        {
            source = T("lida do programador do ficheiro", "read from the file's programmer");
            return k;
        }
        source = T("chave conhecida da Thesis/Stilo", "known Thesis/Stilo key");
        return FiatCipher.DefaultKey;
    }

    private void UpdatePlan()
    {
        if (lstDiff == null) return;
        lstDiff.Items.Clear();
        var img = LoadImage(out var err, out var prof, out bool original);
        if (img == null) { lblPlan.Text = T("Plano:", "Plan:"); if (err != null) lstDiff.Items.Add(err); btnFlash.IsEnabled = false; return; }

        var key = FiatKey(img, out var keySrc);
        var prepared = prof.Prepare(img);
        ushort cs = prof.ChecksumOf(prepared);
        string a0 = $"{prof.EraseStart:X6}".Insert(4, " ").Insert(2, " "); // "80 00 00"
        string a1 = $"{prof.EraseEnd:X6}".Insert(4, " ").Insert(2, " ");
        string sz = $"{prepared.Length:X6}".Insert(4, " ").Insert(2, " ");
        lblPlan.Text = T($"Plano: flash {prof.Name}, {prepared.Length / 1024} KB cifrados em 0x{prof.DownloadStart:X6} · estimativa {Estimate(prepared.Length)}",
                         $"Plan: {prof.Name} flash, {prepared.Length / 1024} KB encrypted at 0x{prof.DownloadStart:X6} · estimate {Estimate(prepared.Length)}");
        foreach (var line in CompareWithEcu(img, prof, original)) lstDiff.Items.Add(line);
        lstDiff.Items.Add(T("1. tester code + data de programação (a ECU exige-os)", "1. tester code + programming date (required by the ECU)"));
        lstDiff.Items.Add(prof.HasBackupZone
            ? T($"2. 31 02 {a0} {a1}  apagar tudo (dados, código, arranque)", $"2. 31 02 {a0} {a1}  erase everything (data, code, boot)")
            : T($"2. 31 02 {a0} {a1}  apagar (o arranque/programador fica intacto)", $"2. 31 02 {a0} {a1}  erase (boot/programmer stays intact)"));
        lstDiff.Items.Add(T($"3. 34 {a0} 01 {sz}  download único, cifrado", $"3. 34 {a0} 01 {sz}  single encrypted download"));
        lstDiff.Items.Add(T($"4. 31 01 {a0} {a1} {cs >> 8:X2} {cs & 0xFF:X2}  verificação (CRC-32 {(prof.Checksum == CrcHalf.High ? ">> 16" : "& FFFF")})",
                            $"4. 31 01 {a0} {a1} {cs >> 8:X2} {cs & 0xFF:X2}  verification (CRC-32 {(prof.Checksum == CrcHalf.High ? ">> 16" : "& FFFF")})"));
        lstDiff.Items.Add(T($"ⓘ Chave: {Convert.ToHexString(key)} ({keySrc})", $"ⓘ Key: {Convert.ToHexString(key)} ({keySrc})"));
        if (prof.HasBackupZone)
            lstDiff.Items.Add(T("ⓘ Zona 0x38000–0x3FFFF enviada em branco, como o Examiner (a ECU gere o backup).",
                                "ⓘ Area 0x38000–0x3FFFF sent blank, like the Examiner (the ECU manages the backup)."));
        btnFlash.IsEnabled = _kwp != null && !_busy;
    }

    /// <summary>Estimativa grosseira: 10 bits/byte ao baud de trabalho + ~15% de protocolo + apagar.</summary>
    private string Estimate(long bytes)
    {
        double s = bytes * 10.0 / WorkingBaud() * 1.15 + bytes / 65536.0 * 2;
        return s < 90 ? $"~{s:0} s" : $"~{s / 60:0.0} min";
    }

    // ------------------------------------------------- ações

    private static readonly System.Windows.Media.Brush ProgGreen = Frozen(0x3C, 0x9A, 0x3C), ProgRed = Frozen(0xC8, 0x3C, 0x3C);

    private static System.Windows.Media.Brush Frozen(byte r, byte g, byte b)
    {
        var br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    /// <summary>Barra vermelha enquanto se apaga/escreve na ECU, verde no resto.</summary>
    private void ProgWriting(bool writing) => prog.Foreground = writing ? ProgRed : ProgGreen;

    private void OnProgress(FlashProgress p) => Dispatcher.BeginInvoke(() =>
    {
        string phase = p.Phase switch
        {
            FlashPhase.Connecting => T("Sessão", "Session"),
            FlashPhase.Unlocking => "Seed/key",
            FlashPhase.Erasing => T("A apagar", "Erasing"),
            FlashPhase.Writing => T("A escrever", "Writing"),
            FlashPhase.Verifying => T("A verificar", "Verifying"),
            FlashPhase.Finalizing => T("A finalizar", "Finalizing"),
            _ => T("Concluído", "Done"),
        };
        if (p.Phase is FlashPhase.Erasing or FlashPhase.Writing) ProgWriting(true);   // fica vermelha até à operação seguinte
        if (p.Total > 0) prog.Value = p.Percent;
        lblProg.Text = $"{phase} {(p.Total > 0 ? $"{p.Percent:0.0}%" : "")} — {p.Message}";
        if (p.Phase != FlashPhase.Writing) Log(p.Message);
    });

    private void ApplyOptions(CoreFlasher f)
    {
        f.Options.WorkingBaud = WorkingBaud();
        f.Options.Me31 = cmbEcuType?.SelectedIndex == 1;   // ME3.1: seed 0x55 + esquema de baud próprio, sem tentativas
        f.Options.KeepEcuIdentification = chkKeepIdent.IsChecked == true;
        f.Options.DiagnoseOnHandoverFailure = false; // versão final: sem diagnóstico automático
    }

    private bool _modeRevert;

    /// <summary>Modo escolhido em cima: OBD (ficha de diagnóstico) ou BSL (modo boot do C167).</summary>
    private bool BslMode => cmbMode?.SelectedIndex == 1;

    /// <summary>
    /// Troca entre OBD e BSL: a janela mostra só o que serve ao modo. Para passar a BSL, a ligação OBD é desligada
    /// (o modo boot usa o cabo diretamente). Não troca a meio de uma operação.
    /// </summary>
    private void cmbMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (grpBsl == null || _modeRevert) return;   // ainda a construir a janela
        if (_busy)
        {
            _modeRevert = true;
            cmbMode.SelectedIndex = BslMode ? 0 : 1;
            _modeRevert = false;
            Log(T("Espera que a operação acabe antes de mudar de modo.", "Wait for the operation to finish before changing mode."));
            return;
        }
        if (BslMode && _kwp is { } kwp)
        {
            StopKeepAlive();
            try { Task.Run(() => { lock (_ioLock) kwp.StopCommunication(); }).Wait(2000); } catch { }
            Disconnect();
            Log(T("Ligação OBD desligada para o modo boot.", "OBD link disconnected for boot mode."));
        }
        ApplyMode();
    }

    private void ApplyMode()
    {
        bool bsl = BslMode;
        var obd = bsl ? Visibility.Collapsed : Visibility.Visible;
        pnlObdBar.Visibility = grpObd.Visibility = btnTest.Visibility = obd;
        grpBsl.Visibility = grpBslOptions.Visibility = bsl ? Visibility.Visible : Visibility.Collapsed;
        UpdateMatch();   // o grupo do Examiner serve aos dois modos, com a identificação de cada um
        if (_kwp == null)   // o grupo ECU serve aos dois modos; sem ligação, diz como identificar
            lblInfo.Text = bsl ? T("(\"Ligar e identificar\" em modo boot)", "(\"Connect and identify\" in boot mode)")
                               : T("(ligar para ler a identificação)", "(connect to read the identification)");
        if (cmbIface.Items[2] is System.Windows.Controls.ComboBoxItem j2534) j2534.IsEnabled = !bsl;   // o J2534 não faz modo boot
        if (bsl && cmbIface.SelectedIndex == 2) cmbIface.SelectedIndex = 1;
        prog.Value = 0; lblProg.Text = "";
    }

    /// <summary>
    /// Ao escolher a família da ECU, repõe o baud recomendado: 124800 nas duas (a ECU fica a 125000 reais; na ME3.1
    /// o Core pede-o como 125000 exato). 115200 é recusado pelas duas.
    /// </summary>
    private void cmbEcuType_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (cmbBaud == null) return;
        cmbBaud.Text = "124800";
    }

    private void chkKeepIdent_Changed(object sender, RoutedEventArgs e)
    {
        if (pnlIdent != null) pnlIdent.IsEnabled = chkKeepIdent.IsChecked != true;
    }

    private async void btnTest_Click(object sender, RoutedEventArgs e)
    {
        var flasher = _flasher;
        if (flasher == null) return;
        ApplyOptions(flasher);
        await RunBusy(T("Teste de acesso", "Access test"), () => flasher.TestProgrammingAccess());
    }

    private async void btnFlash_Click(object sender, RoutedEventArgs e)
    {
        var flasher = _flasher;
        var img = LoadImage(out var err, out var prof, out _);
        if (flasher == null || img == null) { Log(err ?? T("Liga primeiro.", "Connect first.")); return; }

        DateTime date = (chkToday.IsChecked == true ? DateTime.Today : dateCustom.SelectedDate) ?? DateTime.Today;
        string? tester = string.IsNullOrWhiteSpace(txtTester.Text) ? null : txtTester.Text.Trim();
        var key = FiatKey(img, out var keySrc);
        int kb = prof.PayloadSize / 1024;
        bool keep = chkKeepIdent.IsChecked == true;
        string ident = keep
            ? T("Tester code e data: mantém os que estão na ECU.\n", "Tester code and date: keeps the ones already in the ECU.\n")
            : T($"Tester code {(tester != null ? $"\"{tester}\"" : "(mantém o atual)")} e data {date:yyyy-MM-dd}.\n",
                $"Tester code {(tester != null ? $"\"{tester}\"" : "(keeps the current one)")} and date {date:yyyy-MM-dd}.\n");

        string fileId = EcuIdent.FromImage(img)?.ToString() ?? FiatFullFlash.Identify(img) ?? "";
        string hwWarn = HardwareMismatch(img)
            ? T($"⚠⚠ HARDWARE DIFERENTE: o ficheiro NÃO é para esta ECU (ECU {_ecuIdent!.Hardware}). Só continua se sabes o que estás a fazer.\n\n",
                $"⚠⚠ DIFFERENT HARDWARE: the file is NOT for this ECU (ECU {_ecuIdent!.Hardware}). Only continue if you know what you are doing.\n\n")
            : "";
        string eraseDesc = prof.HasBackupZone
            ? T("A ECU vai APAGAR A FLASH TODA (incluindo o arranque) e escrever o ficheiro inteiro.\n",
                "The ECU will ERASE THE WHOLE FLASH (including the boot) and write the entire file.\n")
            : T($"A ECU vai APAGAR 0x{prof.EraseStart:X6}–0x{prof.EraseEnd:X6} (o arranque/programador fica intacto) e escrever {kb} KB.\n",
                $"The ECU will ERASE 0x{prof.EraseStart:X6}–0x{prof.EraseEnd:X6} (boot/programmer stays intact) and write {kb} KB.\n");
        // O ficheiro diz a família (bloco de identificação): o seletor de ECU acompanha-o. Com a família errada a
        // sessão falhava logo (antes de apagar) — o pedido de baud de uma é lido de outra forma pela outra.
        int wantFamily = prof == FiatFlashProfile.Me31 ? 1 : 0;
        if (cmbEcuType.SelectedIndex != wantFamily)
        {
            cmbEcuType.SelectedIndex = wantFamily;
            Log(T($"ECU ajustada para {(wantFamily == 1 ? "ME3.1" : "ME7.3.xx")}, de acordo com o ficheiro.",
                  $"ECU set to {(wantFamily == 1 ? "ME3.1" : "ME7.3.xx")} to match the file."));
        }
        const string expWarn = "";
        double? vbat = null;
        if (_device is IBatteryMonitor)
            vbat = await Task.Run(() => { lock (_ioLock) return ReadBattery(); });
        string batWarn = vbat is double vb && vb >= 8 && vb < BatteryLevels.FlashMin
            ? T($"⚠⚠ BATERIA FRACA: {vb:0.0} V. Um flash demora minutos e a tensão desce — liga um carregador antes de continuar.\n\n",
                $"⚠⚠ LOW BATTERY: {vb:0.0} V. A flash takes minutes and the voltage drops — connect a charger before continuing.\n\n")
            : vbat is double vb2 && vb2 < 8
            ? T($"⚠ O interface mede só {vb2:0.0} V no pino 16: confirma que está ligado à ficha OBD e com a ignição ligada.\n\n",
                $"⚠ The interface only measures {vb2:0.0} V on pin 16: check it is plugged into the OBD socket with the ignition on.\n\n")
            : "";
        string batInfo = vbat is double vb3 && vb3 >= BatteryLevels.FlashMin ? T($"Bateria: {vb3:0.0} V.\n", $"Battery: {vb3:0.0} V.\n") : "";
        var ok = MessageBox.Show(
            batWarn + hwWarn + expWarn +
            T($"FLASH {prof.Name} (OBD, cifrado): {Path.GetFileName(txtNew.Text)}\n{fileId}\n{kb} KB, estimativa {Estimate(prof.PayloadSize)}.\n\n",
              $"{prof.Name} FLASH (OBD, encrypted): {Path.GetFileName(txtNew.Text)}\n{fileId}\n{kb} KB, estimate {Estimate(prof.PayloadSize)}.\n\n") +
            ident + batInfo +
            T($"Chave: {Convert.ToHexString(key)} ({keySrc}).\n\n", $"Key: {Convert.ToHexString(key)} ({keySrc}).\n\n") +
            eraseDesc +
            T("Se falhar depois de apagar, a ECU fica sem aplicação e só recupera por BSL.\n\n" +
              "Requisito: imobilizador a autorizar (chave reconhecida) ou centralina virgem.\n\n" +
              "Alimentação estável, não desligar nada. Continuar?",
              "If it fails after erasing, the ECU is left without an application and can only be recovered by BSL.\n\n" +
              "Requirement: immobilizer authorizing (key recognized) or virgin ECU.\n\n" +
              "Stable power supply, don't disconnect anything. Continue?"),
            T("Confirmar flash", "Confirm flash"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.Yes) return;

        ApplyOptions(flasher);
        bool done = await RunBusy("Flash", () => flasher.ProgramFiatFull(img, key, date, tester, profile: prof));
        if (done)
        {
            // A ECU vai reiniciar com o software novo (power cycle): sem keep-alive nem ligação a fingir que está viva.
            StopKeepAlive();
            Disconnect();
            Log(T("Desligado depois do flash: religa a ignição e liga de novo para ler a ECU.",
                  "Disconnected after the flash: cycle the ignition and connect again to read the ECU."));
        }
        if (done)
            MessageBox.Show(T("Flash concluído e checksum confirmada.\nDesliga e volta a ligar a ignição para a ECU arrancar o software novo.",
                              "Flash complete and checksum confirmed.\nTurn the ignition off and on again so the ECU starts the new software."),
                "OK", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// Modo boot (BSL): lê a flash externa, a ROM interna ou a EEPROM 95080 e grava num ficheiro. Usa o cabo
    /// diretamente (FTDI ou série), por isso a ligação OBD tem de estar desligada. Só lê.
    /// </summary>
    private async void btnBslRead_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_kwp != null) { Log(T("Desliga primeiro a ligação OBD: o modo boot usa o cabo diretamente.", "Disconnect the OBD link first: boot mode uses the cable directly.")); return; }
        if (cmbIface.SelectedIndex == 2) { Log(T("O modo boot precisa de um cabo KKL (FTDI ou Série), não de um J2534.", "Boot mode needs a KKL cable (FTDI or Serial), not a J2534.")); return; }

        string kind = sender == btnBslFlash ? "flash" : sender == btnBslIrom ? "irom" : sender == btnBslEeprom ? "eeprom"
                    : sender == btnBslBackup ? "backup" : "ident";
        int baud = int.TryParse(cmbBslBaud.Text.Trim(), out var b) && b > 0 ? b : 125000;

        // para onde gravar (antes de ligar): um ficheiro, ou uma pasta no backup completo
        string? target = null;
        if (kind is "flash" or "irom" or "eeprom")
        {
            var save = new SaveFileDialog
            {
                Title = T($"Guardar a {BslLabel(kind)} lida por modo boot", $"Save the {BslLabel(kind)} read in boot mode"),
                Filter = T("Binário (*.bin)|*.bin|Todos|*.*", "Binary (*.bin)|*.bin|All|*.*"),
                FileName = $"{kind}_bsl.bin",
            };
            if (save.ShowDialog() != true) return;
            target = save.FileName;
        }
        else if (kind == "backup")
        {
            var folder = new OpenFolderDialog { Title = T("Pasta para o backup completo (flash, ROM interna, EEPROM)", "Folder for the full backup (flash, internal ROM, EEPROM)") };
            if (folder.ShowDialog() != true) return;
            target = folder.FolderName;
        }

        ICommunicationDevice devCreated;
        try { devCreated = CreateDevice(); }   // lê os controlos da janela: tem de ser nesta thread
        catch (Exception ex) { Log(T($"ERRO: {ex.Message}", $"ERROR: {ex.Message}")); return; }

        var parts = new List<(string Kind, byte[] Data)>();
        string? eepromError = null;
        BslRead.BslIdentity? identity = null;
        string title = kind switch
        {
            "ident" => T("Identificação (BSL)", "Identification (BSL)"),
            "backup" => T("Backup completo (BSL)", "Full backup (BSL)"),
            _ => T($"Leitura da {BslLabel(kind)} (BSL)", $"{BslLabel(kind)} read (BSL)"),
        };
        bool ok = await RunBusy(title, () =>
        {
            using var dev = devCreated;
            dev.Open();
            var bsl = new C167Bsl(dev) { OnLog = Log };
            Log(T($"Modo boot a {baud} baud: a ligar…", $"Boot mode at {baud} baud: connecting…"));
            bsl.Connect(baud);
            if (bsl.CpuVariant is byte cpu) _bslCpu = cpu;

            // identificar sempre primeiro (lê só o bloco de identificação, ~0,2 s)
            identity = BslRead.Identify(bsl);
            var id = identity;
            Dispatcher.BeginInvoke(() => ShowBslIdentity(id));
            Log(id.Ident is { } ei
                ? T($"Identificação: {ei} · família {id.Family}.", $"Identification: {ei} · family {id.Family}.")
                : T("Identificação: bloco não encontrado na flash (vazia ou de outra família?).", "Identification: block not found in the flash (blank or another family?)."));
            if (kind == "ident") return;

            foreach (var part in kind == "backup" ? new[] { "flash", "irom", "eeprom" } : new[] { kind })
            {
                string label = BslLabel(part);
                void Progress(int done, int total) => Dispatcher.BeginInvoke(() =>
                {
                    prog.Value = 100.0 * done / total;
                    lblProg.Text = $"BSL: {label} {done / 1024}/{total / 1024} KB";
                });
                try
                {
                    parts.Add((part, part switch
                    {
                        "flash" => BslRead.ReadExternalFlash(bsl, progress: Progress),
                        "irom" => BslRead.ReadInternalRom(bsl, progress: Progress),
                        _ => BslRead.ReadSpiEeprom(bsl, progress: Progress),
                    }));
                    Log(T($"{label}: lida.", $"{label}: read."));
                }
                catch (BslException ex) when (kind == "backup" && part == "eeprom")
                {
                    eepromError = ex.Message;   // no backup, uma EEPROM que falha não deita fora a flash e a ROM já lidas
                    Log(T($"ERRO EEPROM: {ex.Message}", $"ERROR EEPROM: {ex.Message}"));
                }
            }
        });
        if (!ok || target == null) return;

        if (kind == "backup")
        {
            string prefix = BslFilePrefix(identity);
            foreach (var (part, data) in parts)
            {
                string path = Path.Combine(target, $"{prefix}_{part}.bin");
                File.WriteAllBytes(path, data);
                Log(T($"{BslLabel(part)}: {data.Length / 1024} KB → {path}", $"{BslLabel(part)}: {data.Length / 1024} KB → {path}"));
            }
            string info = Path.Combine(target, $"{prefix}_info.txt");
            File.WriteAllText(info, BslInfoText(identity, baud, parts, eepromError));
            Log(T($"Backup completo em {target}{(eepromError != null ? " (sem a EEPROM)" : "")}.", $"Full backup in {target}{(eepromError != null ? " (without the EEPROM)" : "")}."));
        }
        else if (parts.Count == 1)
        {
            File.WriteAllBytes(target, parts[0].Data);
            Log(T($"{BslLabel(kind)}: {parts[0].Data.Length / 1024} KB gravados em {target}.", $"{BslLabel(kind)}: {parts[0].Data.Length / 1024} KB saved to {target}."));
        }
    }

    private void btnBrowseBsl_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = T("Binário ECU (*.bin;*.ori)|*.bin;*.ori|Todos|*.*", "ECU binary (*.bin;*.ori)|*.bin;*.ori|All|*.*") };
        if (dlg.ShowDialog() == true) txtBslFile.Text = dlg.FileName;
    }

    /// <summary>Escolha do modelo no desenho das ligações do modo boot.</summary>
    private void rbWire_Checked(object sender, RoutedEventArgs e)
    {
        if (bslDiagram == null) return;   // ainda a construir a janela
        bslDiagram.Model = rbWireH4.IsChecked == true ? BslWiringDiagram.EcuModel.Me73H4 : BslWiringDiagram.EcuModel.Me731Me31;
    }

    private void btnBrowseBslRef_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = T("Leitura do que está agora na ECU (512 KB)", "Read of what is in the ECU now (512 KB)"),
            Filter = T("Binário ECU (*.bin;*.ori)|*.bin;*.ori|Todos|*.*", "ECU binary (*.bin;*.ori)|*.bin;*.ori|All|*.*"),
        };
        if (dlg.ShowDialog() == true) txtBslRef.Text = dlg.FileName;
    }

    private void cmbBslCompare_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (pnlBslRef != null) pnlBslRef.Visibility = cmbBslCompare.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A roda do rato sobre uma lista fechada não muda a opção: passa para a coluna (deslocar).</summary>
    private void Combo_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox { IsDropDownOpen: false } cb)
        {
            e.Handled = true;
            var sv = FindParent<System.Windows.Controls.ScrollViewer>(cb);
            sv?.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta / 3.0);
        }
    }

    private static TP? FindParent<TP>(DependencyObject d) where TP : DependencyObject
    {
        for (var p = System.Windows.Media.VisualTreeHelper.GetParent(d); p != null; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
            if (p is TP t) return t;
        return null;
    }

    /// <summary>Opções da gravação BSL, para o log (mostra com que opções a gravação arrancou).</summary>
    private string BslOptionsText() => T(
        $"Opções: {(cmbBslCompare.SelectedIndex switch { 0 => "comparar com ficheiro lido", 1 => "ler a ECU e comparar", _ => "não comparar (grava tudo)" })} · {(chkBslVerify.IsChecked == true ? "verificar" : "sem verificação")} · {(chkBslSaveBackup.IsChecked == true ? "guardar o que se lê" : "sem backup")}.",
        $"Options: {(cmbBslCompare.SelectedIndex switch { 0 => "compare with read file", 1 => "read the ECU and compare", _ => "don't compare (write all)" })} · {(chkBslVerify.IsChecked == true ? "verify" : "no verification")} · {(chkBslSaveBackup.IsChecked == true ? "save what is read" : "no backup")}.");

    /// <summary>
    /// Grava a flash externa por modo boot. Fase 1 (sem perguntar, só lê): liga, identifica, lê os IDs do chip (só
    /// Am29F400BB) e decide os sectores: comparando com um ficheiro lido pelo utilizador (só se confirma que a
    /// identificação é a da ECU), lendo a flash toda (backup + comparação exata) ou todos. Fase 2 (depois de
    /// confirmar): apaga e grava esses sectores e relê-os. Um arranque em branco no ficheiro (ME3.1 do Examiner) nunca
    /// se grava: vem da leitura, ou o sector de arranque fica de fora.
    /// </summary>
    private async void btnBslWrite_Click(object sender, RoutedEventArgs e) => await BslWriteFlashAsync(txtBslFile.Text);

    /// <summary>Grava a flash por BSL a partir de <paramref name="file"/>; true se gravou (ou já estava igual).</summary>
    private async Task<bool> BslWriteFlashAsync(string file, bool finalMessage = true)
    {
        if (_busy) return false;
        if (_kwp != null) { Log(T("Desliga primeiro a ligação OBD: o modo boot usa o cabo diretamente.", "Disconnect the OBD link first: boot mode uses the cable directly.")); return false; }
        if (cmbIface.SelectedIndex == 2) { Log(T("O modo boot precisa de um cabo KKL (FTDI ou Série), não de um J2534.", "Boot mode needs a KKL cable (FTDI or Serial), not a J2534.")); return false; }

        var img = LoadImage(file, out var err, out var prof, out bool examiner);
        if (img == null) { Log(err ?? ""); return false; }
        int baud = int.TryParse(cmbBslBaud.Text.Trim(), out var b) && b > 0 ? b : 125000;
        Log(T("Gravar flash · ", "Write flash · ") + BslOptionsText());
        int mode = cmbBslCompare.SelectedIndex;   // 0 = ficheiro lido por mim, 1 = ler a ECU, 2 = tudo

        byte[]? reference = null;
        string refName = Path.GetFileName(txtBslRef.Text);   // os controlos só se leem nesta thread
        if (mode == 0)
        {
            if (!File.Exists(txtBslRef.Text)) { Log(T("Escolhe o ficheiro lido da ECU (ou outra opção em \"Antes de gravar\").", "Choose the file read from the ECU (or another option in \"Before writing\").")); return false; }
            reference = File.ReadAllBytes(txtBslRef.Text);
            if (reference.Length != BslRead.ExternalFlashSize)
            {
                Log(T($"O ficheiro lido da ECU tem de ter 512 KB (tem {reference.Length} bytes): uma leitura completa da flash.",
                      $"The file read from the ECU must be 512 KB (it has {reference.Length} bytes): a full flash read."));
                return false;
            }
        }
        OpenFolderDialog? folder = null;
        if (mode == 1 && chkBslSaveBackup.IsChecked == true)
        {
            folder = new OpenFolderDialog { Title = T("Pasta para o backup da flash atual (lida antes de gravar)", "Folder for the backup of the current flash (read before writing)") };
            if (folder.ShowDialog() != true) return false;
        }

        ICommunicationDevice dev;
        try { dev = CreateDevice(); }   // lê os controlos da janela: tem de ser nesta thread
        catch (Exception ex) { Log(T($"ERRO: {ex.Message}", $"ERROR: {ex.Message}")); return false; }

        C167Bsl? bsl = null;
        BslRead.BslIdentity? identity = null;
        BslWrite.FlashChip? chip = null;
        byte[] image = (byte[])img.Clone();
        List<int> sectors = new();
        string backupPath = "";
        bool bootBlank = false;
        void Progress(string what, int done, int total, bool writing = false) => Dispatcher.BeginInvoke(() =>
        {
            if (writing) ProgWriting(true);
            prog.Value = total > 0 ? 100.0 * done / total : 0;
            lblProg.Text = $"BSL: {what} {done / 1024}/{total / 1024} KB";
        });
        try
        {
            bool ok = await RunBusy(T("Preparação da gravação (BSL)", "Write preparation (BSL)"), () =>
            {
                dev.Open();
                bsl = new C167Bsl(dev) { OnLog = Log };
                Log(T($"Modo boot a {baud} baud: a ligar…", $"Boot mode at {baud} baud: connecting…"));
                bsl.Connect(baud);
                if (bsl.CpuVariant is byte cpu) _bslCpu = cpu;
                identity = BslRead.Identify(bsl);
                var id = identity;
                Dispatcher.BeginInvoke(() => ShowBslIdentity(id));
                Log(id.Ident is { } ei
                    ? T($"Identificação: {ei} · família {id.Family}.", $"Identification: {ei} · family {id.Family}.")
                    : T("Identificação: bloco não encontrado na flash (vazia ou corrompida?).", "Identification: block not found in the flash (blank or corrupted?)."));

                // o que está na ECU: o ficheiro lido pelo utilizador (confere a identificação), uma leitura agora, ou nada
                byte[]? current = null;
                if (mode == 0)
                {
                    var refId = EcuIdent.FromImage(reference!);
                    if (id.Ident is not { } ecuId || refId == null || !SameSoftware(refId, ecuId))
                        throw new BslException(T($"O ficheiro lido da ECU ({refId?.ToString() ?? "sem identificação"}) não é o que está na ECU ({id.Ident?.ToString() ?? "sem identificação"}). Escolhe a leitura certa, ou \"Ler a ECU\" / \"Não comparar\".",
                                                 $"The file read from the ECU ({refId?.ToString() ?? "no identification"}) is not what is in the ECU ({id.Ident?.ToString() ?? "no identification"}). Choose the right read, or \"Read the ECU\" / \"Don't compare\"."));
                    current = reference;
                    Log(T($"A comparar com {refName} (identificação igual à da ECU).", $"Comparing with {refName} (identification matches the ECU)."));
                }
                else if (mode == 1)
                {
                    current = BslRead.ReadExternalFlash(bsl, progress: (d, t) => Progress(T("leitura", "read"), d, t));
                    if (folder != null)
                    {
                        backupPath = Path.Combine(folder.FolderName, $"{BslFilePrefix(identity)}_flash_antes_de_gravar.bin");
                        File.WriteAllBytes(backupPath, current);
                        Log(T($"Backup da flash atual: {backupPath} (CRC-32 0x{CoreFlasher.Crc32(current):X8}).",
                              $"Backup of the current flash: {backupPath} (CRC-32 0x{CoreFlasher.Crc32(current):X8})."));
                    }
                    else
                        Log(T($"Flash atual lida para comparar (CRC-32 0x{CoreFlasher.Crc32(current):X8}, não guardada).",
                              $"Current flash read for comparison (CRC-32 0x{CoreFlasher.Crc32(current):X8}, not saved)."));
                }

                chip = BslWrite.PrepareAndReadChip(bsl);
                Log(T($"Chip da flash: {chip}.", $"Flash chip: {chip}."));
                if (!chip.IsAm29F400Bb)
                    throw new BslException(T($"Chip da flash não suportado ({chip}): só se grava a Am29F400BB (512 KB, arranque em baixo).",
                                             $"Unsupported flash chip ({chip}): only the Am29F400BB (512 KB, bottom boot) is written."));

                // um arranque em branco no ficheiro (ME3.1 do Examiner) nunca se grava: fica o da ECU
                var (bootStart, bootSize) = BslWrite.Sectors[0];
                bootBlank = image.AsSpan(bootStart, bootSize).IndexOfAnyExcept((byte)0xFF) < 0;
                if (current != null)
                {
                    if (bootBlank) current.AsSpan(bootStart, bootSize).CopyTo(image.AsSpan(bootStart));
                    sectors = BslWrite.ChangedSectors(image, current);
                }
                else
                    sectors = Enumerable.Range(bootBlank ? 1 : 0, BslWrite.Sectors.Count - (bootBlank ? 1 : 0)).ToList();
            });
            if (!ok) return false;

            if (sectors.Count == 0)
            {
                Log(T("A flash já é igual ao ficheiro: nada a gravar.", "The flash already matches the file: nothing to write."));
                if (finalMessage) MessageBox.Show(T("A flash já é igual ao ficheiro: não se gravou nada.", "The flash already matches the file: nothing was written."),
                    T("Gravar flash (BSL)", "Write flash (BSL)"), MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }

            var fileIdent = EcuIdent.FromImage(image);
            var ecuIdent = identity?.Ident;
            string fileFamily = (FiatFlashProfile.Detect(image) ?? prof).Name;
            string warn = "";
            if (fileIdent != null && ecuIdent != null && !fileIdent.SameHardware(ecuIdent))
                warn += T($"⚠⚠ HARDWARE DIFERENTE: o ficheiro é para o {fileIdent.Hardware}, a ECU é {ecuIdent.Hardware}. Só continua se sabes o que estás a fazer.\n\n",
                          $"⚠⚠ DIFFERENT HARDWARE: the file is for {fileIdent.Hardware}, the ECU is {ecuIdent.Hardware}. Only continue if you know what you are doing.\n\n");
            if (identity?.Family is { } ecuFamily && ecuFamily != fileFamily)
                warn += T($"⚠ FAMÍLIA DIFERENTE: a ECU tem {ecuFamily}, o ficheiro é {fileFamily}.\n\n", $"⚠ DIFFERENT FAMILY: the ECU has {ecuFamily}, the file is {fileFamily}.\n\n");
            if (fileIdent == null)
                warn += T("⚠ O ficheiro não tem bloco de identificação Bosch.\n\n", "⚠ The file has no Bosch identification block.\n\n");
            if (sectors.Contains(0))
                warn += T("⚠ Inclui o SECTOR DE ARRANQUE (0x00000–0x03FFF): se falhar a meio, a ECU só arranca de novo em modo boot.\n\n",
                          "⚠ Includes the BOOT SECTOR (0x00000–0x03FFF): if it fails midway, the ECU only starts again in boot mode.\n\n");
            int bytes = sectors.Sum(s => BslWrite.Sectors[s].Size);
            string list = string.Join(", ", sectors.Select(s => $"{s} (0x{BslWrite.Sectors[s].Start:X5}, {BslWrite.Sectors[s].Size / 1024} K)"));
            string basis = mode switch
            {
                0 => T($"Comparado com o ficheiro lido: {Path.GetFileName(txtBslRef.Text)} (confirmada só a identificação; se a ECU tiver outra coisa com a mesma identificação, os sectores que não se gravam ficam com o que lá está).\n",
                       $"Compared with the read file: {Path.GetFileName(txtBslRef.Text)} (only the identification was checked; if the ECU holds something else with the same identification, the sectors not written keep what is there).\n"),
                1 when backupPath.Length > 0 => T($"Comparado com a flash lida agora. Backup: {backupPath}\n", $"Compared with the flash read just now. Backup: {backupPath}\n"),
                1 => T("Comparado com a flash lida agora (não guardada).\n", "Compared with the flash read just now (not saved).\n"),
                _ => T("Sem comparação e SEM BACKUP (não se leu a ECU): grava os sectores todos.\n", "No comparison and NO BACKUP (the ECU was not read): writes all sectors.\n"),
            };
            string bootNote = !bootBlank ? ""
                : mode == 2 ? T("O ficheiro tem o arranque em branco: o sector de arranque não se toca.\n", "The file has a blank boot: the boot sector is not touched.\n")
                : T("O ficheiro tem o arranque em branco: fica o que está na ECU.\n", "The file has a blank boot: the ECU's one is kept.\n");
            var yes = MessageBox.Show(warn +
                T($"GRAVAR FLASH POR BSL: {Path.GetFileName(file)}{(examiner ? " (original do Examiner, desencriptado)" : "")}\n" +
                  $"Ficheiro: {fileIdent?.ToString() ?? "—"} · {fileFamily}\nECU:      {ecuIdent?.ToString() ?? "—"}{(identity?.Family is { } f ? $" · {f}" : "")}\n" +
                  $"Chip: {chip}\n\n" +
                  $"Apaga e grava {sectors.Count} sector(es), {bytes / 1024} KB: {list}.\n" + (sectors.Count < BslWrite.Sectors.Count ? "Os outros sectores ficam como estão.\n" : "") +
                  bootNote + "\n" + basis + "\n" +
                  "O modo boot grava o ficheiro tal como está: os checksums têm de estar corrigidos.\n" +
                  "Se falhar, a ECU continua a entrar em modo boot e grava-se de novo.\n\nContinuar?",
                  $"WRITE FLASH BY BSL: {Path.GetFileName(file)}{(examiner ? " (Examiner original, decrypted)" : "")}\n" +
                  $"File: {fileIdent?.ToString() ?? "—"} · {fileFamily}\nECU:  {ecuIdent?.ToString() ?? "—"}{(identity?.Family is { } f2 ? $" · {f2}" : "")}\n" +
                  $"Chip: {chip}\n\n" +
                  $"Erases and writes {sectors.Count} sector(s), {bytes / 1024} KB: {list}.\n" + (sectors.Count < BslWrite.Sectors.Count ? "The other sectors stay as they are.\n" : "") +
                  bootNote + "\n" + basis + "\n" +
                  "Boot mode writes the file as it is: the checksums must be corrected.\n" +
                  "If it fails, the ECU still enters boot mode and can be written again.\n\nContinue?"),
                T("Confirmar gravação por BSL", "Confirm BSL write"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (yes != MessageBoxResult.Yes) { Log(T("Gravação cancelada (nada foi apagado).", "Write cancelled (nothing was erased).")); return false; }

            var b2 = bsl!;
            bool verify = chkBslVerify.IsChecked == true;
            ok = await RunBusy(T("Gravação da flash (BSL)", "Flash write (BSL)"), () =>
            {
                BslWrite.WriteSectors(b2, image, sectors, (w, d, t) => Progress(w, d, t, writing: true));
                if (!verify)
                {
                    Log(T($"Gravados {sectors.Count} sector(es) (sem verificação, como pedido).", $"Wrote {sectors.Count} sector(s) (no verification, as requested)."));
                    return;
                }
                Log(T($"Gravados {sectors.Count} sector(es); a verificar…", $"Wrote {sectors.Count} sector(s); verifying…"));
                var bad = BslWrite.VerifySectors(b2, image, sectors, (w, d, t) => Progress(w, d, t, writing: true));
                if (bad.Count > 0)
                    throw new BslException(T($"A verificação falhou nos sectores {string.Join(", ", bad)}. Grava de novo (a ECU continua em modo boot){(backupPath.Length > 0 ? $" ou repõe o backup {backupPath}" : "")}.",
                                             $"Verification failed in sectors {string.Join(", ", bad)}. Write again (the ECU is still in boot mode){(backupPath.Length > 0 ? $" or restore the backup {backupPath}" : "")}."));
                Log(T("Verificação OK: os sectores gravados são iguais ao ficheiro.", "Verification OK: the written sectors match the file."));
            });
            if (ok && finalMessage)
                MessageBox.Show(T($"Flash gravada{(verify ? " e verificada" : " (sem verificação)")}.\nDesliga a alimentação, tira o pino de boot da massa e volta a ligar para a ECU arrancar.",
                                  $"Flash written{(verify ? " and verified" : " (not verified)")}.\nPower off, release the boot pin from ground and power on again so the ECU starts."),
                    "OK", MessageBoxButton.OK, MessageBoxImage.Information);
            return ok;
        }
        finally { dev.Dispose(); }
    }

    private void btnBrowseBslEeprom_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = T("EEPROM (*.bin;*.eep)|*.bin;*.eep|Todos|*.*", "EEPROM (*.bin;*.eep)|*.bin;*.eep|All|*.*") };
        if (dlg.ShowDialog() == true) txtBslEepromFile.Text = dlg.FileName;
    }

    /// <summary>
    /// Grava a EEPROM 95080 por modo boot: lê-a primeiro (backup ao lado do ficheiro), confirma, grava só os bytes que
    /// mudam e verifica relendo (está dentro do <see cref="BslRead.WriteSpiEeprom"/>).
    /// </summary>
    private async void btnBslEepromWrite_Click(object sender, RoutedEventArgs e) => await BslWriteEepromAsync(txtBslEepromFile.Text);

    /// <summary>Grava a EEPROM por BSL a partir de <paramref name="file"/>; true se gravou (ou já estava igual).</summary>
    private async Task<bool> BslWriteEepromAsync(string file, bool finalMessage = true)
    {
        if (_busy) return false;
        if (_kwp != null) { Log(T("Desliga primeiro a ligação OBD: o modo boot usa o cabo diretamente.", "Disconnect the OBD link first: boot mode uses the cable directly.")); return false; }
        if (cmbIface.SelectedIndex == 2) { Log(T("O modo boot precisa de um cabo KKL (FTDI ou Série), não de um J2534.", "Boot mode needs a KKL cable (FTDI or Serial), not a J2534.")); return false; }
        if (!File.Exists(file)) { Log(T("Escolhe o ficheiro da EEPROM a gravar.", "Choose the EEPROM file to write.")); return false; }
        var data = File.ReadAllBytes(file);
        if (data.Length != BslRead.Eeprom95080Size)
        {
            Log(T($"O ficheiro da EEPROM tem de ter {BslRead.Eeprom95080Size} bytes (95080); tem {data.Length}.", $"The EEPROM file must be {BslRead.Eeprom95080Size} bytes (95080); it has {data.Length}."));
            return false;
        }
        int baud = int.TryParse(cmbBslBaud.Text.Trim(), out var b) && b > 0 ? b : 125000;
        Log(T("Gravar EEPROM · ", "Write EEPROM · ") + BslOptionsText());
        bool compare = cmbBslCompare.SelectedIndex != 2;   // "Não comparar": grava os bytes todos, sem ler antes
        bool verify = chkBslVerify.IsChecked == true;
        OpenFolderDialog? folder = null;
        if (chkBslSaveBackup.IsChecked == true)
        {
            folder = new OpenFolderDialog { Title = T("Pasta para o backup da EEPROM atual (lida antes de gravar)", "Folder for the backup of the current EEPROM (read before writing)") };
            if (folder.ShowDialog() != true) return false;
        }

        ICommunicationDevice dev;
        try { dev = CreateDevice(); }   // lê os controlos da janela: tem de ser nesta thread
        catch (Exception ex) { Log(T($"ERRO: {ex.Message}", $"ERROR: {ex.Message}")); return false; }

        C167Bsl? bsl = null;
        BslRead.BslIdentity? identity = null;
        byte[]? current = null;
        try
        {
            bool ok = await RunBusy(T("Preparação da gravação da EEPROM (BSL)", "EEPROM write preparation (BSL)"), () =>
            {
                dev.Open();
                bsl = new C167Bsl(dev) { OnLog = Log };
                Log(T($"Modo boot a {baud} baud: a ligar…", $"Boot mode at {baud} baud: connecting…"));
                bsl.Connect(baud);
                if (bsl.CpuVariant is byte cpu) _bslCpu = cpu;
                identity = BslRead.Identify(bsl);
                var id = identity;
                Dispatcher.BeginInvoke(() => ShowBslIdentity(id));
                if (!compare && folder == null) return;   // sem comparar nem backup: não é preciso ler
                current = BslRead.ReadSpiEeprom(bsl);
                if (folder != null)
                {
                    string path = Path.Combine(folder.FolderName, $"{BslFilePrefix(identity)}_eeprom_antes_de_gravar.bin");
                    File.WriteAllBytes(path, current);
                    Log(T($"Backup da EEPROM atual: {path} (CRC-32 0x{CoreFlasher.Crc32(current):X8}).",
                          $"Backup of the current EEPROM: {path} (CRC-32 0x{CoreFlasher.Crc32(current):X8})."));
                }
                else
                    Log(T($"EEPROM atual lida para comparar (CRC-32 0x{CoreFlasher.Crc32(current):X8}, não guardada).",
                          $"Current EEPROM read for comparison (CRC-32 0x{CoreFlasher.Crc32(current):X8}, not saved)."));
            });
            if (!ok) return false;

            var compareWith = compare ? current : null;
            var diffs = Enumerable.Range(0, data.Length).Where(i => compareWith == null || data[i] != compareWith[i]).ToList();
            if (diffs.Count == 0)
            {
                Log(T("A EEPROM já é igual ao ficheiro: nada a gravar.", "The EEPROM already matches the file: nothing to write."));
                if (finalMessage) MessageBox.Show(T("A EEPROM já é igual ao ficheiro: não se gravou nada.", "The EEPROM already matches the file: nothing was written."),
                    T("Gravar EEPROM (BSL)", "Write EEPROM (BSL)"), MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }
            // zonas alteradas, para a confirmação (ex.: 0x010–0x01F, 0x3F0)
            var ranges = new List<string>();
            for (int i = 0; i < diffs.Count;)
            {
                int j = i;
                while (j + 1 < diffs.Count && diffs[j + 1] <= diffs[j] + 4) j++;
                ranges.Add(diffs[i] == diffs[j] ? $"0x{diffs[i]:X3}" : $"0x{diffs[i]:X3}–0x{diffs[j]:X3}");
                i = j + 1;
            }
            string where = compareWith == null ? T("todos, sem comparar", "all, no comparison")
                         : string.Join(", ", ranges.Take(12)) + (ranges.Count > 12 ? " …" : "");
            var yes = MessageBox.Show(
                T($"GRAVAR EEPROM POR BSL: {Path.GetFileName(file)}\nECU: {identity?.Ident?.ToString() ?? "—"}\n\n" +
                  $"Muda {diffs.Count} byte(s) de {data.Length}: {where}.\n\n" +
                  "A EEPROM guarda os dados do imobilizador e as adaptações: uma EEPROM de outra ECU muda o emparelhamento. " +
                  (folder != null ? $"Backup da EEPROM atual guardado em {folder.FolderName}." : "Não se guarda backup: confirma que já tens uma leitura desta EEPROM.") + "\n\nContinuar?",
                  $"WRITE EEPROM BY BSL: {Path.GetFileName(file)}\nECU: {identity?.Ident?.ToString() ?? "—"}\n\n" +
                  $"Changes {diffs.Count} byte(s) of {data.Length}: {where}.\n\n" +
                  "The EEPROM holds the immobilizer data and adaptations: an EEPROM from another ECU changes the pairing. " +
                  (folder != null ? $"Backup of the current EEPROM saved in {folder.FolderName}." : "No backup is saved: make sure you already have a read of this EEPROM.") + "\n\nContinue?"),
                T("Confirmar gravação da EEPROM", "Confirm EEPROM write"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (yes != MessageBoxResult.Yes) { Log(T("Gravação da EEPROM cancelada.", "EEPROM write cancelled.")); return false; }

            var b2 = bsl!;
            ok = await RunBusy(T("Gravação da EEPROM (BSL)", "EEPROM write (BSL)"), () =>
            {
                int n = BslRead.WriteSpiEeprom(b2, data, compareWith, verify: verify, progress: (d, t) => Dispatcher.BeginInvoke(() =>
                {
                    ProgWriting(true);
                    prog.Value = t > 0 ? 100.0 * d / t : 0;
                    lblProg.Text = $"BSL: EEPROM {d}/{t} bytes";
                }));
                Log(verify ? T($"EEPROM: {n} byte(s) gravados e verificados.", $"EEPROM: {n} byte(s) written and verified.")
                           : T($"EEPROM: {n} byte(s) gravados (sem verificação, como pedido).", $"EEPROM: {n} byte(s) written (no verification, as requested)."));
            });
            if (ok && finalMessage)
                MessageBox.Show(verify ? T("EEPROM gravada e verificada.", "EEPROM written and verified.") : T("EEPROM gravada (sem verificação).", "EEPROM written (not verified)."),
                    "OK", MessageBoxButton.OK, MessageBoxImage.Information);
            return ok;
        }
        finally { dev.Dispose(); }
    }

    /// <summary>
    /// Repõe um backup completo (do "Backup completo"): grava o …_flash.bin e depois o …_eeprom.bin com o mesmo prefixo,
    /// com as opções da gravação. A ROM interna fica de fora (OTP).
    /// </summary>
    private async void btnBslRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dlg = new OpenFileDialog
        {
            Title = T("Ficheiro do backup a repor (…_info.txt, …_flash.bin ou …_eeprom.bin)", "Backup file to restore (…_info.txt, …_flash.bin or …_eeprom.bin)"),
            Filter = T("Backup (*_info.txt;*_flash.bin;*_eeprom.bin)|*_info.txt;*_flash.bin;*_eeprom.bin|Todos|*.*",
                       "Backup (*_info.txt;*_flash.bin;*_eeprom.bin)|*_info.txt;*_flash.bin;*_eeprom.bin|All|*.*"),
        };
        if (dlg.ShowDialog() != true) return;

        string name = Path.GetFileName(dlg.FileName);
        string? prefix = new[] { "_info.txt", "_flash.bin", "_eeprom.bin", "_irom.bin" }
            .Where(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)).Select(s => name[..^s.Length]).FirstOrDefault();
        if (prefix == null)
        {
            Log(T("Escolhe um ficheiro de um backup completo (…_info.txt, …_flash.bin ou …_eeprom.bin).", "Choose a file from a full backup (…_info.txt, …_flash.bin or …_eeprom.bin)."));
            return;
        }
        string dir = Path.GetDirectoryName(dlg.FileName)!;
        string flash = Path.Combine(dir, prefix + "_flash.bin"), eeprom = Path.Combine(dir, prefix + "_eeprom.bin");
        bool hasFlash = File.Exists(flash), hasEeprom = File.Exists(eeprom);
        if (!hasFlash && !hasEeprom)
        {
            Log(T($"Não há {prefix}_flash.bin nem {prefix}_eeprom.bin em {dir}.", $"There is no {prefix}_flash.bin or {prefix}_eeprom.bin in {dir}."));
            return;
        }
        string what = hasFlash && hasEeprom ? "flash + EEPROM" : hasFlash ? "flash" : "EEPROM";
        Log(T($"Repor o backup {prefix}: {what}.", $"Restoring backup {prefix}: {what}."));

        if (hasFlash && !await BslWriteFlashAsync(flash, finalMessage: false))
        {
            Log(T("Reposição parada: a flash não foi gravada; a EEPROM não se tocou.", "Restore stopped: the flash was not written; the EEPROM was not touched."));
            return;
        }
        if (hasEeprom && !await BslWriteEepromAsync(eeprom, finalMessage: false))
        {
            Log(T("Reposição incompleta: a flash ficou gravada, a EEPROM não.", "Restore incomplete: the flash was written, the EEPROM was not."));
            return;
        }
        Log(T($"Backup {prefix} reposto ({what}).", $"Backup {prefix} restored ({what})."));
        MessageBox.Show(T($"Backup reposto ({what}).\nDesliga a alimentação, tira o pino de boot da massa e volta a ligar para a ECU arrancar.",
                          $"Backup restored ({what}).\nPower off, release the boot pin from ground and power on again so the ECU starts."),
            "OK", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Mesmo hardware, software e versão (para confiar que uma leitura antiga é o que está na ECU).</summary>
    private static bool SameSoftware(EcuIdent a, EcuIdent b)
        => a.SameHardware(b) && a.Software.Trim() == b.Software.Trim()
           && (a.SoftwareVersion ?? Array.Empty<byte>()).AsSpan().SequenceEqual(b.SoftwareVersion ?? Array.Empty<byte>());

    private byte? _bslCpu;

    private static string BslLabel(string part) => part switch
    {
        "flash" => T("flash externa", "external flash"),
        "irom" => T("ROM interna", "internal ROM"),
        _ => "EEPROM",
    };

    /// <summary>Identificação lida por modo boot no grupo ECU.</summary>
    private void ShowBslIdentity(BslRead.BslIdentity id)
    {
        if (!SameIdent(_bslIdent, id.Ident)) { _bslIdent = id.Ident; UpdateMatch(); }
        string cpu = _bslCpu is byte c ? $"CPU 0x{c:X2} ({C167Bsl.VariantName(c)})" : "CPU C167";
        lblInfo.Text = id.Ident is { } ei
            ? T($"Modo boot · {cpu}\nFamília: {id.Family}\nBosch: {ei.Hardware}\nSoftware: {ei.Software}{(ei.SoftwareVersion is { } v ? $" v{v[0]:X2}{v[1]:X2}" : "")}\nDesenho Fiat: {ei.FiatDrawing ?? "—"}",
                $"Boot mode · {cpu}\nFamily: {id.Family}\nBosch: {ei.Hardware}\nSoftware: {ei.Software}{(ei.SoftwareVersion is { } v2 ? $" v{v2[0]:X2}{v2[1]:X2}" : "")}\nFiat drawing: {ei.FiatDrawing ?? "—"}")
            : T($"Modo boot · {cpu}\nSem bloco de identificação na flash.", $"Boot mode · {cpu}\nNo identification block in the flash.");
    }

    /// <summary>Prefixo dos ficheiros do backup: software e hardware, mais a data (para não sobrescrever).</summary>
    private static string BslFilePrefix(BslRead.BslIdentity? id)
    {
        string who = id?.Ident is { } ei ? $"{ei.Software.Trim()}_{ei.Hardware.Trim()}" : "ecu";
        foreach (char ch in Path.GetInvalidFileNameChars()) who = who.Replace(ch, '_');
        return $"{who}_{DateTime.Now:yyyyMMdd_HHmm}";
    }

    private string BslInfoText(BslRead.BslIdentity? id, int baud, List<(string Kind, byte[] Data)> parts, string? eepromError)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Backup por modo boot (BSL) — {DateTime.Now:yyyy-MM-dd HH:mm}");
        if (_bslCpu is byte c) sb.AppendLine($"CPU: 0x{c:X2} ({C167Bsl.VariantName(c)})");
        sb.AppendLine($"Baud: {baud}");
        if (id?.Ident is { } ei)
        {
            sb.AppendLine($"Família: {id.Family}");
            sb.AppendLine($"Bosch: {ei.Hardware}");
            sb.AppendLine($"Software: {ei.Software}{(ei.SoftwareVersion is { } v ? $" v{v[0]:X2}{v[1]:X2}" : "")}");
            sb.AppendLine($"Desenho Fiat: {ei.FiatDrawing ?? "—"}");
        }
        else sb.AppendLine("Identificação: bloco não encontrado na flash.");
        foreach (var (part, data) in parts)
            sb.AppendLine($"{part}: {data.Length} bytes, CRC-32 0x{CoreFlasher.Crc32(data):X8}");
        if (eepromError != null) sb.AppendLine($"EEPROM: não lida — {eepromError}");
        return sb.ToString();
    }

    /// <summary>Desencripta um .BIN do Examiner e grava _dec.bin ao lado (não precisa de ECU).</summary>
    private void btnDecrypt_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = T("Ficheiro cifrado do Examiner", "Encrypted Examiner file"),
            Filter = T("Examiner (*.bin)|*.bin|Todos|*.*", "Examiner (*.bin)|*.bin|All|*.*"),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var enc = File.ReadAllBytes(dlg.FileName);
            var key = FiatKey(File.Exists(txtNew.Text) ? File.ReadAllBytes(txtNew.Text) : null, out var keySrc);
            if (FiatFullFlash.Identify(enc) is string plainId)
                Log(T($"AVISO {Path.GetFileName(dlg.FileName)} já parece desencriptado ({plainId}). Desencripta-se na mesma.",
                      $"WARNING {Path.GetFileName(dlg.FileName)} already looks decrypted ({plainId}). Decrypting anyway."));
            var dec = FiatCipher.DecryptAll(enc, key);
            string? id = FiatFullFlash.Identify(dec);
            string outPath = Path.Combine(Path.GetDirectoryName(dlg.FileName)!, Path.GetFileNameWithoutExtension(dlg.FileName) + "_dec.bin");
            File.WriteAllBytes(outPath, dec);
            Log(T($"Desencriptado com a chave {Convert.ToHexString(key)} ({keySrc}) → {outPath}",
                  $"Decrypted with key {Convert.ToHexString(key)} ({keySrc}) → {outPath}"));
            Log(id != null
                ? T($"Identificação encontrada: {id}", $"Identification found: {id}")
                : T("AVISO: não aparece nenhuma identificação Bosch no resultado — chave errada ou ficheiro não é do Examiner.",
                    "WARNING: no Bosch identification in the result — wrong key or not an Examiner file."));
            if (dec.Length == _layout.TotalSize)
                Log(T("Nota: no ficheiro do Examiner a zona 0x38000–0x3FFFF vem em branco (a ECU gere o backup); numa leitura da ECU essa zona tem dados.",
                      "Note: in the Examiner file the 0x38000–0x3FFFF area is blank (the ECU manages the backup); in an ECU read that area has data."));
        }
        catch (Exception ex) { Log(T($"ERRO desencriptar: {ex.Message}", $"ERROR decrypting: {ex.Message}")); }
    }

    /// <summary>Corre uma operação longa com o keep-alive em pausa e os botões bloqueados.</summary>
    private async Task<bool> RunBusy(string what, Action action)
    {
        _busy = true;
        btnFlash.IsEnabled = btnTest.IsEnabled = btnConnect.IsEnabled = btnDisconnect.IsEnabled = false;
        prog.Value = 0;
        ProgWriting(false);   // verde por omissão; uma escrita põe-na vermelha e assim fica até à operação seguinte
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Task.Run(() => { lock (_ioLock) action(); });
            Log($"{what}: OK ({sw.Elapsed.TotalSeconds:0.0} s).");
            return true;
        }
        catch (Exception ex)
        {
            Log(T($"ERRO {what.ToLowerInvariant()}: {ex.Message}", $"ERROR {what.ToLowerInvariant()}: {ex.Message}"));
            MessageBox.Show(ex.Message, T($"Erro — {what}", $"Error — {what}"), MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            _busy = false;
            btnConnect.IsEnabled = true;
            bool connected = _kwp != null;
            btnDisconnect.IsEnabled = btnTest.IsEnabled = connected;
            UpdatePlan();
        }
    }
}
