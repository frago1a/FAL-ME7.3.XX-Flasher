using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using ME7Tool.Core.Flashing;
using static ME7Tool.Core.Common.Lang;

namespace ME7Tool.Flasher.Wpf;

/// <summary>
/// Pesquisa manual na pasta do Examiner por referência (nº Bosch, software, desenho Fiat ou nome do ficheiro).
/// Do resultado escolhido: usar no flash ou extrair a imagem desencriptada para um .bin (sem ECU).
/// </summary>
public sealed class ExaminerSearchWindow : Window
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush Field = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x41));
    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x3C, 0x9A, 0x3C));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xC0, 0xA0, 0x00));

    private readonly ExaminerLibrary _lib;
    private readonly Action<string> _use;
    private readonly TextBox _query;
    private readonly ListBox _list;
    private readonly TextBlock _detail;
    private readonly Button _btnUse, _btnExtract;
    private ExaminerImage? _image;

    /// <param name="use">Recebe o caminho do .BIN escolhido para o flash.</param>
    public ExaminerSearchWindow(ExaminerLibrary lib, Action<string> use, string? initialQuery)
    {
        _lib = lib;
        _use = use;
        Title = T("Procurar original por referência", "Search original by reference");
        Width = 760; Height = 520; MinWidth = 520; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Bg; Foreground = Fg; FontFamily = new FontFamily("Segoe UI"); FontSize = 13;

        var root = new DockPanel { Margin = new Thickness(10) };

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var btnSearch = MakeButton(T("Procurar", "Search"), (_, _) => DoSearch());
        DockPanel.SetDock(btnSearch, Dock.Right);
        top.Children.Add(btnSearch);
        top.Children.Add(new TextBlock { Text = T("Referência:", "Reference:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _query = new TextBox
        {
            Text = initialQuery ?? "", Background = Field, Foreground = Fg, BorderBrush = Line, Padding = new Thickness(4, 3, 4, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = T("Nº Bosch (0261…), software (1037…/1277…), desenho Fiat (5519…/4681…) ou nome do ficheiro (BO0101…). Basta parte do número.",
                        "Bosch number (0261…), software (1037…/1277…), Fiat drawing (5519…/4681…) or file name (BO0101…). Part of the number is enough."),
        };
        _query.KeyDown += (_, e) => { if (e.Key == Key.Enter) DoSearch(); };
        top.Children.Add(_query);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        _btnUse = MakeButton(T("Usar para o flash", "Use for flashing"), (_, _) => UseSelected());
        _btnExtract = MakeButton(T("Extrair desencriptado…", "Extract decrypted…"), (_, _) => ExtractSelected());
        _btnUse.IsEnabled = _btnExtract.IsEnabled = false;
        bottom.Children.Add(_btnExtract);
        bottom.Children.Add(_btnUse);
        bottom.Children.Add(MakeButton(T("Fechar", "Close"), (_, _) => Close()));
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        _detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), MinHeight = 54 };
        DockPanel.SetDock(_detail, Dock.Bottom);
        root.Children.Add(_detail);

        _list = new ListBox { Background = Field, Foreground = Fg, BorderBrush = Line, FontFamily = new FontFamily("Consolas") };
        _list.SelectionChanged += (_, _) => ShowSelected();
        _list.MouseDoubleClick += (_, _) => { if (_btnUse.IsEnabled) UseSelected(); };
        root.Children.Add(_list);

        Content = root;
        Loaded += (_, _) => { _query.Focus(); _query.SelectAll(); if (_query.Text.Trim().Length > 0) DoSearch(); };
    }

    private static Button MakeButton(string text, RoutedEventHandler click)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(4, 0, 0, 0), Background = Line, Foreground = Fg, BorderThickness = new Thickness(0) };
        b.Click += click;
        return b;
    }

    private void DoSearch()
    {
        _list.Items.Clear();
        var results = _lib.Search(_query.Text);
        foreach (var r in results) _list.Items.Add(r);
        _detail.Foreground = Fg;
        _detail.Text = _query.Text.Trim().Length == 0
            ? T("Escreve uma referência.", "Type a reference.")
            : results.Count == 0
                ? T("Nada encontrado.", "Nothing found.")
                : T($"{results.Count} ficheiro(s). Escolhe um para ver se é utilizável.", $"{results.Count} file(s). Pick one to see whether it is usable.");
        if (results.Count == 1) _list.SelectedIndex = 0;
    }

    private void ShowSelected()
    {
        _image = null;
        _btnUse.IsEnabled = _btnExtract.IsEnabled = false;
        if (_list.SelectedItem is not ExaminerEntry e) return;
        string from = e.FromSoftware.Count > 0
            ? T($"Atualiza o software: {string.Join(", ", e.FromSoftware)}.", $"Updates software: {string.Join(", ", e.FromSoftware)}.")
            : "";
        if (e.Path == null)
        {
            _detail.Foreground = Warn;
            _detail.Text = T($"✖ O {e.File} está no Global.ndb mas não nesta pasta. ", $"✖ {e.File} is in Global.ndb but not in this folder. ") + from;
            return;
        }
        Cursor = Cursors.Wait;
        try { _image = ExaminerLibrary.OpenImage(e.Path, out var why) is { } img ? img : null; if (_image == null) Fail(why); }
        finally { Cursor = null; }
        if (_image == null) return;

        _detail.Foreground = Ok;
        _detail.Text = T($"✔ ME7.3.1, CRC do .Prm confirmado (0x{_image.Checksum:X4}). {_image.Ident}. ", $"✔ ME7.3.1, .Prm CRC confirmed (0x{_image.Checksum:X4}). {_image.Ident}. ") + from;
        _btnUse.IsEnabled = _btnExtract.IsEnabled = true;

        void Fail(string? why)
        {
            _detail.Foreground = Warn;
            _detail.Text = T($"✖ Não utilizável neste flasher: {why}. ", $"✖ Not usable with this flasher: {why}. ") + from;
        }
    }

    private void UseSelected()
    {
        if (_image == null) return;
        _use(_image.Path);
        Close();
    }

    private void ExtractSelected()
    {
        if (_image == null) return;
        var id = _image.Ident;
        var dlg = new SaveFileDialog
        {
            Title = T("Guardar o original desencriptado", "Save the decrypted original"),
            Filter = T("Binário ECU (*.bin)|*.bin", "ECU binary (*.bin)|*.bin"),
            FileName = $"{Path.GetFileNameWithoutExtension(_image.Path)}_{id.Hardware}_{id.Software}_dec.bin",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllBytes(dlg.FileName, _image.Payload);
            _detail.Foreground = Ok;
            _detail.Text = _image.Profile.HasBackupZone
                ? T($"✔ Guardado ({_image.Payload.Length / 1024} KB): {dlg.FileName}\nNota: a zona 0x38000–0x3FFFF vem em branco no original do Examiner (a ECU gere o backup).",
                    $"✔ Saved ({_image.Payload.Length / 1024} KB): {dlg.FileName}\nNote: the 0x38000–0x3FFFF area is blank in the Examiner original (the ECU manages the backup).")
                : T($"✔ Guardado ({_image.Payload.Length / 1024} KB): {dlg.FileName}\nNota: é a zona 0x4000–0x7FFFF (ME3.1); o arranque 0x0000–0x3FFF não está no ficheiro do Examiner.",
                    $"✔ Saved ({_image.Payload.Length / 1024} KB): {dlg.FileName}\nNote: this is the 0x4000–0x7FFFF area (ME3.1); the 0x0000–0x3FFF boot is not in the Examiner file.");
        }
        catch (Exception ex)
        {
            _detail.Foreground = Warn;
            _detail.Text = T($"✖ Não deu para guardar: {ex.Message}", $"✖ Could not save: {ex.Message}");
        }
    }
}
