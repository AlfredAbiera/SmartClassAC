using ClosedXML.Excel;

namespace SmartClassAC.Services;

internal static class ReportFormatting
{
    internal const string PrintStyles = """
        <style>
        *{box-sizing:border-box}
        body{font:14px Arial,sans-serif;margin:2rem;color:#17212b}
        h1{font-size:22px;margin:1rem 0}
        p{margin:.5rem 0;overflow-wrap:anywhere}
        .report-print-button{border:0;border-radius:4px;padding:10px 14px;background:#1f5a85;color:#fff;cursor:pointer}
        .summary{display:flex;flex-wrap:wrap;gap:.5rem 1rem;margin:1rem 0}
        table{border-collapse:collapse;table-layout:fixed;width:100%;margin-top:1rem}
        th,td{border:1px solid #aeb8c2;padding:6px;text-align:left;vertical-align:top;overflow-wrap:anywhere}
        th{background:#e8eef3}
        @page{size:A4 landscape;margin:12mm}
        @media print{
          body{margin:0;font-size:9pt;color:#111}
          h1{font-size:16pt}
          .report-print-button{display:none}
          thead{display:table-header-group}
          tr{break-inside:avoid}
          th,td{padding:4px}
          th{print-color-adjust:exact;-webkit-print-color-adjust:exact}
        }
        </style>
        """;

    internal static void FormatWorkbook(IXLWorksheet sheet, int headerRow, int dataCount,
        double[] widths, params int[] metadataRows)
    {
        var lastRow = headerRow + Math.Max(dataCount, 1);
        for (var column = 1; column <= widths.Length; column++)
            sheet.Column(column).Width = widths[column - 1];

        foreach (var row in metadataRows)
        {
            sheet.Range(row, 1, row, widths.Length).Merge();
            sheet.Cell(row, 1).Style.Alignment.WrapText = true;
            SetRowHeight(sheet.Row(row), [sheet.Cell(row, 1).GetString()], [widths.Sum()]);
        }

        sheet.Row(1).Style.Font.Bold = true;
        sheet.Row(1).Style.Font.FontSize = 16;
        sheet.Row(1).Height = 26;
        var header = sheet.Range(headerRow, 1, headerRow, widths.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF3");
        header.Style.Alignment.WrapText = true;
        sheet.Row(headerRow).Height = 25;

        var table = sheet.Range(headerRow, 1, lastRow, widths.Length);
        table.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        table.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        table.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        if (dataCount == 0)
        {
            sheet.Range(lastRow, 1, lastRow, widths.Length).Merge();
            sheet.Cell(lastRow, 1).Style.Alignment.WrapText = true;
            SetRowHeight(sheet.Row(lastRow), [sheet.Cell(lastRow, 1).GetString()], [widths.Sum()]);
        }
        else
        {
            var data = sheet.Range(headerRow + 1, 1, lastRow, widths.Length);
            data.Style.Alignment.WrapText = true;
            for (var row = headerRow + 1; row <= lastRow; row++)
            {
                var cells = Enumerable.Range(1, widths.Length).Select(column => sheet.Cell(row, column).GetString()).ToArray();
                SetRowHeight(sheet.Row(row), cells, widths);
            }
        }

        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
        sheet.PageSetup.FitToPages(1, 0);
        sheet.PageSetup.SetRowsToRepeatAtTop(headerRow, headerRow);
        sheet.PageSetup.PrintAreas.Add(sheet.Range(1, 1, lastRow, widths.Length).RangeAddress.ToString());
    }

    private static void SetRowHeight(IXLRow row, string[] values, double[] widths)
    {
        var lines = 1;
        for (var i = 0; i < values.Length; i++)
        {
            var capacity = Math.Max(1, (int)(widths[i] * 0.6));
            var cellLines = values[i].Split('\n').Sum(line => Math.Max(1, (int)Math.Ceiling((double)line.Length / capacity)));
            lines = Math.Max(lines, cellLines);
        }
        row.Height = Math.Min(409, Math.Max(20, lines * 16));
    }
}
