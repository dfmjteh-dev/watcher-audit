using Watcher.SessionService;
using Xunit;

namespace Watcher.SessionService.Tests;

// Задача F1: транскрипты — реальные, со стенда спайка F0 (русская локаль), и
// та же структура в английской локали.
public class TranscriptParserTests
{
    private const string Header = """
**********************
Начало записи сценария Windows PowerShell
Время начала: 20261008153328
Имя пользователя: PC-1\wt-ssh
Запуск от имени пользователя: PC-1\wt-ssh
Имя конфигурации: 
Компьютер: PC-1 (Microsoft Windows NT 10.0.26200.0)
Ведущее приложение: c:\windows\system32\windowspowershell\v1.0\powershell.exe
ИД процесса: 4040
PSVersion: 5.1.26100.9444
PSEdition: Desktop
PSCompatibleVersions: 1.0, 2.0, 3.0, 4.0, 5.0, 5.1.26100.9444
BuildVersion: 10.0.26100.9444
CLRVersion: 4.0.30319.42000
WSManStackVersion: 3.0
PSRemotingProtocolVersion: 2.3
SerializationVersion: 1.1.0.1
**********************
""";

    private const string Interactive = Header + """
**********************
Время запуска команды: 20261008153336
**********************
PS C:\Users\wt-ssh> whoami
pc-1\wt-ssh
**********************
Время запуска команды: 20261008153340
**********************
PS C:\Users\wt-ssh> echo "кириллица-ок"
кириллица-ок
**********************
Время запуска команды: 20261008153342
**********************
PS C:\Users\wt-ssh> cmd /c ver
Microsoft Windows [Version 10.0.26200.9457]
**********************
Время запуска команды: 20261008153344
**********************
PS C:\Users\wt-ssh> exit
**********************
Конец записи протокола Windows PowerShell
Время окончания: 20261008153344
**********************
""";

    [Fact]
    public void Interactive_session_header_commands_and_end()
    {
        var t = TranscriptParser.Parse("\uFEFF" + Interactive.Replace("\n", "\r\n"))!;

        Assert.Equal(new DateTime(2026, 10, 8, 15, 33, 28), t.StartedLocal);
        Assert.Equal(new DateTime(2026, 10, 8, 15, 33, 44), t.EndedLocal);
        Assert.True(t.Completed);
        Assert.Equal(@"PC-1\wt-ssh", t.User);
        Assert.Equal("wt-ssh", t.SessionUser);
        Assert.Equal(4040, t.ProcessId);
        Assert.Equal("Windows PowerShell 5.1.26100.9444", t.Shell);
        Assert.Equal(new[] { "whoami", "echo \"кириллица-ок\"", "cmd /c ver", "exit" }, t.Commands);
    }

    [Fact]
    public void Command_mode_drops_global_status_artifact()
    {
        string text = Header + """
**********************
Время запуска команды: 20261008153357
**********************
PS>Write-Output "F0-command-mode"; 2+2
F0-command-mode
4
**********************
Время запуска команды: 20261008153357
**********************
PS>$global:?
True
**********************
Конец записи протокола Windows PowerShell
Время окончания: 20261008153357
**********************
""";
        var t = TranscriptParser.Parse(text)!;
        Assert.Equal(new[] { "Write-Output \"F0-command-mode\"; 2+2" }, t.Commands);
    }

    [Fact]
    public void Stop_transcript_restart_keeps_first_header_and_skips_diagnostics()
    {
        string text = Header + """
**********************
Время запуска команды: 20261008153912
**********************
PS C:\Users\wt-ssh> Stop-Transcript
**********************
Время запуска команды: 20261008153913
**********************
PS C:\Users\wt-ssh> TerminatingError(Stop-Transcript): "Произошла ошибка при остановке транскрибирования"
**********************
Начало записи сценария Windows PowerShell
Время начала: 20261008153913
Имя пользователя: PC-1\wt-ssh
Запуск от имени пользователя: PC-1\wt-ssh
Имя конфигурации: 
Компьютер: PC-1 (Microsoft Windows NT 10.0.26200.0)
Ведущее приложение: c:\windows\system32\windowspowershell\v1.0\powershell.exe
ИД процесса: 1892
PSVersion: 5.1.26100.9444
**********************
**********************
Время запуска команды: 20261008153913
**********************
PS>CommandInvocation(Out-String): "Out-String"
>> ParameterBinding(Out-String): имя="InputObject"
**********************
Время запуска команды: 20261008153914
**********************
PS C:\Users\wt-ssh> Write-Output "after-stop"
after-stop
""";
        var t = TranscriptParser.Parse(text)!;
        Assert.Equal(4040, t.ProcessId);
        Assert.Equal(new DateTime(2026, 10, 8, 15, 33, 28), t.StartedLocal);
        Assert.Equal(new[] { "Stop-Transcript", "Write-Output \"after-stop\"" }, t.Commands);
        // Сессия оборвана — без блока окончания.
        Assert.False(t.Completed);
    }

