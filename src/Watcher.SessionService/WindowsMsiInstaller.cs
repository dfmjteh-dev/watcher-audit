using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Watcher.Uploader;

namespace Watcher.SessionService;

// 0.13 (п.16): обновление агента, поставленного из MSI, — новым MSI, а не
// заменой файлов: иначе в «Программах и компонентах» навсегда оставалась
// версия первой установки, а удаление по MSI снимало чужие файлы.
//
// msiexec /i с MajorUpgrade (installer/watcher-agent.wxs): прежняя версия
// удаляется (её RunUninstallScript останавливает службу и стирает конфиги
// Watcher.*.json), новая ставится и её установщик пишет конфиги заново — поэтому
// адрес сервера, ключ обновлений и аудит консоли передаются свойствами из
// текущего конфига службы. Учётка агента (ProgramData\Watcher\agent) при
// удалении не трогается — токен регистрации не нужен. Ошибка MSI сама по себе
// не подтверждает восстановление старой службы: его нужно проверять отдельно.
//
// Одноразовой задачей Планировщика от SYSTEM (UpdateTask): MSI
// останавливает эту службу. 0.16: без PowerShell — задача запускает копию этого
// же exe в режиме помощника (--apply-msi-update, MsiUpdateHelper): из каталога
// агента нельзя, его exe заменяет сам msiexec. Параметры — в job.json рядом с
// пакетом (в /TR не больше 261 символа, а ключ обновлений длинный).
[SupportedOSPlatform("windows")]
public sealed class WindowsMsiInstaller : IUpdateInstaller
{
    private readonly string _installDir;
    private readonly string _serverUrl;
    private readonly string _updateKey;
    private readonly bool _terminalAudit;
    private readonly ILogger _logger;

    public WindowsMsiInstaller(string installDir, string serverUrl, string updateKey, bool terminalAudit, ILogger logger)
    {
        _installDir = Path.GetFullPath(installDir).TrimEnd('\\');
        _serverUrl = serverUrl;
        _updateKey = updateKey;
        _terminalAudit = terminalAudit;
        _logger = logger;
    }

    public string Platform => "windows-msi";

    public bool InProgress() => UpdateTask.TaskRunning(_logger);

    public void Start(string packagePath, string version, string workDir, string resultFile)
    {
        var job = Job(packagePath, version, resultFile, Path.Combine(workDir, "msi.log"), _installDir, _serverUrl, _updateKey, _terminalAudit);
        string jobFile = Path.Combine(workDir, "job.json");
        File.WriteAllText(jobFile, JsonSerializer.Serialize(job, MsiUpdateHelper.Json));
        string helper = Path.Combine(workDir, "update-helper.exe");
        File.Copy(Path.Combine(_installDir, "Watcher.SessionService.exe"), helper, overwrite: true);
        UpdateTask.RunTask($"\"{helper}\" --apply-msi-update \"{jobFile}\"");
        _logger.LogInformation("update {Version}: msiexec task {Task} started, service will restart", version, UpdateTask.TaskName);
    }

    // Параметры задачи — отдельно от запуска, чтобы проверять тестом.
    public static MsiUpdateJob Job(string msi, string version, string resultFile, string log, string installDir, string serverUrl, string updateKey, bool terminalAudit) =>
        new(msi, version, resultFile, log, installDir, serverUrl, updateKey, terminalAudit);
}

// Установщик обновлений службы: Windows-агент обновляется только MSI (0.16:
// zip-пакеты и update.ps1 убраны — на проде все Windows-агенты из MSI). Агент не
// из MSI или ещё без своей учётки (MSI-апгрейду нечем заменить токен
// регистрации) платформу не сообщает — сервер ему обновления не предлагает.
// Платформа уходит серверу с каждым heartbeat, сервер предлагает пакет под неё.
[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateInstaller : IUpdateInstaller
{
    private readonly WindowsMsiInstaller _msi;
    private readonly string _credentialPath;

    public WindowsUpdateInstaller(WindowsMsiInstaller msi, string credentialPath)
    {
        _msi = msi;
        _credentialPath = credentialPath;
    }

    public static string? Choose(bool msiInstalled, bool hasCredential) =>
        msiInstalled && hasCredential ? "windows-msi" : null;

    public string? Platform => Choose(WindowsSelfRemover.InstalledProductCode() is not null, File.Exists(_credentialPath));

    public bool InProgress() => _msi.InProgress();

    public void Start(string packagePath, string version, string workDir, string resultFile) =>
        _msi.Start(packagePath, version, workDir, resultFile);
}
