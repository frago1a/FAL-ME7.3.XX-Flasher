using System.Globalization;

namespace ME7Tool.Core.Common;

/// <summary>
/// Língua das mensagens (logs, erros, avisos): português ou inglês. As mensagens trazem as duas versões
/// lado a lado — <c>T("texto em português", "text in English")</c> — e esta classe escolhe.
/// A escolha é guardada num ficheiro comum ao flasher e ao logger (<see cref="Load"/>/<see cref="Save"/>);
/// sem ficheiro, segue a língua do Windows (português → PT, tudo o resto → EN).
/// </summary>
public static class Lang
{
    /// <summary>true = inglês; false = português.</summary>
    public static bool English { get; set; }

    /// <summary>Código da língua atual: "pt" ou "en".</summary>
    public static string Code => English ? "en" : "pt";

    /// <summary>Escolhe a versão da mensagem na língua atual.</summary>
    public static string T(string pt, string en) => English ? en : pt;

    /// <summary>Ficheiro da escolha (partilhado pelas duas aplicações).</summary>
    public static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ME7Tool", "language.txt");

    /// <summary>Lê a escolha guardada (ou a língua do Windows) e aplica-a.</summary>
    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                English = File.ReadAllText(SettingsFile).Trim().Equals("en", StringComparison.OrdinalIgnoreCase);
                return;
            }
        }
        catch { /* sem acesso ao ficheiro: segue a língua do Windows */ }
        English = !CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("pt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Guarda a escolha (vale para o flasher e o logger).</summary>
    public static void Save(bool english)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, english ? "en" : "pt");
    }
}
