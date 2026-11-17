using System.IO.Compression;
using System.Security;
using System.Text;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services.Support;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Tickets;

/// <summary>
/// A minimal .xlsx writer: one sheet of text and numbers, inline strings, no styles. Enough for an
/// export to open in Excel, LibreOffice and Numbers, without a spreadsheet library to vet and keep
/// patched for one download.
///
/// <para>Every cell value is XML-escaped, and text that would start a formula (=, +, -, @) is kept
/// as text: an export of what customers wrote must not become a spreadsheet that runs it.</para>
/// </summary>
public static class XlsxWriter
{
    public static byte[] Write(string sheetName, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        using MemoryStream buffer = new();

        using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>""");
            Add(zip, "_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Add(zip, "xl/workbook.xml",
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{Escape(SheetName(sheetName))}" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Add(zip, "xl/_rels/workbook.xml.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");

            StringBuilder sheet = new();
            sheet.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
            Row(sheet, 1, headers.Cast<object?>().ToList());

            int r = 2;
            foreach (IReadOnlyList<object?> row in rows)
            {
                Row(sheet, r++, row);
            }

            sheet.Append("</sheetData></worksheet>");
            Add(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
        }

        return buffer.ToArray();
    }

    private static void Row(StringBuilder sheet, int index, IReadOnlyList<object?> cells)
    {
        sheet.Append("<row r=\"").Append(index).Append("\">");

        for (int c = 0; c < cells.Count; c++)
        {
            string reference = Column(c) + index;

            switch (cells[c])
            {
                case null:
                    break;
                case int or long or decimal or double:
                    sheet.Append("<c r=\"").Append(reference).Append("\"><v>")
                        .Append(Convert.ToString(cells[c], System.Globalization.CultureInfo.InvariantCulture)).Append("</v></c>");
                    break;
                case DateTime d:
                    sheet.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"><is><t>")
                        .Append(BusinessCalendar.ToLocal(d).ToString("yyyy-MM-dd HH:mm")).Append("</t></is></c>");
                    break;
                default:
                    sheet.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                        .Append(Escape(Defuse(cells[c]!.ToString() ?? ""))).Append("</t></is></c>");
                    break;
            }
        }

        sheet.Append("</row>");
    }

    /// <summary>Text a spreadsheet would read as a formula, made plainly text.</summary>
    public static string Defuse(string text) =>
        text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text : text;

    private static string Column(int index)
    {
        string name = "";
        for (int i = index + 1; i > 0; i = (i - 1) / 26)
        {
            name = (char)('A' + (i - 1) % 26) + name;
        }

        return name;
    }

    private static string SheetName(string name)
    {
        string clean = new([.. name.Where(ch => ch is not ('\\' or '/' or '?' or '*' or '[' or ']' or ':'))]);
        return clean.Length == 0 ? "Sheet1" : clean.Length > 31 ? clean[..31] : clean;
    }

    // XML 1.0 forbids most control characters even escaped; they are dropped rather than written.
    private static string Escape(string s) =>
        SecurityElement.Escape(new string([.. s.Where(ch => ch is '\t' or '\n' or '\r' || ch >= ' ')])) ?? "";

    private static void Add(ZipArchive zip, string path, string content)
    {
        using Stream entry = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        entry.Write(bytes, 0, bytes.Length);
    }
}

/// <summary>Ticket counts for a period (docs/helpdesk.md, phase 6) — Zammad's reporting.</summary>
public sealed record SupportReport(
    DateTime From, DateTime To, int Created, int Closed,
    IReadOnlyList<(string Label, int Count)> ByStatus,
    IReadOnlyList<(string Label, int Count)> ByChannel,
    IReadOnlyList<(string Label, int Count)> ByPriority,
    IReadOnlyList<(DateOnly Day, int Created, int Closed)> Daily,
    int Tickets);

/// <summary>
/// Reporting over a tenant's tickets for a period: what was opened, by status, channel and
/// priority, and what was closed, day by day — plus an .xlsx of the tickets, capped at
/// <see cref="ExportLimit"/> rows as Zammad caps its download.
/// </summary>
public class SupportReportService(IDbContextFactory<SupportDbContext> dbFactory)
{
    public const int ExportLimit = 6000;

    /// <summary>A Swedish calendar period as the UTC window [from, to).</summary>
    public static (DateTime From, DateTime To) Window(DateOnly first, DateOnly last) =>
        (TimeZoneInfo.ConvertTimeToUtc(first.ToDateTime(TimeOnly.MinValue), BusinessCalendar.SwedishTime),
         TimeZoneInfo.ConvertTimeToUtc(last.AddDays(1).ToDateTime(TimeOnly.MinValue), BusinessCalendar.SwedishTime));

    public async Task<SupportReport> ReportAsync(Guid tenantId, DateOnly first, DateOnly last, CancellationToken ct = default)
    {
        (DateTime from, DateTime to) = Window(first, last);

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        var created = await db.Tickets.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.ReportedAt >= from && t.ReportedAt < to)
            .Select(t => new { t.Status, t.Channel, t.Priority, t.ReportedAt })
            .ToListAsync(ct);

        List<DateTime> closed = await db.Tickets.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.ClosedAt != null && t.ClosedAt >= from && t.ClosedAt < to)
            .Select(t => t.ClosedAt!.Value)
            .ToListAsync(ct);

        List<(DateOnly, int, int)> daily = [];
        for (DateOnly d = first; d <= last; d = d.AddDays(1))
        {
            daily.Add((d,
                created.Count(c => DateOnly.FromDateTime(BusinessCalendar.ToLocal(c.ReportedAt)) == d),
                closed.Count(c => DateOnly.FromDateTime(BusinessCalendar.ToLocal(c)) == d)));
        }

        return new SupportReport(
            from, to, created.Count, closed.Count,
            [.. created.GroupBy(c => c.Status).OrderBy(g => g.Key).Select(g => (g.Key.ToString(), g.Count()))],
            [.. created.GroupBy(c => c.Channel).OrderByDescending(g => g.Count()).Select(g => (g.Key.ToString(), g.Count()))],
            [.. created.GroupBy(c => c.Priority).OrderBy(g => g.Key).Select(g => (g.Key.ToString(), g.Count()))],
            daily,
            created.Count);
    }

    /// <summary>The period's tickets as an .xlsx, newest first, at most <see cref="ExportLimit"/> rows.</summary>
    public async Task<byte[]> ExportAsync(Guid tenantId, DateOnly first, DateOnly last, CancellationToken ct = default)
    {
        (DateTime from, DateTime to) = Window(first, last);

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        var rows = await db.Tickets.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.ReportedAt >= from && t.ReportedAt < to)
            .OrderByDescending(t => t.ReportedAt)
            .Take(ExportLimit)
            .Select(t => new
            {
                t.Number, t.Title, Customer = t.Customer.Name, t.Status, t.Priority, t.Channel, t.Assignee,
                t.ReportedAt, t.FirstResponseAt, t.ResolvedAt, t.ClosedAt,
                Tags = t.Tags.Select(g => g.Name).ToList(),
            })
            .ToListAsync(ct);

        return XlsxWriter.Write(
            $"Tickets {first:yyyy-MM-dd}",
            ["Number", "Title", "Customer", "Status", "Priority", "Channel", "Assignee", "Reported", "First response", "Resolved", "Closed", "Tags"],
            rows.Select(r => (IReadOnlyList<object?>)
            [
                r.Number, r.Title, r.Customer, r.Status.ToString(), r.Priority.ToString(), r.Channel.ToString(), r.Assignee,
                r.ReportedAt, r.FirstResponseAt, r.ResolvedAt, r.ClosedAt, string.Join(", ", r.Tags.Order(StringComparer.Ordinal)),
            ]));
    }
}
