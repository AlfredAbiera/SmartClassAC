using System.Net;
using ClosedXML.Excel;
using SmartClassAC.Services;
using Xunit;

namespace SmartClassAC.Tests;

public sealed class ScheduleReportTests
{
    [Theory]
    [InlineData("2025-01-01", "2025-01-01", "", true)]
    [InlineData("2025-01-02", "2025-01-01", "", false)]
    [InlineData("2025-02-29", "2025-03-01", "", false)]
    [InlineData("2025-01-01", "2025-01-02", "0", false)]
    [InlineData("2025-01-01", "2025-01-02", "3", true)]
    [InlineData("2025-01-01", "2025-01-02", "-1", false)]
    public void ValidatesFilters(string from, string to, string teacher, bool valid) =>
        Assert.Equal(valid, ScheduleReport.TryFilter(from, to, teacher, out _));

    [Fact]
    public void PrintAndWorkbookIncludeTeacherScheduleDetailsAndEncodeHtml()
    {
        var date = new DateOnly(2025, 1, 2);
        var schedule = new ScheduleDirectoryEntry(
            1, 2, "<Teacher>", 2, "<Teacher>", 3, "Room A", 4, "Semester 1",
            "Regular", "=subject", date, new TimeOnly(8, 0), new TimeOnly(9, 30));
        var report = new ScheduleReportData(new(date, date, 2), "<Teacher>", [schedule]);
        var html = ScheduleReport.Printable(report);

        Assert.Contains("&lt;Teacher&gt;", html);
        Assert.DoesNotContain("<Teacher>", html);
        Assert.Contains("Room A", WebUtility.HtmlDecode(html));
        Assert.Contains("Total schedules: 1", html);

        using var workbook = new XLWorkbook(new MemoryStream(ScheduleReport.Workbook(report)));
        var sheet = workbook.Worksheet("Schedules");
        Assert.Equal("Teacher", sheet.Cell(6, 1).GetString());
        Assert.Equal("<Teacher>", sheet.Cell(7, 1).GetString());
        Assert.Equal("=subject", sheet.Cell(7, 5).GetString());
        Assert.False(sheet.Cell(7, 5).HasFormula);
        Assert.Equal("2025-01-02", sheet.Cell(7, 6).GetString());
        Assert.Equal("08:00", sheet.Cell(7, 7).GetString());
    }

    [Fact]
    public void EmptyReportShowsNoSchedules()
    {
        var date = new DateOnly(2025, 1, 1);
        var report = new ScheduleReportData(new(date, date, null), "All teachers", []);

        Assert.Contains("No schedules match the selected filters.", ScheduleReport.Printable(report));
        using var workbook = new XLWorkbook(new MemoryStream(ScheduleReport.Workbook(report)));
        Assert.Equal("Total schedules: 0", workbook.Worksheet("Schedules").Cell(4, 1).GetString());
    }
}
