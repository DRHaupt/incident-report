using System.Globalization;
using System.Text.Json;
using IncidentReporting.Models;
using Microsoft.Data.Sqlite;

namespace IncidentReporting.Data;

public sealed class ReportQuery
{
    public string? SearchText { get; set; }
    public ReportStatus? Status { get; set; }
    public string? Category { get; set; }
    public string? FormCode { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

public sealed class AuditEntry
{
    public DateTime TimestampUtc { get; init; }
    public string User { get; init; } = "";
    public string Action { get; init; } = "";
    public string Details { get; init; } = "";
}

/// <summary>SQLite-backed storage for incident reports and their audit trail.</summary>
public sealed class ReportRepository
{
    private readonly string _connectionString;

    public ReportRepository(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true }.ToString();
        EnsureSchema();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void EnsureSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Reports (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                ReportNumber  TEXT NOT NULL UNIQUE,
                FormCode      TEXT NOT NULL,
                Category      TEXT NOT NULL,
                Status        INTEGER NOT NULL,
                Title         TEXT NOT NULL DEFAULT '',
                Severity      TEXT NOT NULL DEFAULT '',
                Location      TEXT NOT NULL DEFAULT '',
                Department    TEXT NOT NULL DEFAULT '',
                ReportedBy    TEXT NOT NULL DEFAULT '',
                IncidentDate  TEXT NULL,
                CreatedUtc    TEXT NOT NULL,
                ModifiedUtc   TEXT NOT NULL,
                ValuesJson    TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Reports_Status ON Reports(Status);
            CREATE INDEX IF NOT EXISTS IX_Reports_IncidentDate ON Reports(IncidentDate);

            CREATE TABLE IF NOT EXISTS AuditLog (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                ReportId     INTEGER NOT NULL REFERENCES Reports(Id) ON DELETE CASCADE,
                TimestampUtc TEXT NOT NULL,
                UserName     TEXT NOT NULL,
                Action       TEXT NOT NULL,
                Details      TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS IX_AuditLog_ReportId ON AuditLog(ReportId);

            CREATE TABLE IF NOT EXISTS Attachments (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                ReportId     INTEGER NOT NULL REFERENCES Reports(Id) ON DELETE CASCADE,
                FileName     TEXT NOT NULL,
                Description  TEXT NOT NULL DEFAULT '',
                AddedUtc     TEXT NOT NULL,
                Content      BLOB NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Attachments_ReportId ON Attachments(ReportId);
            """;
        cmd.ExecuteNonQuery();
    }

    public string NextReportNumber(string prefix)
    {
        var year = DateTime.Now.Year.ToString(CultureInfo.InvariantCulture);
        var stem = $"{prefix}-{year}-";
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ReportNumber FROM Reports WHERE ReportNumber LIKE $stem || '%'";
        cmd.Parameters.AddWithValue("$stem", stem);
        var max = 0;
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var tail = r.GetString(0)[stem.Length..];
                if (int.TryParse(tail, out var n) && n > max)
                    max = n;
            }
        }
        return $"{stem}{max + 1:D5}";
    }

    public void Save(IncidentReport report, string category, string user)
    {
        var now = DateTime.UtcNow;
        report.ModifiedUtc = now;
        var isNew = report.Id == 0;
        if (isNew)
            report.CreatedUtc = now;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = isNew
                ? """
                  INSERT INTO Reports (ReportNumber, FormCode, Category, Status, Title, Severity, Location, Department,
                                       ReportedBy, IncidentDate, CreatedUtc, ModifiedUtc, ValuesJson)
                  VALUES ($num, $form, $cat, $status, $title, $sev, $loc, $dept, $by, $date, $created, $modified, $json);
                  SELECT last_insert_rowid();
                  """
                : """
                  UPDATE Reports SET Status=$status, Title=$title, Severity=$sev, Location=$loc, Department=$dept,
                         ReportedBy=$by, IncidentDate=$date, ModifiedUtc=$modified, ValuesJson=$json
                  WHERE Id=$id;
                  SELECT $id;
                  """;
            cmd.Parameters.AddWithValue("$id", report.Id);
            cmd.Parameters.AddWithValue("$num", report.ReportNumber);
            cmd.Parameters.AddWithValue("$form", report.FormCode);
            cmd.Parameters.AddWithValue("$cat", category);
            cmd.Parameters.AddWithValue("$status", (int)report.Status);
            cmd.Parameters.AddWithValue("$title", report.Title);
            cmd.Parameters.AddWithValue("$sev", report.Severity);
            cmd.Parameters.AddWithValue("$loc", report.Location);
            cmd.Parameters.AddWithValue("$dept", report.Department);
            cmd.Parameters.AddWithValue("$by", report.ReportedBy);
            cmd.Parameters.AddWithValue("$date", (object?)report.IncidentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", report.CreatedUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$modified", now.ToString("o"));
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(report.Values));
            report.Id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        AddAudit(conn, tx, report.Id, user, isNew ? "Created" : "Saved", $"Status: {report.Status.ToDisplay()}");
        tx.Commit();
    }

    public void SetStatus(IncidentReport report, ReportStatus status, string user, string details = "")
    {
        var previous = report.Status;
        report.Status = status;
        report.ModifiedUtc = DateTime.UtcNow;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE Reports SET Status=$s, ModifiedUtc=$m WHERE Id=$id";
            cmd.Parameters.AddWithValue("$s", (int)status);
            cmd.Parameters.AddWithValue("$m", report.ModifiedUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$id", report.Id);
            cmd.ExecuteNonQuery();
        }
        var text = $"{previous.ToDisplay()} -> {status.ToDisplay()}";
        if (!string.IsNullOrWhiteSpace(details))
            text += $": {details}";
        AddAudit(conn, tx, report.Id, user, "Status Changed", text);
        tx.Commit();
    }

    /// <summary>Writes a consistent copy of the database to the given path using SQLite's online backup.</summary>
    public void Backup(string destinationPath)
    {
        if (File.Exists(destinationPath))
            File.Delete(destinationPath);
        using var source = Open();
        using var dest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destinationPath, Pooling = false }.ToString());
        dest.Open();
        source.BackupDatabase(dest);
    }

    public void Delete(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Reports WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public IncidentReport? Get(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, ReportNumber, FormCode, Status, CreatedUtc, ModifiedUtc, ValuesJson FROM Reports WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadReport(r) : null;
    }

    public List<IncidentReport> Search(ReportQuery q)
    {
        var where = new List<string>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        if (q.Status is { } status)
        {
            where.Add("Status = $status");
            cmd.Parameters.AddWithValue("$status", (int)status);
        }
        if (!string.IsNullOrEmpty(q.Category))
        {
            where.Add("Category = $cat");
            cmd.Parameters.AddWithValue("$cat", q.Category);
        }
        if (!string.IsNullOrEmpty(q.FormCode))
        {
            where.Add("FormCode = $form");
            cmd.Parameters.AddWithValue("$form", q.FormCode);
        }
        if (q.From is { } from)
        {
            where.Add("IncidentDate >= $from");
            cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        if (q.To is { } to)
        {
            where.Add("IncidentDate <= $to");
            cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(q.SearchText))
        {
            where.Add("(ReportNumber LIKE $q OR Title LIKE $q OR Location LIKE $q OR ReportedBy LIKE $q OR Department LIKE $q OR ValuesJson LIKE $q)");
            cmd.Parameters.AddWithValue("$q", "%" + q.SearchText.Trim() + "%");
        }
        cmd.CommandText = "SELECT Id, ReportNumber, FormCode, Status, CreatedUtc, ModifiedUtc, ValuesJson FROM Reports"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY ModifiedUtc DESC";
        var list = new List<IncidentReport>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadReport(r));
        return list;
    }

    public Dictionary<ReportStatus, int> CountByStatus()
    {
        var result = Enum.GetValues<ReportStatus>().ToDictionary(s => s, _ => 0);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Status, COUNT(*) FROM Reports GROUP BY Status";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result[(ReportStatus)r.GetInt32(0)] = r.GetInt32(1);
        return result;
    }

    public List<AuditEntry> GetAudit(long reportId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TimestampUtc, UserName, Action, Details FROM AuditLog WHERE ReportId=$id ORDER BY Id";
        cmd.Parameters.AddWithValue("$id", reportId);
        var list = new List<AuditEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AuditEntry
            {
                TimestampUtc = DateTime.Parse(r.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                User = r.GetString(1),
                Action = r.GetString(2),
                Details = r.GetString(3)
            });
        }
        return list;
    }

    public void AddAudit(long reportId, string user, string action, string details)
    {
        using var conn = Open();
        AddAudit(conn, null, reportId, user, action, details);
    }

    private static void AddAudit(SqliteConnection conn, SqliteTransaction? tx, long reportId, string user, string action, string details)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO AuditLog (ReportId, TimestampUtc, UserName, Action, Details) VALUES ($r, $t, $u, $a, $d)";
        cmd.Parameters.AddWithValue("$r", reportId);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$u", user);
        cmd.Parameters.AddWithValue("$a", action);
        cmd.Parameters.AddWithValue("$d", details);
        cmd.ExecuteNonQuery();
    }

    // ----- Attachments -----

    public sealed record AttachmentInfo(long Id, string FileName, string Description, DateTime AddedUtc, long Size);

    public List<AttachmentInfo> GetAttachments(long reportId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, FileName, Description, AddedUtc, length(Content) FROM Attachments WHERE ReportId=$id ORDER BY Id";
        cmd.Parameters.AddWithValue("$id", reportId);
        var list = new List<AttachmentInfo>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AttachmentInfo(r.GetInt64(0), r.GetString(1), r.GetString(2),
                DateTime.Parse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), r.GetInt64(4)));
        }
        return list;
    }

    public void AddAttachment(long reportId, string filePath, string description, string user)
    {
        var bytes = File.ReadAllBytes(filePath);
        var name = Path.GetFileName(filePath);
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO Attachments (ReportId, FileName, Description, AddedUtc, Content) VALUES ($r, $f, $d, $t, $c)";
            cmd.Parameters.AddWithValue("$r", reportId);
            cmd.Parameters.AddWithValue("$f", name);
            cmd.Parameters.AddWithValue("$d", description);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$c", bytes);
            cmd.ExecuteNonQuery();
        }
        AddAudit(conn, tx, reportId, user, "Attachment Added", name);
        tx.Commit();
    }

    public void SaveAttachmentTo(long attachmentId, string destinationPath)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Content FROM Attachments WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", attachmentId);
        if (cmd.ExecuteScalar() is byte[] bytes)
            File.WriteAllBytes(destinationPath, bytes);
    }

    public void DeleteAttachment(long reportId, long attachmentId, string fileName, string user)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM Attachments WHERE Id=$id";
            cmd.Parameters.AddWithValue("$id", attachmentId);
            cmd.ExecuteNonQuery();
        }
        AddAudit(conn, tx, reportId, user, "Attachment Removed", fileName);
        tx.Commit();
    }

    private static IncidentReport ReadReport(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        ReportNumber = r.GetString(1),
        FormCode = r.GetString(2),
        Status = (ReportStatus)r.GetInt32(3),
        CreatedUtc = DateTime.Parse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        ModifiedUtc = DateTime.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Values = new Dictionary<string, string>(
            JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(6)) ?? new(),
            StringComparer.OrdinalIgnoreCase)
    };
}
