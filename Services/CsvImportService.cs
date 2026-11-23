using System.Globalization;
using Microsoft.VisualBasic.FileIO;

namespace SmartClassAC.Services;

public sealed record CsvImportResult(int ImportedRows, int CreatedRecords, int SkippedRows,
    int SkippedOccurrences, IReadOnlyList<string> Errors, string? Error = null)
{
    public static CsvImportResult Failure(string error) => new(0, 0, 0, 0, Array.Empty<string>(), error);
}

internal sealed class CsvImportService(IAuthService auth)
{
    internal const int MaximumBytes = 1_000_000;
    private const int MaximumRows = 1000;

    private sealed record Row(long Line, Dictionary<string, string> Fields, string? ParseError = null)
    {
        public string Get(string name) => Fields.TryGetValue(name, out var value) ? value.Trim() : string.Empty;
    }

    public async Task<CsvImportResult> ImportAsync(string kind, string csv)
    {
        var actor = await auth.GetCurrentUserAsync();
        if (actor is null || actor.Role != UserRole.Admin)
            return CsvImportResult.Failure("Administrator access is required.");
        if (csv.Length > MaximumBytes)
            return CsvImportResult.Failure("The CSV file exceeds the 1 MB limit.");
        var required = kind switch
        {
            "teachers" => new[] { "name", "phone" },
            "semesters" => new[] { "name", "start_date", "end_date" },
            "schedules" => new[] { "employee_number", "semester", "classroom", "subject", "date", "start_time", "end_time" },
            _ => Array.Empty<string>()
        };
        if (required.Length == 0) return CsvImportResult.Failure("Choose a valid import type.");
        var optional = kind switch
        {
            "semesters" => new[] { "active" },
            "schedules" => new[] { "recurrence", "weekdays", "kind", "replaces_schedule_id" },
            _ => Array.Empty<string>()
        };
        var (rows, error) = Parse(csv, required, optional);
        if (error is not null) return CsvImportResult.Failure(error);

        var teachers = kind == "schedules" ? await auth.GetTeachersAsync() : Array.Empty<TeacherDirectoryEntry>();
        var semesters = kind == "schedules" ? await auth.GetSemestersAsync() : Array.Empty<SemesterEntry>();
        var rooms = kind == "schedules" ? await auth.GetClassroomsAsync() : Array.Empty<ClassroomEntry>();
        var imported = 0;
        var created = 0;
        var skipped = 0;
        var skippedOccurrences = 0;
        var proposedOccurrences = 0;
        var errors = new List<string>();
        foreach (var row in rows)
        {
            OperationResult result;
            var count = 1;
            if (row.ParseError is not null)
                result = OperationResult.Failure(row.ParseError);
            else if (kind == "teachers")
                result = row.Get("name").Length > 160
                    ? OperationResult.Failure("Name must be at most 160 characters.")
                    : await auth.CreateImportedTeacherAsync(row.Get("name"), row.Get("phone"));
            else if (kind == "semesters")
                result = await ImportSemesterAsync(row);
            else
            {
                var (schedule, validationError) = ParseSchedule(row, teachers, semesters, rooms);
                if (schedule is null) result = OperationResult.Failure(validationError!);
                else
                {
                    var end = semesters.First(s => s.Id == schedule.SemesterId).EndDate;
                    var occurrences = CountOccurrences(schedule, end);
                    if (occurrences > 1000 || proposedOccurrences + occurrences > 10000)
                        result = OperationResult.Failure("Import is limited to 1,000 occurrences per row and 10,000 per file.");
                    else
                    {
                        proposedOccurrences += occurrences;
                        var outcome = await auth.CreateImportedSchedulesAsync(schedule);
                        result = outcome.Succeeded ? OperationResult.Success() : OperationResult.Failure(outcome.Error ?? "Schedule could not be saved.");
                        count = outcome.CreatedCount;
                        skippedOccurrences += outcome.SkippedCount;
                        if (outcome.SkippedCount > 0) errors.Add($"Row {row.Line}: {outcome.SkippedCount} conflicting occurrence(s) skipped.");
                    }
                }
            }
            if (result.Succeeded)
            {
                imported++;
                created += count;
            }
            else
            {
                skipped++;
                errors.Add($"Row {row.Line}: {result.Error ?? "Could not import this row."}");
            }
        }
        return new CsvImportResult(imported, created, skipped, skippedOccurrences, errors);
    }

    private async Task<OperationResult> ImportSemesterAsync(Row row)
    {
        if (!TryDate(row.Get("start_date"), out var start) || !TryDate(row.Get("end_date"), out var end))
            return OperationResult.Failure("Dates must use YYYY-MM-DD.");
        var activeText = row.Get("active");
        if (activeText.Length > 0 && !bool.TryParse(activeText, out _))
            return OperationResult.Failure("Active must be true or false.");
        return await auth.CreateSemesterAsync(new CreateSemesterRequest(row.Get("name"), start, end,
            activeText.Length == 0 || bool.Parse(activeText)));
    }

