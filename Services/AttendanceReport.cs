using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using ClosedXML.Excel;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace SmartClassAC.Services;

internal sealed record AttendanceReportFilter(DateOnly From, DateOnly To, long? TeacherId, long? ClassroomId, string Outcome);
internal sealed record AttendanceReportRow(DateOnly Date, string Teacher, string Classroom, string Subject,
    DateTime? TimeIn, DateTime? TimeOut, string Verification, string Outcome, bool ActiveLate = false);
internal sealed record AttendanceReportData(AttendanceReportFilter Filter, IReadOnlyList<AttendanceReportRow> Rows,
    string TeacherFilter = "All teachers", string ClassroomFilter = "All classrooms");

internal static class AttendanceReport
{
    internal static readonly string[] Outcomes = ["OnTime", "Late", "Absent", "MissingTimeOut", "Active", "Other"];
    internal static string Label(string outcome) => outcome switch
    {
        "OnTime" => "On time",
        "Late" => "Late",
        "Absent" => "Absent",
        "MissingTimeOut" => "Missing time-out",
        "Active" => "Active",
        _ => "Other"
    };
    internal static string Category(string status, string? outcome) =>
        status == "Active" ? "Active" :
        outcome is "OnTime" or "Late" or "Absent" or "MissingTimeOut" ? outcome : "Other";

    internal static bool TryFilter(string? from, string? to, string? teacher, string? classroom, string? outcome,
        out AttendanceReportFilter? filter)
    {
        filter = null;
        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) ||
            start > end || !TryId(teacher, out var teacherId) || !TryId(classroom, out var classroomId) ||
            (outcome is not null and not "" && !Outcomes.Contains(outcome, StringComparer.Ordinal)))
            return false;
        filter = new AttendanceReportFilter(start, end, teacherId, classroomId, outcome ?? "");
        return true;
    }

    private static bool TryId(string? value, out long? id)
    {
        id = null;
        if (string.IsNullOrEmpty(value)) return true;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0) return false;
        id = parsed;
        return true;
    }

    internal static async Task<AttendanceReportData> LoadAsync(IConfiguration configuration, AttendanceReportFilter filter,
        CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("SmartClassMariaDb")
            ?? throw new InvalidOperationException("Set ConnectionStrings:SmartClassMariaDb before creating attendance reports.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        async Task<string> FilterNameAsync(long? id, string sql, string allLabel, string missingLabel)
        {
            if (id is null) return allLabel;
            await using var lookup = connection.CreateCommand();
            lookup.CommandText = sql;
            lookup.Parameters.Add("@id", MySqlDbType.Int64).Value = id.Value;
            var name = await lookup.ExecuteScalarAsync(cancellationToken);
            return name is string value ? value : $"{missingLabel} #{id} (not found)";
        }
        var teacherName = await FilterNameAsync(filter.TeacherId, "SELECT display_name FROM user_accounts WHERE id = @id AND role = 'Teacher'", "All teachers", "Teacher");
        var classroomName = await FilterNameAsync(filter.ClassroomId, "SELECT name FROM classrooms WHERE id = @id", "All classrooms", "Classroom");
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(s.schedule_date, DATE(a.time_in_utc)), u.display_name, c.name,
                   COALESCE(s.subject_name, ''), a.time_in_utc, a.time_out_utc, a.verification_method, a.status, a.outcome
            FROM attendance_logs a
            INNER JOIN user_accounts u ON u.id = a.teacher_account_id
            INNER JOIN classrooms c ON c.id = a.classroom_id
            LEFT JOIN class_schedules s ON s.id = a.class_schedule_id
            WHERE COALESCE(s.schedule_date, DATE(a.time_in_utc)) BETWEEN @from AND @to
              AND (@teacher IS NULL OR a.teacher_account_id = @teacher)
              AND (@room IS NULL OR a.classroom_id = @room)
            ORDER BY COALESCE(s.schedule_date, DATE(a.time_in_utc)), a.time_in_utc, a.id;
            """;
        command.Parameters.Add("@from", MySqlDbType.Date).Value = filter.From.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@to", MySqlDbType.Date).Value = filter.To.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@teacher", MySqlDbType.Int64).Value = (object?)filter.TeacherId ?? DBNull.Value;
        command.Parameters.Add("@room", MySqlDbType.Int64).Value = (object?)filter.ClassroomId ?? DBNull.Value;
        var rows = new List<AttendanceReportRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var status = reader.GetString(7);
            var rawOutcome = reader.IsDBNull(8) ? null : reader.GetString(8);
            var category = Category(status, rawOutcome);
            if (filter.Outcome.Length > 0 && filter.Outcome != category) continue;
            rows.Add(new AttendanceReportRow(DateOnly.FromDateTime(reader.GetDateTime(0)), reader.GetString(1),
                reader.GetString(2), reader.GetString(3),
                category == "Absent" ? null : reader.GetDateTime(4),
                category is "Absent" or "MissingTimeOut" || reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                reader.GetString(6), category, category == "Active" && rawOutcome == "Late"));
        }
        return new AttendanceReportData(filter, rows, teacherName, classroomName);
    }

    internal static byte[] Workbook(AttendanceReportData report)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Attendance");
        sheet.Cell(1, 1).Value = "Attendance report";
        sheet.Cell(2, 1).Value = $"Report dates: {report.Filter.From:yyyy-MM-dd} to {report.Filter.To:yyyy-MM-dd}";
        sheet.Cell(3, 1).Value = "Dates are scheduled class dates where available; missing time-outs are not actual punches.";
        sheet.Cell(4, 1).Value = FilterDescription(report);
        sheet.Cell(5, 1).Value = "Total";
        sheet.Cell(5, 2).Value = report.Rows.Count;
        for (var i = 0; i < Outcomes.Length; i++)
        {
            sheet.Cell(6 + i, 1).Value = Label(Outcomes[i]);
            sheet.Cell(6 + i, 2).Value = report.Rows.Count(row => row.Outcome == Outcomes[i]);
        }
        string[] headings = ["Class date", "Teacher", "Classroom", "Subject", "Time in", "Time out", "Verification", "Outcome"];
        for (var i = 0; i < headings.Length; i++) sheet.Cell(14, i + 1).Value = headings[i];
        for (var i = 0; i < report.Rows.Count; i++)
        {
            var row = report.Rows[i];
            var n = i + 15;
            sheet.Cell(n, 1).Value = row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            sheet.Cell(n, 2).Value = row.Teacher;
            sheet.Cell(n, 3).Value = row.Classroom;
            sheet.Cell(n, 4).Value = row.Subject;
            sheet.Cell(n, 5).Value = FormatTime(row.TimeIn);
            sheet.Cell(n, 6).Value = FormatTime(row.TimeOut);
            sheet.Cell(n, 7).Value = row.Verification;
            sheet.Cell(n, 8).Value = RowLabel(row);
        }
        sheet.Row(1).Style.Font.Bold = true;
        sheet.Row(14).Style.Font.Bold = true;
        sheet.Columns(1, 8).AdjustToContents();
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    internal static string Printable(AttendanceReportData report)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Attendance report</title>
            <link rel="stylesheet" href="/css/site.css"><link rel="stylesheet" href="/css/dashboard.css">
            <style>body{font:14px Arial,sans-serif;margin:2rem;color:#111}table{border-collapse:collapse;width:100%;margin-top:1rem}
            th,td{border:1px solid #bbb;padding:6px;text-align:left}th{background:#eee}h1{font-size:22px}
            .summary{display:flex;flex-wrap:wrap;gap:1rem}.report-print-button{border:0}@media print{button{display:none}body{margin:0}thead{display:table-header-group}tr{break-inside:avoid}}</style>
            </head><body><button type="button" class="primary-button report-print-button" onclick="window.print()">Print / save as PDF</button><h1>Attendance report</h1>
            """);
        html.Append("<p>Class dates: ").Append(report.Filter.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append(" to ").Append(report.Filter.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("</p>")
            .Append("<p>").Append(E(FilterDescription(report))).Append("</p>")
            .Append("<p>Dates are scheduled class dates where available. Missing time-outs are not actual punches.</p>")
            .Append("<div class=\"summary\"><strong>Total: ").Append(report.Rows.Count).Append("</strong>");
        foreach (var category in Outcomes)
            html.Append("<span>").Append(E(Label(category))).Append(": ").Append(report.Rows.Count(row => row.Outcome == category)).Append("</span>");
        html.Append("</div><table><thead><tr><th>Class date</th><th>Teacher</th><th>Classroom</th><th>Subject</th><th>Time in</th><th>Time out</th><th>Verification</th><th>Outcome</th></tr></thead><tbody>");
        foreach (var row in report.Rows)
        {
            string[] cells = [row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), row.Teacher, row.Classroom,
                row.Subject, FormatTime(row.TimeIn), FormatTime(row.TimeOut), row.Verification, RowLabel(row)];
            html.Append("<tr>");
            foreach (var cell in cells) html.Append("<td>").Append(E(cell)).Append("</td>");
            html.Append("</tr>");
        }
        if (report.Rows.Count == 0) html.Append("<tr><td colspan=\"8\">No attendance records match the selected filters.</td></tr>");
        return html.Append("</tbody></table></body></html>").ToString();
    }

    private static string FormatTime(DateTime? time) =>
        time?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—";

    private static string RowLabel(AttendanceReportRow row) => row.ActiveLate ? "Active · Late" : Label(row.Outcome);

    private static string FilterDescription(AttendanceReportData report) =>
        $"Teacher: {report.TeacherFilter} | Classroom: {report.ClassroomFilter} | Outcome: {(report.Filter.Outcome.Length == 0 ? "All outcomes" : Label(report.Filter.Outcome))}";
}

