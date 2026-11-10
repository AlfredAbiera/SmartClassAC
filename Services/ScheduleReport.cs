using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using ClosedXML.Excel;

namespace SmartClassAC.Services;

internal sealed record ScheduleReportFilter(DateOnly From, DateOnly To, long? TeacherId);
internal sealed record ScheduleReportData(ScheduleReportFilter Filter, string TeacherFilter, IReadOnlyList<ScheduleDirectoryEntry> Rows);

internal static class ScheduleReport
{
    internal static bool TryFilter(string? from, string? to, string? teacher, out ScheduleReportFilter? filter)
    {
        filter = null;
        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) ||
            start > end)
        {
            return false;
        }

        long? teacherId = null;
        if (!string.IsNullOrEmpty(teacher))
        {
            if (!long.TryParse(teacher, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            {
                return false;
            }

            teacherId = parsed;
        }

        filter = new ScheduleReportFilter(start, end, teacherId);
        return true;
    }

    internal static byte[] Workbook(ScheduleReportData report)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Schedules");
        sheet.Cell(1, 1).Value = "Schedule report";
        sheet.Cell(2, 1).Value = $"Schedule dates: {report.Filter.From:yyyy-MM-dd} to {report.Filter.To:yyyy-MM-dd}";
        sheet.Cell(3, 1).Value = $"Teacher: {report.TeacherFilter}";
        sheet.Cell(4, 1).Value = $"Total schedules: {report.Rows.Count}";
        string[] headings = ["Teacher", "Semester", "Kind", "Classroom", "Subject", "Date", "Start time", "End time"];
        for (var i = 0; i < headings.Length; i++)
        {
            sheet.Cell(6, i + 1).Value = headings[i];
        }

        for (var i = 0; i < report.Rows.Count; i++)
        {
            var schedule = report.Rows[i];
            var row = i + 7;
            sheet.Cell(row, 1).Value = schedule.TeacherDisplayName;
            sheet.Cell(row, 2).Value = schedule.SemesterName ?? "—";
            sheet.Cell(row, 3).Value = schedule.ScheduleKind;
            sheet.Cell(row, 4).Value = schedule.ClassroomName;
            sheet.Cell(row, 5).Value = schedule.SubjectName;
            sheet.Cell(row, 6).Value = schedule.ScheduleDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            sheet.Cell(row, 7).Value = schedule.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            sheet.Cell(row, 8).Value = schedule.EndTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        sheet.Row(1).Style.Font.Bold = true;
        sheet.Row(6).Style.Font.Bold = true;
        sheet.Columns(1, 8).AdjustToContents();
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    internal static string Printable(ScheduleReportData report)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Schedule report</title>
            <style>body{font:14px Arial,sans-serif;margin:2rem;color:#111}table{border-collapse:collapse;width:100%;margin-top:1rem}
            th,td{border:1px solid #bbb;padding:6px;text-align:left}th{background:#eee}h1{font-size:22px}
            .report-print-button{border:0;padding:8px 12px}@media print{button{display:none}body{margin:0}thead{display:table-header-group}tr{break-inside:avoid}}</style>
            </head><body><button type="button" class="report-print-button" onclick="window.print()">Print / save as PDF</button><h1>Schedule report</h1>
            """);
        html.Append("<p>Schedule dates: ").Append(report.Filter.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append(" to ").Append(report.Filter.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("</p>")
            .Append("<p>Teacher: ").Append(E(report.TeacherFilter)).Append(" · Total schedules: ").Append(report.Rows.Count).Append("</p>")
            .Append("<table><thead><tr><th>Teacher</th><th>Semester</th><th>Kind</th><th>Classroom</th><th>Subject</th><th>Date</th><th>Start time</th><th>End time</th></tr></thead><tbody>");
        foreach (var schedule in report.Rows)
        {
            string[] cells =
            [
                schedule.TeacherDisplayName, schedule.SemesterName ?? "—", schedule.ScheduleKind,
                schedule.ClassroomName, schedule.SubjectName,
                schedule.ScheduleDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                schedule.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                schedule.EndTime.ToString("HH:mm", CultureInfo.InvariantCulture)
            ];
            html.Append("<tr>");
            foreach (var cell in cells)
            {
                html.Append("<td>").Append(E(cell)).Append("</td>");
            }
            html.Append("</tr>");
        }

        if (report.Rows.Count == 0)
        {
            html.Append("<tr><td colspan=\"8\">No schedules match the selected filters.</td></tr>");
        }

        return html.Append("</tbody></table></body></html>").ToString();
    }
}

internal static class ScheduleReportRoutes
{
    internal static void MapScheduleReportRoutes(this WebApplication app)
    {
        app.MapGet("/reports/schedules/{format}", async (string format, HttpContext context,
            IUserAccountRepository accounts, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!long.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ||
                (await accounts.FindByIdAsync(id, cancellationToken))?.Role != UserRole.Admin)
            {
                return Results.Forbid();
            }

            if (format is not ("print" or "xlsx") ||
                !ScheduleReport.TryFilter(context.Request.Query["from"], context.Request.Query["to"],
                    context.Request.Query["teacher"], out var filter))
            {
                return Results.BadRequest("Invalid schedule report filters.");
            }

            var teachers = await accounts.GetTeachersAsync(cancellationToken);
            var selectedTeacher = filter!.TeacherId is long teacherId
                ? teachers.FirstOrDefault(teacher => teacher.Id == teacherId)
                : null;
            if (filter.TeacherId is not null && selectedTeacher is null)
            {
                return Results.BadRequest("The selected teacher was not found.");
            }

            var schedules = await accounts.GetSchedulesAsync(cancellationToken);
            var rows = schedules
                .Where(schedule => schedule.ScheduleDate >= filter.From && schedule.ScheduleDate <= filter.To)
                .Where(schedule => filter.TeacherId is null || schedule.TeacherAccountId == filter.TeacherId)
                .OrderBy(schedule => schedule.ScheduleDate)
                .ThenBy(schedule => schedule.StartTime)
                .ThenBy(schedule => schedule.TeacherDisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var report = new ScheduleReportData(filter, selectedTeacher?.DisplayName ?? "All teachers", rows);
            return format == "print"
                ? Results.Content(ScheduleReport.Printable(report), "text/html; charset=utf-8")
                : Results.File(ScheduleReport.Workbook(report),
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    $"schedules-{filter.From:yyyy-MM-dd}-to-{filter.To:yyyy-MM-dd}.xlsx");
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));
    }
}
