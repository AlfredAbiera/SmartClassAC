using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using SmartClassAC.Services;
using Xunit;

namespace SmartClassAC.Tests;

public sealed class AttendanceReportTests
{
    [Theory]
    [InlineData("2025-01-01", "2025-01-01", "", "", "", true)]
    [InlineData("2025-01-02", "2025-01-01", "", "", "", false)]
    [InlineData("2025-02-29", "2025-03-01", "", "", "", false)]
    [InlineData("2025-01-01", "2025-01-02", "0", "", "", false)]
    [InlineData("2025-01-01", "2025-01-02", "", "-1", "", false)]
    [InlineData("2025-01-01", "2025-01-02", "1", "2", "Active", true)]
    [InlineData("2025-01-01", "2025-01-02", "", "", "invalid", false)]
    public void ValidatesFilters(string from, string to, string teacher, string room, string outcome, bool valid) =>
        Assert.Equal(valid, AttendanceReport.TryFilter(from, to, teacher, room, outcome, out _));

    [Theory]
    [InlineData("Active", "Late", "Active")]
    [InlineData("Active", "OnTime", "Active")]
    [InlineData("Completed", "Late", "Late")]
    [InlineData("Completed", "Absent", "Absent")]
    [InlineData("Completed", "MissingTimeOut", "MissingTimeOut")]
    [InlineData("Completed", null, "Other")]
    public void GroupsOutcomes(string status, string? outcome, string expected) =>
        Assert.Equal(expected, AttendanceReport.Category(status, outcome));

    [Fact]
    public void PrintAndWorkbookContainSameDetailsAndCountsAndTreatTextAsText()
    {
        var date = new DateOnly(2025, 1, 2);
        var report = new AttendanceReportData(new(date, date, 1, 2, ""),
        [
            new(date, "=1+1", "<Room>", "History", date.ToDateTime(new TimeOnly(8, 0)), null, "Biometric", "Active", true),
            new(date, "Teacher", "Room", "Math", null, null, "System", "Absent")
        ], "=1+1", "<Room>");
        var html = AttendanceReport.Printable(report);
        Assert.Contains("Total: 2", html);
        Assert.Contains("Active: 1", html);
        Assert.Contains("Absent: 1", html);
        Assert.Contains("Active · Late", WebUtility.HtmlDecode(html));
        Assert.Contains("&lt;Room&gt;", html);
        Assert.DoesNotContain("<Room>", html);
        using var workbook = new XLWorkbook(new MemoryStream(AttendanceReport.Workbook(report)));
        var sheet = workbook.Worksheet("Attendance");
        Assert.Equal(2, sheet.Cell(5, 2).GetValue<int>());
        Assert.Equal(1, sheet.Cell(8, 2).GetValue<int>());
        Assert.Equal(1, sheet.Cell(10, 2).GetValue<int>());
        Assert.Equal("=1+1", sheet.Cell(15, 2).GetString());
        Assert.False(sheet.Cell(15, 2).HasFormula);
        Assert.Equal("<Room>", sheet.Cell(15, 3).GetString());
        Assert.Equal("Active · Late", sheet.Cell(15, 8).GetString());
        Assert.Equal("—", sheet.Cell(16, 5).GetString());
        Assert.Equal("Absent", sheet.Cell(16, 8).GetString());
    }

    [Fact]
    public void EmptyReportShowsZeroAndNoMatchingRecords()
    {
        var date = new DateOnly(2025, 1, 1);
        var report = new AttendanceReportData(new(date, date, null, null, "Absent"), []);
        Assert.Contains("No attendance records match", AttendanceReport.Printable(report));
        using var workbook = new XLWorkbook(new MemoryStream(AttendanceReport.Workbook(report)));
        Assert.Equal(0, workbook.Worksheet("Attendance").Cell(5, 2).GetValue<int>());
        Assert.Contains("Absent", workbook.Worksheet("Attendance").Cell(4, 1).GetString());
    }

