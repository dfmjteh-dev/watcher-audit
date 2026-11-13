using System.Diagnostics.CodeAnalysis;

namespace Watcher.Server;

// 0.13 (проверка RC): удалить выбранные записи хоста — сегменты видео и
// консольные сессии (с выводом, привязанным к сессии). Только admin, каждое
// удаление — в журнал доступа (RecordingEndpoints); записи под действующим
// legal hold не удаляются (как и при удалении хоста).
public sealed record RecordDeleteResult(int Segments, int TerminalSessions, int Held, List<string> Files);

public sealed partial class Db
{
    [SuppressMessage("Security", "CA2100", Justification = "SQL — константы кода и имена параметров (AddInParams); значения — только через Parameters.")]
    public RecordDeleteResult DeleteRecords(string host, IReadOnlyCollection<long> segmentIds, IReadOnlyCollection<long> terminalIds)
    {
        using var totals = TotalsChange();
        using var c = Open();
        using var tx = c.BeginTransaction();
        var files = new List<string>();
        int held = 0, segments = 0, terminal = 0;

        // Чужой хост или выдуманный id — просто не находится: только записи этого хоста.
        List<long> Pick(string table, IReadOnlyCollection<long> ids, string extraCols, Action<Microsoft.Data.Sqlite.SqliteDataReader> collect)
        {
            var keep = new List<long>();
            if (ids.Count == 0) return keep;
            using var q = c.CreateCommand();
            q.Transaction = tx;
            q.CommandText = $"SELECT t.id, {HeldClause("t")}{extraCols} FROM {table} t WHERE t.host = $h AND t.id IN ({AddInParams(q, "d", ids)})";
            q.Parameters.AddWithValue("$h", host);
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                if (r.GetInt64(1) != 0) { held++; continue; }
                keep.Add(r.GetInt64(0));
                collect(r);
            }
            return keep;
        }

        var segTimes = new List<(DateTime S, DateTime E)>();
        var segs = Pick("segments", segmentIds, ", t.file_path, t.started_at, t.ended_at", r =>
        {
            files.Add(r.GetString(2));
            segTimes.Add((ParseUtc(r.GetString(3)), ParseUtc(r.GetString(4))));
        });
        // Заготовки отчётов этих суток — пересчитать без удалённых записей.
        foreach (var (s, e) in segTimes) MarkReportDirty(c, tx, host, s, e);
        // Сессия — вместе с выводом, привязанным к ней (parent_id).
        var termRoots = Pick("terminal_sessions", terminalIds, "", _ => { });
        var terms = new List<long>(termRoots);
        if (termRoots.Count > 0)
        {
            using var q = c.CreateCommand();
            q.Transaction = tx;
            q.CommandText = $"SELECT id FROM terminal_sessions WHERE parent_id IN ({AddInParams(q, "p", termRoots)})";
            using var r = q.ExecuteReader();
            while (r.Read()) terms.Add(r.GetInt64(0));
        }
        if (terms.Count > 0)
        {
            using var q = c.CreateCommand();
            q.Transaction = tx;
            q.CommandText = $"SELECT out_file_path, timing_file_path FROM terminal_sessions WHERE id IN ({AddInParams(q, "f", terms)})";
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                if (!r.IsDBNull(0)) files.Add(r.GetString(0));
                if (!r.IsDBNull(1)) files.Add(r.GetString(1));
            }
        }

        if (segs.Count > 0)
        {
            using var d = c.CreateCommand();
            d.Transaction = tx;
            d.CommandText = $"DELETE FROM segments WHERE id IN ({AddInParams(d, "s", segs)})";   // segment_activity — триггером
            segments = d.ExecuteNonQuery();
        }
        if (terms.Count > 0)
        {
            using var d = c.CreateCommand();
            d.Transaction = tx;
            d.CommandText = $"DELETE FROM terminal_sessions WHERE id IN ({AddInParams(d, "t", terms)})";
            terminal = d.ExecuteNonQuery();
        }
        tx.Commit();
        return new RecordDeleteResult(segments, terminal, held, files);
    }
}