internal static class AttendanceReportRoutes
{
    internal static void MapAttendanceReportRoutes(this WebApplication app)
    {
        app.MapGet("/reports/attendance/{format}", async (string format, HttpContext context,
            IConfiguration configuration, IUserAccountRepository accounts, IOptions<SchoolTimeOptions> schoolTime,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!long.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ||
                (await accounts.FindByIdAsync(id, cancellationToken))?.Role != UserRole.Admin)
                return Results.Forbid();
            if (format is not ("print" or "xlsx") ||
                !AttendanceReport.TryFilter(context.Request.Query["from"], context.Request.Query["to"],
                    context.Request.Query["teacher"], context.Request.Query["classroom"], context.Request.Query["outcome"], out var filter))
                return Results.BadRequest("Invalid attendance report filters.");
            var now = SchoolClock.GetNow(schoolTime.Value);
            await accounts.ReconcileAttendanceOutcomesAsync(DateOnly.FromDateTime(now), TimeOnly.FromDateTime(now), cancellationToken);
            var report = await AttendanceReport.LoadAsync(configuration, filter!, cancellationToken);
            return format == "print"
                ? Results.Content(AttendanceReport.Printable(report), "text/html; charset=utf-8")
                : Results.File(AttendanceReport.Workbook(report),
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    $"attendance-{filter!.From:yyyy-MM-dd}-to-{filter.To:yyyy-MM-dd}.xlsx");
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));
    }
}