    [Fact]
    public async Task BothRoutesRequireAdminAndRecheckAccount()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, ReportTestAuthHandler>("test", _ => { });
        builder.Services.AddAuthorizationCore();
        builder.Services.AddAuthorization();
        builder.Services.AddOptions();
        builder.Services.AddScoped(_ => DispatchProxy.Create<IUserAccountRepository, ReportRepositoryProxy>());
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAttendanceReportRoutes();
        await app.StartAsync();
        var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        foreach (var format in new[] { "print", "xlsx" })
        {
            var path = $"/reports/attendance/{format}?from=2025-01-01&to=2025-01-02";
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
            using var teacher = new HttpRequestMessage(HttpMethod.Get, path);
            teacher.Headers.Add("X-Test-Role", "Teacher");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(teacher)).StatusCode);
            using var staleAdmin = new HttpRequestMessage(HttpMethod.Get, path);
            staleAdmin.Headers.Add("X-Test-Role", "StaleAdmin");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(staleAdmin)).StatusCode);
            using var admin = new HttpRequestMessage(HttpMethod.Get, $"/reports/attendance/{format}?from=invalid&to=2025-01-02");
            admin.Headers.Add("X-Test-Role", "Admin");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(admin)).StatusCode);
        }
    }

    [MariaDbFact]
    public async Task DatabaseFilteringUsesInclusiveScheduledDatesAndHidesSyntheticPunches()
    {
        var builder = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SMARTCLASS_TEST_CONNECTION")!) { Database = "" };
        var database = "smartclass_report_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await new MySqlCommand($"CREATE DATABASE `{database}`", admin).ExecuteNonQueryAsync();
        try
        {
            builder.Database = database;
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await new MySqlCommand("""
                CREATE TABLE user_accounts (id BIGINT PRIMARY KEY, display_name VARCHAR(80), role VARCHAR(20));
                CREATE TABLE classrooms (id BIGINT PRIMARY KEY, name VARCHAR(80));
                CREATE TABLE class_schedules (id BIGINT PRIMARY KEY, schedule_date DATE, subject_name VARCHAR(80));
                CREATE TABLE attendance_logs (id BIGINT PRIMARY KEY, class_schedule_id BIGINT NULL, teacher_account_id BIGINT, classroom_id BIGINT,
                  time_in_utc DATETIME, time_out_utc DATETIME NULL, verification_method VARCHAR(40), status VARCHAR(24), outcome VARCHAR(24));
                INSERT INTO user_accounts VALUES (1, 'Teacher A', 'Teacher'), (2, 'Teacher B', 'Teacher');
                INSERT INTO classrooms VALUES (1, 'Room A'), (2, 'Room B');
                INSERT INTO class_schedules VALUES (1, '2025-01-01', 'Math'), (2, '2025-01-02', 'Art');
                INSERT INTO attendance_logs VALUES
                  (1, 1, 1, 1, '2025-01-05 10:00:00', '2025-01-05 10:00:00', 'System', 'Completed', 'Absent'),
                  (2, 2, 1, 1, '2025-01-02 08:00:00', '2025-01-02 10:00:00', 'System', 'Completed', 'MissingTimeOut'),
                  (3, NULL, 2, 2, '2025-01-02 09:00:00', NULL, 'Biometric', 'Active', 'Late'),
                  (4, NULL, 2, 2, '2025-01-03 09:00:00', NULL, 'Biometric', 'Active', 'OnTime');
                """, connection).ExecuteNonQueryAsync();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = builder.ConnectionString
            }).Build();
            var filter = new AttendanceReportFilter(new(2025, 1, 1), new(2025, 1, 2), null, null, "");
            var all = await AttendanceReport.LoadAsync(configuration, filter, default);
            Assert.Equal(3, all.Rows.Count);
            Assert.Equal(new DateOnly(2025, 1, 1), all.Rows[0].Date);
            Assert.Null(all.Rows[0].TimeIn);
            Assert.Null(all.Rows[0].TimeOut);
            Assert.Null(all.Rows[1].TimeOut);
            Assert.True(all.Rows[2].ActiveLate);
            var filtered = await AttendanceReport.LoadAsync(configuration, filter with { TeacherId = 1, ClassroomId = 1, Outcome = "Absent" }, default);
            Assert.Single(filtered.Rows);
            Assert.Equal("Teacher A", filtered.TeacherFilter);
            Assert.Equal("Room A", filtered.ClassroomFilter);
            var empty = await AttendanceReport.LoadAsync(configuration, filter with { TeacherId = 1, ClassroomId = 2 }, default);
            Assert.Empty(empty.Rows);

            var web = WebApplication.CreateSlimBuilder();
            web.Logging.ClearProviders();
            web.WebHost.UseUrls("http://127.0.0.1:0");
            web.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = builder.ConnectionString
            });
            web.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, ReportTestAuthHandler>("test", _ => { });
            web.Services.AddAuthorization();
            web.Services.AddOptions();
            web.Services.AddScoped(_ => DispatchProxy.Create<IUserAccountRepository, ReportRepositoryProxy>());
            await using var app = web.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapAttendanceReportRoutes();
            await app.StartAsync();
            var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            client.DefaultRequestHeaders.Add("X-Test-Role", "Admin");
            const string query = "?from=2025-01-01&to=2025-01-02&teacher=1&classroom=1&outcome=Absent";
            using var print = await client.GetAsync("/reports/attendance/print" + query);
            Assert.Equal(HttpStatusCode.OK, print.StatusCode);
            Assert.Equal("no-store", print.Headers.CacheControl?.ToString());
            var html = await print.Content.ReadAsStringAsync();
            Assert.Contains("Teacher A", html);
            Assert.Contains("Total: 1", html);
            using var excel = await client.GetAsync("/reports/attendance/xlsx" + query);
            Assert.Equal(HttpStatusCode.OK, excel.StatusCode);
            Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", excel.Content.Headers.ContentType?.MediaType);
            using var workbook = new XLWorkbook(new MemoryStream(await excel.Content.ReadAsByteArrayAsync()));
            Assert.Equal(1, workbook.Worksheet("Attendance").Cell(5, 2).GetValue<int>());
            Assert.Equal("Teacher A", workbook.Worksheet("Attendance").Cell(15, 2).GetString());
        }
        finally
        {
            await new MySqlCommand($"DROP DATABASE `{database}`", admin).ExecuteNonQueryAsync();
        }
    }
}

public class ReportRepositoryProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IUserAccountRepository.FindByIdAsync))
        {
            var id = (long)args![0]!;
            return Task.FromResult<UserAccountRecord?>(id == 1
                ? new UserAccountRecord(1, "admin", "Admin", null, "", UserRole.Admin, false)
                : null);
        }
        if (targetMethod?.Name == nameof(IUserAccountRepository.ReconcileAttendanceOutcomesAsync))
            return Task.FromResult(0);
        throw new NotSupportedException(targetMethod?.Name);
    }
}

public sealed class ReportTestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers["X-Test-Role"].ToString();
        if (role.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, role == "StaleAdmin" ? "2" : "1"),
            new Claim(ClaimTypes.Role, role == "Teacher" ? "Teacher" : "Admin")
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
