using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ME7Tool.Core.Common;

namespace ME7Tool.Wpf.Shared;

/// <summary>
/// Traduz os textos fixos de uma janela (escritos em português no XAML) para inglês, ao abrir: percorre a
/// árvore e troca Title, Text, Content, Header e ToolTip que existam no dicionário <c>i18n_en.txt</c>
/// (recurso embutido na aplicação; uma linha por texto: <c>português&lt;TAB&gt;inglês</c>, <c>\n</c> = mudança
/// de linha). Textos fora do dicionário (valores, nomes, números) ficam como estão. Em português não faz nada.
/// </summary>
public static class UiLocalizer
{
    private static Dictionary<string, string>? _dict;

    /// <summary>Dicionário PT → EN da aplicação (lido uma vez do recurso embutido).</summary>
    public static Dictionary<string, string> Dictionary => _dict ??= LoadDictionary(Assembly.GetEntryAssembly()!);

    public static Dictionary<string, string> LoadDictionary(Assembly asm)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        using var s = asm.GetManifestResourceStream("i18n_en.txt");
        if (s == null) return d;
        using var r = new StreamReader(s);
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            d[Unescape(line[..tab])] = Unescape(line[(tab + 1)..]);
        }
        return d;
    }

    private static string Unescape(string s) => s.Replace("\\n", "\n");

    /// <summary>Texto fixo na língua atual (para textos que o código põe em controlos).</summary>
    public static string Tr(string pt) => Lang.English && Dictionary.TryGetValue(pt, out var en) ? en : pt;

    /// <summary>Traduz a janela (chamar depois de InitializeComponent).</summary>
    public static void Apply(Window w)
    {
        if (!Lang.English) return;
        if (w.Title is { Length: > 0 } t) w.Title = Tr(t);
        Walk(w, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    private static void Walk(object node, HashSet<object> seen)
    {
        if (node is not DependencyObject d || !seen.Add(node)) return;

        switch (d)
        {
            case TextBlock tb when tb.Text is { Length: > 0 } x: tb.Text = Tr(Normalize(x)); break;
            case TextBox: break; // valores escritos pelo utilizador
            case HeaderedContentControl h when h.Header is string hs: h.Header = Tr(hs); break; // GroupBox, TabItem
            case HeaderedItemsControl hi when hi.Header is string his: hi.Header = Tr(his); break;  // MenuItem
        }
        if (d is ContentControl { Content: string cs } cc && d is not HeaderedContentControl) cc.Content = Tr(cs);
        if (d is FrameworkElement { ToolTip: string tip } fe) fe.ToolTip = Tr(tip);

        if (d is DataGrid g)
            foreach (var col in g.Columns)
                if (col.Header is string ch) col.Header = Tr(ch);

        // filhos: árvore lógica (apanha MenuItems e ComboBoxItems que ainda não foram desenhados)
        foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<object>().ToList())
            Walk(child, seen);
        if (d is ItemsControl ic && ic.ItemsSource == null)
            foreach (var item in ic.Items.OfType<object>().ToList())
                if (item is DependencyObject) Walk(item, seen);
    }

    private static string Normalize(string s) => s.Contains('\n') ? string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) : s;

    /// <summary>
    /// Pede para mudar de língua: guarda a escolha e reabre a aplicação (os textos fixos só se trocam ao abrir).
    /// </summary>
    public static void SwitchLanguage(Window owner, bool english, Func<bool>? canRestart = null)
    {
        if (english == Lang.English) return;
        if (canRestart != null && !canRestart()) return;
        Lang.Save(english);
        string msg = english
            ? "Language set to English. The program will reopen now."
            : "Língua alterada para português. O programa vai reabrir agora.";
        MessageBox.Show(owner, msg, "ME7Tool", MessageBoxButton.OK, MessageBoxImage.Information);
        try
        {
            string exe = Environment.ProcessPath!;
            System.Diagnostics.Process.Start(exe);
            Application.Current.Shutdown();
        }
        catch { /* se não der para reabrir, fica para o próximo arranque */ }
    }
}
