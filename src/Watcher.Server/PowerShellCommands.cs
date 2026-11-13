using System.Text;
using System.Text.RegularExpressions;

namespace Watcher.Server;

// 0.16: ssh host "…" через Windows OpenSSH, задачи и скрипты автоматизации
// запускают PowerShell с -EncodedCommand <base64 от UTF-16LE> — в записи консоли
// это нечитаемая строка. Расшифровка — для показа и поиска; исходный ввод
// хранится как есть (input), расшифрованный — рядом (input_decoded).
public static partial class PowerShellCommands
{
    // -e, -ec, -en…, -EncodedCommand (powershell.exe принимает сокращения; -ex — это ExecutionPolicy).
    [GeneratedRegex(@"(?<=^|\s)[-/](?:e|ec|en[a-z]*)\s+([A-Za-z0-9+/]{8,}={0,2})(?=\s|$|"")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Encoded();

    // null — расшифровывать нечего.
    public static string? Decode(string? input)
    {
        if (string.IsNullOrEmpty(input)) return null;
        bool any = false;
        string result = Encoded().Replace(input, m =>
        {
            if (TryDecode(m.Groups[1].Value) is not { } script) return m.Value;
            any = true;
            return "-EncodedCommand ⟦" + script + "⟧";
        });
        return any ? result : null;
    }

    private static string? TryDecode(string b64)
    {
        if (b64.Length % 4 != 0) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (FormatException) { return null; }
        if (bytes.Length < 2 || bytes.Length % 2 != 0) return null;
        string text = Encoding.Unicode.GetString(bytes);
        // Не UTF-16-текст (случайное слово из base64-алфавита) — не подменяем.
        int bad = text.Count(ch => ch == '�' || (char.IsControl(ch) && ch is not ('\r' or '\n' or '\t')));
        return bad == 0 ? text : null;
    }
}
