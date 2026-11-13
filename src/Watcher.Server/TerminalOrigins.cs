using Microsoft.Data.Sqlite;

namespace Watcher.Server;

// 0.16: метки транскриптов PowerShell от Windows-агента (TranscriptKind на агенте).
public static class TerminalOrigins
{
    public static readonly string[] Origins = ["ssh", "automation", "sql-agent", "local", "winrm", "unknown"];
    public static readonly string[] Modes = ["interactive", "exec"];

    public static bool IsKnown(string? origin) => origin is not null && Origins.Contains(origin);

    // Чужое значение не сохраняем: метка только из известного набора.
    public static string? Origin(string? value) => IsKnown(value) ? value : null;
    public static string? Mode(string? value) => value is not null && Modes.Contains(value) ? value : null;
}

public sealed partial class Db
{
    // Миграция 33: метки транскриптов, расшифрованный -EncodedCommand прежних
    // записей и подтверждение потерь transcript-no-4688 — с 0.16 такие транскрипты
    // выгружаются (origin=unknown), а не теряются; строки журнала остаются.
    private static void TranscriptOriginsMigration(SqliteConnection c)
    {
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = @"ALTER TABLE terminal_sessions ADD COLUMN origin TEXT;
ALTER TABLE terminal_sessions ADD COLUMN mode TEXT;
ALTER TABLE terminal_sessions ADD COLUMN input_decoded TEXT;
UPDATE host_losses SET acknowledged_at = $now, acknowledged_by = 'system: transcripts uploaded since 0.16'
WHERE reason = 'transcript-no-4688' AND acknowledged_at IS NULL;";
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        var decoded = new List<(long Id, string Text)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, input FROM terminal_sessions WHERE source = 'win-transcript' AND kind = 'terminal-input' AND input LIKE '%-e%'";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (!r.IsDBNull(1) && PowerShellCommands.Decode(r.GetString(1)) is { } text) decoded.Add((r.GetInt64(0), text));
        }
        foreach (var (id, text) in decoded)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE terminal_sessions SET input_decoded = $d, search_text = COALESCE(search_text, '') || char(10) || $d WHERE id = $id";
            cmd.Parameters.AddWithValue("$d", text);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }
}
