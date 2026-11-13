using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Watcher.TerminalAgent.Tests;

// R10 (docs/code-review-2026-10.md): watcher-term-wrap.sh собирает .json через
// printf — доменный логин winbind (CORP\ivanov) или кавычка давали невалидный
// JSON. Тест гоняет настоящий скрипт через sh: stdin не tty, поэтому сама
// обёртка не срабатывает, а функции остаются определены.
public class WrapScriptTests
{
    private static string ScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Watcher.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Watcher.TerminalAgent", "scripts", "watcher-term-wrap.sh");
    }

    private static string RunSh(string snippet, string arg)
    {
        var psi = new ProcessStartInfo("sh") { RedirectStandardOutput = true, RedirectStandardInput = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(". \"$0\"; " + snippet);
        psi.ArgumentList.Add(ScriptPath());
        psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return output;
    }

    [Theory]
    [InlineData(@"CORP\ivanov", @"CORP\ivanov")]
    [InlineData("a\"b", "a\"b")]
    [InlineData(@"x\""y", @"x\""y")]
    [InlineData("tab\there", "tabhere")]
    [InlineData("ivanov@corp.local", "ivanov@corp.local")]
    public void Json_string_roundtrips_through_a_json_parser(string user, string expected)
    {
        string json = RunSh("printf '{\"sessionUser\":\"%s\"}' \"$(_wta_json_str \"$1\")\"", user);

        Assert.Equal(expected, JsonDocument.Parse(json).RootElement.GetProperty("sessionUser").GetString());
    }

    [Theory]
    [InlineData(@"CORP\ivanov", "CORP_ivanov")]
    [InlineData("ivanov@corp.local", "ivanov@corp.local")]
    [InlineData("../x y", ".._x_y")]
    public void File_name_part_has_no_separators(string user, string expected)
    {
        Assert.Equal(expected, RunSh("_wta_name_part \"$1\"", user));
    }

    // 0.14.3: на util-linux < 2.35 (EL8, Ubuntu 20.04) нет --log-out — script
    // падал, а обёртка закрывала вход. Подложный script в PATH: help — со
    // старым или новым набором опций, запуск — записывает аргументы и, если
    // «работает», пишет .out и тайминг как настоящий.
    private static (string Output, string Dir) Record(string help, bool starts, int rc = 0)
    {
        string root = Path.Combine(Path.GetTempPath(), $"wta-{Guid.NewGuid():N}");
        string bin = Path.Combine(root, "bin"), dir = Path.Combine(root, "raw");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(dir);
        string fake = Path.Combine(bin, "script");
        File.WriteAllText(fake, $$"""
            #!/bin/sh
            if [ "$1" = "--help" ]; then echo '{{help}}'; exit 0; fi
            echo "$@" > "{{root}}/args"
            {{(starts ? """
            out=; timing=; prev=
            if [ "$2" = "-t" ]; then out=$3; else for a; do
              [ "$prev" = --log-out ] && out=$a; [ "$prev" = --log-timing ] && timing=$a; prev=$a
            done; fi
            echo 'Script started on 2026-10-20 [COMMAND="sh"]' > "$out"
            if [ -n "$timing" ]; then echo '0.1 5' > "$timing"; else echo '0.1 5' >&2; fi
            """ : "")}}
            exit {{rc}}
            """);
        string output = RunSh($"chmod +x \"{fake}\"; PATH=\"{bin}:$PATH\"; umask 022; _wta_record \"$1\"; echo \"ALIVE $(umask)\"", dir);
        return (output, dir);
    }

    [Fact]
    public void Script_that_does_not_start_keeps_the_shell_and_leaves_nothing()
    {
        var (output, dir) = Record("Usage: script [options] [file]", starts: false, rc: 1);

        Assert.Equal("ALIVE 0022", output.Trim());   // сессия не закрыта, umask вернули
        Assert.Empty(Directory.GetFiles(dir));         // ни .start, ни пустых .out/.timing
    }

    [Fact]
    public void Old_util_linux_uses_classic_syntax_and_records()
    {
        var (output, dir) = Record(" -t[<file>], --timing[=<file>]  output timing data", starts: true);

        Assert.DoesNotContain("ALIVE", output);   // запись шла — по выходе из неё shell закрывается
        string args = File.ReadAllText(Path.Combine(Path.GetDirectoryName(dir)!, "args")).Trim();
        Assert.StartsWith("-qfe -t ", args);
        string id = Path.GetFileNameWithoutExtension(Directory.GetFiles(dir, "*.json").Single());
        Assert.Equal("0.1 5", File.ReadAllText(Path.Combine(dir, id + ".timing")).Trim());
        Assert.Equal(0, JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, id + ".json"))).RootElement.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void New_util_linux_uses_log_out_options()
    {
        var (_, dir) = Record(" -O, --log-out <file>  log stdout to file", starts: true);

        string args = File.ReadAllText(Path.Combine(Path.GetDirectoryName(dir)!, "args")).Trim();
        Assert.Matches("^-qfe --log-out \\S+\\.out --log-timing \\S+\\.timing$", args);
        Assert.Single(Directory.GetFiles(dir, "*.json"));
    }

    // 0.14.4: есть --logging-format — расширенный тайминг (размер окна и его
    // изменения); размер на старте — ещё и в .start/.json (без tty — null).
    [Fact]
    public void Newest_util_linux_uses_advanced_logging_format()
    {
        var (_, dir) = Record(" -O, --log-out <file>  log stdout to file\n -m, --logging-format <name>  force to classic or advanced format", starts: true);

        string args = File.ReadAllText(Path.Combine(Path.GetDirectoryName(dir)!, "args")).Trim();
        Assert.Matches("^-qfe --logging-format advanced --log-out \\S+\\.out --log-timing \\S+\\.timing$", args);
        var meta = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(dir, "*.json").Single())).RootElement;
        Assert.Equal(JsonValueKind.Null, meta.GetProperty("cols").ValueKind);
        Assert.Equal(JsonValueKind.Null, meta.GetProperty("rows").ValueKind);
    }
}