    private static (CreateScheduleRequest? Request, string? Error) ParseSchedule(Row row,
        IReadOnlyList<TeacherDirectoryEntry> teachers, IReadOnlyList<SemesterEntry> semesters,
        IReadOnlyList<ClassroomEntry> rooms)
    {
        var teacher = teachers.FirstOrDefault(t => t.IsActive && string.Equals(t.EmployeeNumber, row.Get("employee_number"), StringComparison.OrdinalIgnoreCase));
        var semester = semesters.FirstOrDefault(s => s.IsActive && string.Equals(s.Name, row.Get("semester"), StringComparison.OrdinalIgnoreCase));
        var room = rooms.FirstOrDefault(r => r.IsActive && string.Equals(r.Name, row.Get("classroom"), StringComparison.OrdinalIgnoreCase));
        if (teacher is null || semester is null || room is null)
            return (null, "Teacher, semester, or classroom was not found or is inactive.");
        if (!TryDate(row.Get("date"), out var date) ||
            !TimeOnly.TryParseExact(row.Get("start_time"), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !TimeOnly.TryParseExact(row.Get("end_time"), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            return (null, "Use YYYY-MM-DD for the date and HH:mm (24-hour) for times.");
        var recurrence = row.Get("recurrence");
        if (recurrence.Length == 0) recurrence = ScheduleRecurrence.None;
        if (!ScheduleRecurrence.IsValid(recurrence)) return (null, "Invalid recurrence pattern.");
        var days = new List<DayOfWeek>();
        if (row.Get("weekdays") is { Length: > 0 } dayText)
        {
            foreach (var day in dayText.Split('|', StringSplitOptions.TrimEntries))
            {
                var parsed = day switch
                {
                    "Mon" => DayOfWeek.Monday, "Tue" => DayOfWeek.Tuesday, "Wed" => DayOfWeek.Wednesday,
                    "Thu" => DayOfWeek.Thursday, "Fri" => DayOfWeek.Friday, _ => (DayOfWeek?)null
                };
                if (parsed is null || days.Contains(parsed.Value)) return (null, "Weekdays must be distinct Mon|Tue|Wed|Thu|Fri values.");
                days.Add(parsed.Value);
            }
        }
        if (recurrence != ScheduleRecurrence.Custom && days.Count > 0)
            return (null, "Weekdays are only valid for Custom recurrence.");
        var kind = row.Get("kind");
        if (kind.Length == 0) kind = ScheduleKinds.Regular;
        var replacementText = row.Get("replaces_schedule_id");
        long? replacement = null;
        if (replacementText.Length > 0)
        {
            if (!long.TryParse(replacementText, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                return (null, "Replacement schedule ID must be a positive number.");
            replacement = id;
        }
        return (new CreateScheduleRequest(teacher.Id, room.Id, semester.Id, kind, row.Get("subject"),
            date, start, end, recurrence, days, replacement), null);
    }

    private static bool TryDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static int CountOccurrences(CreateScheduleRequest schedule, DateOnly semesterEnd)
    {
        if (schedule.ScheduleDate > semesterEnd) return 0;
        if (schedule.RecurrencePattern == ScheduleRecurrence.None) return 1;
        var weekdays = ScheduleRecurrence.ResolveWeekdays(schedule.RecurrencePattern, schedule.ScheduleDate, schedule.CustomWeekdays);
        if (weekdays.Count == 0) return 0;
        var count = 0;
        for (var day = schedule.ScheduleDate.DayNumber; day <= semesterEnd.DayNumber && count <= 1000; day++)
            if (weekdays.Contains(DateOnly.FromDayNumber(day).DayOfWeek)) count++;
        return count;
    }

    private static (List<Row> Rows, string? Error) Parse(string text, string[] required, string[] optional)
    {
        var rows = new List<Row>();
        using var parser = new TextFieldParser(new StringReader(text.TrimStart('\uFEFF')))
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");
        try
        {
            var header = parser.ReadFields()?.Select(value => value.Trim()).ToArray();
            if (header is null || header.Length == 0 ||
                header.Any(string.IsNullOrEmpty) || header.Distinct(StringComparer.OrdinalIgnoreCase).Count() != header.Length)
                return (rows, "CSV must have unique, nonempty column headers.");
            if (required.Any(column => !header.Contains(column, StringComparer.OrdinalIgnoreCase)) ||
                header.Any(column => !required.Contains(column, StringComparer.OrdinalIgnoreCase) && !optional.Contains(column, StringComparer.OrdinalIgnoreCase)))
                return (rows, $"Expected required columns: {string.Join(", ", required)}. Optional: {string.Join(", ", optional)}.");
            while (!parser.EndOfData)
            {
                var line = parser.LineNumber;
                var values = parser.ReadFields();
                if (values is null) continue;
                if (rows.Count >= MaximumRows) return ([], $"CSV must contain at most {MaximumRows} data rows.");
                if (values.Length != header.Length)
                {
                    rows.Add(new Row(line, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                        "Incorrect number of columns."));
                    continue;
                }
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < header.Length; i++) fields.Add(header[i], values[i]);
                rows.Add(new Row(line, fields));
            }
        }
        catch (MalformedLineException exception)
        {
            return ([], $"CSV syntax is invalid at row {exception.LineNumber}.");
        }
        return rows.Count == 0 ? (rows, "The CSV contains no data rows.") : (rows, null);
    }
}
