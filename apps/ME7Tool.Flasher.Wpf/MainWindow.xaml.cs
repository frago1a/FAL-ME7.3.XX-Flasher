using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
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
        if (_ecuIdent == null)
        {
            lblMatch.Text = _kwp == null ? T("Liga à ECU para a procurar pela identificação.", "Connect to the ECU to look it up by its identification.")
                                         : T("A ECU não deu o nº Bosch e o software (0x1A): não dá para procurar.", "The ECU did not give its Bosch number and software (0x1A): cannot look it up.");
            return;
        }
        try { _match = _examiner.Match(_ecuIdent); }
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
            txtNew.Text = path;
            Log(T($"Ficheiro a gravar: original do Examiner {Path.GetFileName(path)} (pesquisa manual).",
                  $"File to write: Examiner original {Path.GetFileName(path)} (manual search)."));
        }, _ecuIdent?.Hardware) { Owner = this }.ShowDialog();
    }

    private void btnUseOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (_match?.Image is not { } img) return;
        txtNew.Text = img.Path; // o LoadImage reconhece o original cifrado e desencripta-o em memória
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
    {
        error = null;
        examinerOriginal = false;
        profile = FiatFlashProfile.Me731;
        if (!File.Exists(txtNew.Text)) { error = T("Escolhe o ficheiro a gravar.", "Choose the file to write."); return null; }

        // 1) original do Examiner (tem .Prm e o CRC confirma): decide o perfil e desencripta em memória
        if (ExaminerLibrary.OpenImage(txtNew.Text, out string? openWhy) is { } orig)
        {
            examinerOriginal = true;
            profile = orig.Profile;
            return orig.Image;
        }

        // 2) ficheiro próprio: 512 KB já desencriptados
        var img = File.ReadAllBytes(txtNew.Text);
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
