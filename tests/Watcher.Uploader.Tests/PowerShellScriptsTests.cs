using Xunit;

namespace Watcher.Uploader.Tests;

// H2: скрипты PowerShell в репозитории с кириллицей — только UTF-8 с BOM.
// Без BOM Windows PowerShell 5.1 читает их как ANSI (cp1251), и разбор ломается:
// байт 0x84 из «ф» — кавычка «„», команды оказываются внутри строки. Так
// update.ps1 молча пропустил резервную копию, и «битое» обновление оставило
// станцию без службы (живая проверка H2). Тест — здесь, потому что этот
// проект собирается и гоняется на Linux без Windows.
public class PowerShellScriptsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Watcher.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Watcher.sln not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Non_ascii_ps1_files_have_utf8_bom()
    {
        string root = RepoRoot();
        var scripts = Directory.EnumerateFiles(root, "*.ps1", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or "node_modules" or "dist" or ".git"))
            .ToList();

        var broken = scripts.Where(p =>
        {
            byte[] b = File.ReadAllBytes(p);
            bool bom = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
            return !bom && b.Any(x => x >= 0x80);
        }).Select(p => Path.GetRelativePath(root, p)).ToList();
        Assert.True(broken.Count == 0, "без UTF-8 BOM: " + string.Join(", ", broken));
    }
}
