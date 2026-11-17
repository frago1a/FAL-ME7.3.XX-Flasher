using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static ME7Tool.Core.Common.Lang;

namespace ME7Tool.Flasher.Wpf;

/// <summary>Janela "Sobre": versão, autor, licença, créditos e aviso.</summary>
public sealed class AboutWindow : Window
{
    public const string Author = "";
    public const string RepoUrl = "https://github.com/frago1a/FAL-ME7.3.XX-Flasher";

    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x41));
    private static readonly Brush Link = new SolidColorBrush(Color.FromRgb(0x6C, 0xB4, 0xFF));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xC0, 0xA0, 0x00));

    public AboutWindow()
    {
        Title = T("Sobre", "About");
        Width = 520; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Bg; Foreground = Fg; FontFamily = new FontFamily("Segoe UI"); FontSize = 13;

        var v = typeof(AboutWindow).Assembly.GetName().Version;
        string version = v == null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";

        var root = new StackPanel { Margin = new Thickness(18) };

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var icon = new Image { Width = 64, Height = 64, Margin = new Thickness(0, 0, 14, 0) };
        try { icon.Source = BitmapFrame.Create(new Uri("pack://application:,,,/ME7Tool.Flasher.Wpf;component/app.ico")); } catch { /* sem ícone, segue */ }
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "FAL ME7.3.XX Flasher", FontSize = 20, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(new TextBlock { Text = T($"Versão {version}", $"Version {version}"), Foreground = Dim });
        head.Children.Add(titles);
        root.Children.Add(head);

        root.Children.Add(Para(T(
            "Flasher por OBD (K-line, KWP2000) para as centralinas Bosch ME7.3.1 da Fiat, Alfa Romeo e Lancia, e ME3.1 (experimental).",
            "OBD flasher (K-line, KWP2000) for the Bosch ME7.3.1 ECUs of Fiat, Alfa Romeo and Lancia, and ME3.1 (experimental).")));

        root.Children.Add(new TextBlock { Text = $"© 2026 {Author}", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 2) });
        var link = new Hyperlink(new Run(RepoUrl)) { NavigateUri = new Uri(RepoUrl), Foreground = Link };
        link.RequestNavigate += (_, e) =>
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { /* sem browser */ }
            e.Handled = true;
        };
        root.Children.Add(new TextBlock(link) { Margin = new Thickness(0, 0, 0, 10) });

        root.Children.Add(Para(T(
            "Software livre, licença GPL-3.0: podes usar, estudar, alterar e distribuir, nos termos da licença. Sem qualquer garantia.",
            "Free software, GPL-3.0 licence: you may use, study, change and distribute it under the licence terms. No warranty of any kind.")));
        root.Children.Add(Para(T(
            "O procedimento de flash, o algoritmo de segurança e a cifra foram obtidos por engenharia inversa, para interoperabilidade. O seed/key foi confirmado com as ferramentas open source do NefMoto.",
            "The flash procedure, the security algorithm and the encryption were obtained by reverse engineering, for interoperability. The seed/key was cross-checked with the open-source NefMoto tools.")));

        root.Children.Add(new TextBlock
        {
            Text = T("⚠ Usa por tua conta e risco: o flash apaga a ECU. Lê a documentação antes de usar.",
                     "⚠ Use at your own risk: flashing erases the ECU. Read the documentation before using it."),
            TextWrapping = TextWrapping.Wrap, Foreground = Warn, Margin = new Thickness(0, 4, 0, 14),
        });

        var ok = new Button
        {
            Content = "OK", Width = 90, Padding = new Thickness(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Right,
            Background = Line, Foreground = Fg, BorderThickness = new Thickness(0), IsDefault = true, IsCancel = true,
        };
        ok.Click += (_, _) => Close();
        root.Children.Add(ok);

        Content = root;
    }

    private static TextBlock Para(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Dim, Margin = new Thickness(0, 0, 0, 8) };
}
