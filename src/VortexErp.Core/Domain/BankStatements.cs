using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VortexErp.Core.Domain;

/// <summary>Una línea del extracto del banco: abono (+) o cargo (−).</summary>
public sealed record BankEntry(DateOnly On, decimal Amount, string Description, string? Reference);

/// <summary>
/// Lee extractos bancarios (ERP-37): CSV de cualquier banco (fecha, descripción, importe o cargo/abono, referencia; con
/// cabecera en español, inglés o portugués), OFX/QFX y CAMT.053 (ISO 20022). El formato se reconoce por el contenido.
/// </summary>
public static partial class BankStatements
{
    public static IReadOnlyList<BankEntry> Parse(string content)
    {
        var text = content.TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (text.Contains("<BkToCstmrStmt", StringComparison.Ordinal) || text.Contains(":camt.053", StringComparison.Ordinal))
            return ParseCamt(text);
        if (text.Contains("<OFX", StringComparison.OrdinalIgnoreCase) || text.StartsWith("OFXHEADER", StringComparison.Ordinal))
            return ParseOfx(text);
        return ParseCsv(text);
    }

    // ---------- CAMT.053 ----------

    private static List<BankEntry> ParseCamt(string xml)
    {
        var doc = XDocument.Parse(xml);
        XElement? Child(XElement? e, string name) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == name);
        XElement? Path(XElement? e, params string[] names) => names.Aggregate(e, Child);
        var entries = new List<BankEntry>();
        foreach (var ntry in doc.Descendants().Where(e => e.Name.LocalName == "Ntry"))
        {
            if (!decimal.TryParse(Child(ntry, "Amt")?.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
                continue;
            if (Child(ntry, "CdtDbtInd")?.Value == "DBIT")
                amount = -amount;
            var date = Path(ntry, "BookgDt", "Dt")?.Value ?? Path(ntry, "BookgDt", "DtTm")?.Value ?? Path(ntry, "ValDt", "Dt")?.Value;
            if (date is null || !DateOnly.TryParse(date[..Math.Min(10, date.Length)], CultureInfo.InvariantCulture, out var on))
                continue;
            var tx = Path(ntry, "NtryDtls", "TxDtls");
            var description = Path(tx, "RmtInf", "Ustrd")?.Value ?? Child(ntry, "AddtlNtryInf")?.Value
                              ?? Path(tx, "RltdPties", "Dbtr", "Nm")?.Value ?? Path(tx, "RltdPties", "Cdtr", "Nm")?.Value ?? "";
            var reference = Child(ntry, "AcctSvcrRef")?.Value ?? Path(tx, "Refs", "EndToEndId")?.Value ?? Child(ntry, "NtryRef")?.Value;
            entries.Add(new BankEntry(on, amount, description.Trim(), Clean(reference)));
        }
        return entries;
    }

    // ---------- OFX (SGML o XML) ----------

    [GeneratedRegex(@"<STMTTRN>(.*?)(</STMTTRN>|(?=<STMTTRN>)|(?=</BANKTRANLIST>))", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex OfxTransaction();

    private static List<BankEntry> ParseOfx(string ofx)
    {
        static string? Tag(string block, string tag)
        {
            var m = Regex.Match(block, $@"<{tag}>([^<\r\n]*)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
        var entries = new List<BankEntry>();
        foreach (Match m in OfxTransaction().Matches(ofx))
        {
            var block = m.Groups[1].Value;
            var date = Tag(block, "DTPOSTED");
            if (date is not { Length: >= 8 } || !DateOnly.TryParseExact(date[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var on))
                continue;
            if (ParseAmount(Tag(block, "TRNAMT")) is not { } amount)
                continue;
            var description = string.Join(" ", new[] { Tag(block, "NAME"), Tag(block, "MEMO") }.Where(x => !string.IsNullOrWhiteSpace(x)));
            entries.Add(new BankEntry(on, amount, description, Clean(Tag(block, "CHECKNUM")) ?? Clean(Tag(block, "FITID"))));
        }
        return entries;
    }

    // ---------- CSV ----------

    private static readonly string[] DateNames = ["fecha", "date", "data", "fecha operación", "fecha de operación", "fecha valor", "booking date"];
    private static readonly string[] DescriptionNames = ["descripción", "descripcion", "description", "concepto", "detalle", "descrição", "historico", "histórico", "memo"];
    private static readonly string[] AmountNames = ["importe", "monto", "amount", "valor", "importe (s/)", "monto (s/)"];
    private static readonly string[] DebitNames = ["cargo", "cargos", "debe", "debit", "débito", "debito", "retiro", "withdrawal"];
    private static readonly string[] CreditNames = ["abono", "abonos", "haber", "credit", "crédito", "credito", "depósito", "deposito", "deposit"];
    private static readonly string[] ReferenceNames = ["referencia", "reference", "nro. operación", "n° operación", "número de operación", "operación", "operacion", "documento", "ref"];

    private static List<BankEntry> ParseCsv(string csv)
    {
        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0)
            return [];
        var separator = new[] { ';', '\t', ',' }.MaxBy(s => Split(lines[0], s).Count);
        var rows = lines.Select(l => Split(l, separator)).ToList();

        // La cabecera puede no estar en la primera fila (algunos bancos ponen el titular y el número de cuenta antes).
        var headerIndex = rows.FindIndex(r => r.Any(c => DateNames.Contains(Normalize(c))));
        int date = 0, description = 1, amount = 2, debit = -1, credit = -1, reference = -1;
        if (headerIndex >= 0)
        {
            var header = rows[headerIndex].Select(Normalize).ToList();
            int Find(string[] names) => header.FindIndex(names.Contains);
            date = Find(DateNames);
            description = Find(DescriptionNames);
            amount = Find(AmountNames);
            debit = Find(DebitNames);
            credit = Find(CreditNames);
            reference = Find(ReferenceNames);
            rows = rows.Skip(headerIndex + 1).ToList();
        }

        var entries = new List<BankEntry>();
        foreach (var r in rows)
        {
            string? Cell(int i) => i >= 0 && i < r.Count ? r[i] : null;
            if (ParseDate(Cell(date)) is not { } on)
                continue;
            decimal? value = amount >= 0 ? ParseAmount(Cell(amount)) : null;
            if (value is null && (debit >= 0 || credit >= 0))
                value = (ParseAmount(Cell(credit)) ?? 0) - Math.Abs(ParseAmount(Cell(debit)) ?? 0);
            if (value is not { } v || v == 0)
                continue;
            entries.Add(new BankEntry(on, v, Cell(description)?.Trim() ?? "", Clean(Cell(reference))));
        }
        return entries;
    }

    private static List<string> Split(string line, char separator)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (ch == separator && !quoted)
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }
        cells.Add(current.ToString().Trim());
        return cells;
    }

    private static string Normalize(string header) => header.Trim().Trim('"').ToLowerInvariant();

    private static readonly string[] DateFormats = ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd-MM-yyyy", "dd.MM.yyyy", "yyyyMMdd", "dd/MM/yy"];

    public static DateOnly? ParseDate(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v))
            return null;
        if (v.Length > 10 && v[10] is ' ' or 'T')
            v = v[..10];
        return DateOnly.TryParseExact(v, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>Importe con punto o coma decimal, separador de miles, signo, paréntesis (negativo) o símbolo de moneda.</summary>
    public static decimal? ParseAmount(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v))
            return null;
        var negative = v.StartsWith('(') && v.EndsWith(')') || v.EndsWith('-');
        v = new string(v.Where(ch => char.IsDigit(ch) || ch is '.' or ',' or '-').ToArray()).Trim('-');
        if (v.Length == 0)
            return null;
        if (value!.TrimStart().StartsWith('-'))
            negative = true;
        int lastDot = v.LastIndexOf('.'), lastComma = v.LastIndexOf(',');
        // El último separador es el decimal si le siguen 1 o 2 dígitos (o es el único y no forma grupos de 3).
        if (lastComma > lastDot && v.Length - lastComma - 1 is 1 or 2)
            v = v.Replace(".", "").Replace(',', '.');
        else
            v = v.Replace(",", "");
        return decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? (negative ? -d : d) : null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
