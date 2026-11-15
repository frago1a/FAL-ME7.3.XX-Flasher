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

    private void Disconnect()
    {
        StopKeepAlive();
        try { _device?.Close(); _device?.Dispose(); } catch { }
        _device = null; _kwp = null; _flasher = null;
        btnDisconnect.IsEnabled = false; btnTest.IsEnabled = false; btnFlash.IsEnabled = false;
        Status(T("Desligado", "Disconnected"));
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
                try { lock (_ioLock) { _kwp?.TesterPresent(true); } }
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
        foreach (var (opt, label) in new (EcuIdentificationOption, string)[]
        {
            (EcuIdentificationOption.FiatDrawingNumber, "Drawing"),
            (EcuIdentificationOption.EcuHardwareNumber, "Hardware"),
            (EcuIdentificationOption.EcuSoftwareNumber, "Software"),
            (EcuIdentificationOption.TesterCode, "Tester"),
            (EcuIdentificationOption.ProgrammingDate, T("Data prog.", "Prog. date")),
        })
        {
            try { var d = await Task.Run(() => { lock (_ioLock) return kwp.ReadEcuIdentification(opt); }); sb.AppendLine($"{label}: {Ascii(d)}"); }
            catch (Exception ex) { sb.AppendLine(T($"{label}: (erro {ex.Message})", $"{label}: (error {ex.Message})")); }
        }
        lblInfo.Text = sb.ToString();
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
    private byte[]? LoadImage(out string? error)
    {
        error = null;
        if (!File.Exists(txtNew.Text)) { error = T("Escolhe o ficheiro a gravar.", "Choose the file to write."); return null; }
        var img = File.ReadAllBytes(txtNew.Text);
        if (img.Length != _layout.TotalSize)
        {
            error = T($"O ficheiro tem {img.Length} bytes; esperado {_layout.TotalSize} (512 KB).",
                      $"The file has {img.Length} bytes; expected {_layout.TotalSize} (512 KB).");
            return null;
        }
        if (FiatFullFlash.Identify(img) == null && FiatFullFlash.Identify(FiatCipher.DecryptAll(img)) != null)
        {
            error = T("Este ficheiro está CIFRADO (Examiner). Desencripta-o primeiro (botão à esquerda) e grava o _dec.bin.",
                      "This file is ENCRYPTED (Examiner). Decrypt it first (button on the left) and write the _dec.bin.");
            return null;
        }
        return img;
    }

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
        var img = LoadImage(out var err);
        if (img == null) { lblPlan.Text = T("Plano:", "Plan:"); if (err != null) lstDiff.Items.Add(err); btnFlash.IsEnabled = false; return; }

        var key = FiatKey(img, out var keySrc);
        ushort cs = FiatFullFlash.Checksum(FiatFullFlash.Prepare(img));
        lblPlan.Text = T($"Plano: flash completo, 512 KB cifrados · estimativa {Estimate(img.Length)}",
                         $"Plan: full flash, 512 KB encrypted · estimate {Estimate(img.Length)}");
        lstDiff.Items.Add(T($"Ficheiro: {FiatFullFlash.Identify(img) ?? "(sem identificação Bosch)"}",
                            $"File: {FiatFullFlash.Identify(img) ?? "(no Bosch identification)"}"));
        lstDiff.Items.Add(T("1. tester code + data de programação (a ECU exige-os)", "1. tester code + programming date (required by the ECU)"));
        lstDiff.Items.Add(T("2. 31 02 80 00 00 87 FF FF  apagar tudo (dados, código, arranque)", "2. 31 02 80 00 00 87 FF FF  erase everything (data, code, boot)"));
        lstDiff.Items.Add(T("3. 34 80 00 00 01 08 00 00  download único, cifrado", "3. 34 80 00 00 01 08 00 00  single encrypted download"));
        lstDiff.Items.Add(T($"4. 31 01 80 00 00 87 FF FF {cs >> 8:X2} {cs & 0xFF:X2}  verificação (CRC-32 >> 16)",
                            $"4. 31 01 80 00 00 87 FF FF {cs >> 8:X2} {cs & 0xFF:X2}  verification (CRC-32 >> 16)"));
        lstDiff.Items.Add(T($"ⓘ Chave: {Convert.ToHexString(key)} ({keySrc})", $"ⓘ Key: {Convert.ToHexString(key)} ({keySrc})"));
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
        f.Options.KeepEcuIdentification = chkKeepIdent.IsChecked == true;
        f.Options.DiagnoseOnHandoverFailure = false; // versão final: sem diagnóstico automático
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
        var img = LoadImage(out var err);
        if (flasher == null || img == null) { Log(err ?? T("Liga primeiro.", "Connect first.")); return; }

        DateTime date = (chkToday.IsChecked == true ? DateTime.Today : dateCustom.SelectedDate) ?? DateTime.Today;
        string? tester = string.IsNullOrWhiteSpace(txtTester.Text) ? null : txtTester.Text.Trim();
        var key = FiatKey(img, out var keySrc);
        bool keep = chkKeepIdent.IsChecked == true;
        string ident = keep
            ? T("Tester code e data: mantém os que estão na ECU.\n", "Tester code and date: keeps the ones already in the ECU.\n")
            : T($"Tester code {(tester != null ? $"\"{tester}\"" : "(mantém o atual)")} e data {date:yyyy-MM-dd}.\n",
                $"Tester code {(tester != null ? $"\"{tester}\"" : "(keeps the current one)")} and date {date:yyyy-MM-dd}.\n");

        var ok = MessageBox.Show(
            T($"FLASH COMPLETO (OBD, cifrado): {Path.GetFileName(txtNew.Text)}\n{FiatFullFlash.Identify(img)}\n512 KB, estimativa {Estimate(img.Length)}.\n\n",
              $"FULL FLASH (OBD, encrypted): {Path.GetFileName(txtNew.Text)}\n{FiatFullFlash.Identify(img)}\n512 KB, estimate {Estimate(img.Length)}.\n\n") +
            ident +
            T($"Chave: {Convert.ToHexString(key)} ({keySrc}).\n\n", $"Key: {Convert.ToHexString(key)} ({keySrc}).\n\n") +
            T("A ECU vai APAGAR A FLASH TODA (incluindo os sectores de arranque) e escrever o ficheiro inteiro.\n" +
              "Se falhar depois de apagar, a ECU fica sem aplicação e só recupera por BSL.\n\n" +
              "Requisito: imobilizador a autorizar (chave reconhecida) ou centralina virgem.\n\n" +
              "Alimentação estável, não desligar nada. Continuar?",
              "The ECU will ERASE THE WHOLE FLASH (including the boot sectors) and write the entire file.\n" +
              "If it fails after erasing, the ECU is left without an application and can only be recovered by BSL.\n\n" +
              "Requirement: immobilizer authorizing (key recognized) or virgin ECU.\n\n" +
              "Stable power supply, don't disconnect anything. Continue?"),
            T("Confirmar flash", "Confirm flash"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.Yes) return;

        ApplyOptions(flasher);
        bool done = await RunBusy("Flash", () => flasher.ProgramFiatFull(img, key, date, tester));
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