    [Fact]
    public void English_locale_parses_by_position()
    {
        string text = """
**********************
Windows PowerShell transcript start
Start time: 20261008120000
Username: CORP\alice
RunAs User: CORP\alice
Configuration Name: 
Machine: SRV-1 (Microsoft Windows NT 10.0.17763.0)
Host Application: C:\Program Files\PowerShell\7\pwsh.dll
Process ID: 777
PSVersion: 7.4.6
PSEdition: Core
**********************
**********************
Command start time: 20261008120005
**********************
PS C:\Users\alice> Get-Service sshd |
>> Select-Object Status
Running
**********************
PowerShell transcript end
End time: 20261008120010
**********************
""";
        var t = TranscriptParser.Parse(text)!;
        Assert.Equal("alice", t.SessionUser);
        Assert.Equal(777, t.ProcessId);
        Assert.Equal("PowerShell 7.4.6", t.Shell);
        Assert.Equal(new[] { "Get-Service sshd |\nSelect-Object Status" }, t.Commands);
        Assert.Equal(new DateTime(2026, 10, 8, 12, 0, 10), t.EndedLocal);
    }

    [Fact]
    public void Run_as_user_is_parsed()
    {
        var t = TranscriptParser.Parse(Header)!;
        Assert.Equal(@"PC-1\wt-ssh", t.RunAsUser);
    }

    [Fact]
    public void Header_only_session_has_no_commands()
    {
        var t = TranscriptParser.Parse(Header)!;
        Assert.Empty(t.Commands);
        Assert.False(t.Completed);
    }

    [Fact]
    public void Garbage_is_not_a_transcript()
    {
        Assert.Null(TranscriptParser.Parse("hello\nworld"));
        Assert.Null(TranscriptParser.Parse(""));
    }

    // Ведущее приложение — многострочный -Command (CompatTelRunner, реальный
    // заголовок с VM-AVZ-SUPPORT-1C, 2026-10-20): PID был 0 → ложная потеря
    // transcript-no-4688 через сутки.
    private const string MultilineHeaderRu = """
**********************
Начало записи сценария Windows PowerShell
Время начала: 20261020045848
Имя пользователя: AVZ\СИСТЕМА
Запуск от имени пользователя: AVZ\СИСТЕМА
Имя конфигурации: 
Компьютер: VM-AVZ-SUPPORT- (Microsoft Windows NT 10.0.19045.0)
Ведущее приложение: powershell.exe -ExecutionPolicy Restricted -Command
 $Res = 0
 $InboxIMEs = (@(
   'ja-JP', 'ko-KR'
 ))
 Write-Host 'Final result:',$Res
ИД процесса: 4472
PSVersion: 5.1.19041.6456
PSEdition: Desktop
**********************
""";

    [Fact]
    public void Multiline_host_application_keeps_pid_and_whole_command()
    {
        var t = TranscriptParser.Parse(MultilineHeaderRu)!;

        Assert.Equal(4472, t.ProcessId);
        Assert.StartsWith("powershell.exe -ExecutionPolicy Restricted -Command\n $Res = 0", t.HostApplication);
        Assert.EndsWith("Write-Host 'Final result:',$Res", t.HostApplication);
        Assert.Equal("5.1.19041.6456", t.PsVersion);
    }

    [Fact]
    public void Multiline_host_application_english_locale_and_label_like_line_inside()
    {
        string header = """
**********************
Windows PowerShell transcript start
Start time: 20261020045848
Username: CORP\svc
RunAs User: CORP\svc
Configuration Name: 
Machine: PC-2 (Microsoft Windows NT 10.0.19045.0)
Host Application: powershell.exe -Command
 $Port: 8080
 Get-Item C:\x
Process ID: 991
PSVersion: 5.1.19041.6456
**********************
""";
        var t = TranscriptParser.Parse(header)!;

        Assert.Equal(991, t.ProcessId);   // «$Port: 8080» внутри скрипта — не PID
        Assert.Equal("powershell.exe -Command\n $Port: 8080\n Get-Item C:\\x", t.HostApplication);
    }

    [Fact]
    public void Single_line_host_application_unchanged()
    {
        var t = TranscriptParser.Parse(Header)!;

        Assert.Equal(4040, t.ProcessId);
        Assert.Equal(@"c:\windows\system32\windowspowershell\v1.0\powershell.exe", t.HostApplication);
    }
}
