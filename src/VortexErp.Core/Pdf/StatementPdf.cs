using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using VortexApi.Suite.Money;
using VortexApi.Suite.Regional;
using VortexErp.Core.Domain;

using static VortexErp.Core.Texts.CoreText;

namespace VortexErp.Core.Pdf;

/// <summary>Estado de cuenta de un cliente en PDF (A4), con PDFsharp/MigraDoc y sin dependencias nativas.</summary>
public static class StatementPdf
{
    /// <summary>El morado de la marca Vortex (el de .NET).</summary>
    private static readonly Color Brand = new(0x51, 0x2B, 0xD4);
    private static readonly Color Ink = new(0x16, 0x15, 0x1D);
    private static readonly Color Muted = new(0x66, 0x65, 0x6F);
    private static readonly Color Line = new(0xE6, 0xE6, 0xEA);

    public static byte[] Generate(CustomerStatement statement, CompanyInfo? company, CultureInfo culture)
    {
        PdfFonts.Register();
        var currency = Currencies.Get(statement.CurrencyCode);
        string Money(decimal value) => currency.Format(value, culture);
        string Day(DateOnly day) => day.ToString("d", culture);
        DateTime Local(DateTimeOffset at) => RegionalSettings.ToCompanyTime(at, company?.TimeZone).DateTime;

        var document = new Document();
        document.Info.Title = T("pdf.estado_de_cuenta_de", statement.CustomerName);
        document.Info.Author = company?.TradeName ?? company?.LegalName ?? "Vortex ERP";
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = PdfFonts.Family;
        normal.Font.Size = 8.5;
        normal.Font.Color = Ink;

        var section = document.AddSection();
        section.PageSetup = document.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = section.PageSetup.BottomMargin = Unit.FromCentimeter(1.8);
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(1.6);

        // Encabezado: empresa a la izquierda, título y periodo a la derecha.
        var header = section.AddTable();
        header.AddColumn(Unit.FromCentimeter(10));
        header.AddColumn(Unit.FromCentimeter(7.8));
        var row = header.AddRow();
        var left = row.Cells[0];
        var name = left.AddParagraph(company?.TradeName ?? company?.LegalName ?? "");
        name.Format.Font.Size = 13;
        name.Format.Font.Bold = true;
        if (company?.TradeName is not null)
            left.AddParagraph(company.LegalName).Format.Font.Color = Muted;
        if (company?.TaxId is { Length: > 0 } taxId)
            left.AddParagraph(taxId).Format.Font.Color = Muted;
        var right = row.Cells[1];
        right.Format.Alignment = ParagraphAlignment.Right;
        var title = right.AddParagraph(T("pdf.estado_de_cuenta"));
        title.Format.Font.Size = 15;
        title.Format.Font.Bold = true;
        title.Format.Font.Color = Brand;
        right.AddParagraph(T("pdf.del_al", Day(statement.From), Day(statement.To))).Format.Font.Color = Muted;
        right.AddParagraph(T("pdf.moneda", $"{currency.Code} ({currency.Symbol})")).Format.Font.Color = Muted;

        var customer = section.AddParagraph();
        customer.Format.SpaceBefore = Unit.FromCentimeter(0.6);
        customer.Format.SpaceAfter = Unit.FromCentimeter(0.4);
        customer.AddFormattedText(T("pdf.cliente") + " ", TextFormat.Bold);
        customer.AddText(statement.CustomerName + (statement.CustomerDocument is { } doc ? $" · {doc}" : ""));

        // Movimientos del periodo con el saldo acumulado.
        var table = section.AddTable();
        table.Borders.Bottom.Color = Line;
        table.Borders.Bottom.Width = 0.5;
        foreach (var width in new[] { 2.1, 3.0, 2.6, 2.1, 2.6, 2.6, 2.8 })
            table.AddColumn(Unit.FromCentimeter(width));
        for (var i = 4; i < 7; i++)
            table.Columns[i].Format.Alignment = ParagraphAlignment.Right;

        var head = table.AddRow();
        head.HeadingFormat = true;
        head.Shading.Color = Ink;
        head.Format.Font.Color = Colors.White;
        head.Format.Font.Bold = true;
        head.TopPadding = head.BottomPadding = Unit.FromPoint(4);
        string[] titles = [T("pdf.fecha"), T("pdf.documento"), T("pdf.referencia"), T("pdf.vence"), T("pdf.cargo"), T("pdf.abono"), T("pdf.saldo")];
        for (var i = 0; i < titles.Length; i++)
            head.Cells[i].AddParagraph(titles[i]);

        AddRow(table, [Day(statement.From), T("pdf.saldo_anterior"), "", "", "", "", Money(statement.OpeningBalance)], bold: true);
        foreach (var e in statement.Entries)
        {
            AddRow(table,
            [
                Local(e.At).ToString("d", culture), $"{KindName(e.Kind)} {e.Number}", e.Reference ?? "", e.DueDate is { } due ? Day(due) : "",
                e.Charge > 0 ? Money(e.Charge) : "", e.Credit > 0 ? Money(e.Credit) : "", Money(e.Balance),
            ], bold: false);
        }
        if (statement.Entries.Count == 0)
            AddRow(table, ["", T("pdf.sin_movimientos"), "", "", "", "", ""], bold: false);

        // Totales.
        var totals = section.AddTable();
        totals.AddColumn(Unit.FromCentimeter(13.6));
        totals.AddColumn(Unit.FromCentimeter(4.2));
        totals.Columns[1].Format.Alignment = ParagraphAlignment.Right;
        totals.Format.SpaceBefore = Unit.FromCentimeter(0.3);
        void Total(string label, string value, bool strong)
        {
            var r = totals.AddRow();
            r.TopPadding = Unit.FromPoint(3);
            r.Cells[0].AddParagraph(label).Format.Alignment = ParagraphAlignment.Right;
            r.Cells[1].AddParagraph(value);
            r.Format.Font.Bold = strong;
            if (strong)
                r.Format.Font.Size = 10.5;
        }
        Total(T("pdf.cargos_del_periodo"), Money(statement.Entries.Sum(e => e.Charge)), false);
        Total(T("pdf.abonos_del_periodo"), Money(statement.Entries.Sum(e => e.Credit)), false);
        Total(statement.ClosingBalance < 0 ? T("pdf.saldo_a_favor") : T("pdf.saldo_al", Day(statement.To)),
            Money(Math.Abs(statement.ClosingBalance)), true);
        if (statement.Overdue > 0)
            Total(T("pdf.vencido_a_hoy"), Money(statement.Overdue), false);

        var footer = section.Footers.Primary.AddParagraph(T("pdf.generado", Local(statement.GeneratedAt).ToString("g", culture)));
        footer.Format.Alignment = ParagraphAlignment.Center;
        footer.Format.Font.Size = 7;
        footer.Format.Font.Color = Muted;

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        using var memory = new MemoryStream();
        renderer.PdfDocument.Save(memory);
        return memory.ToArray();
    }

    private static void AddRow(Table table, string[] cells, bool bold)
    {
        var row = table.AddRow();
        row.TopPadding = row.BottomPadding = Unit.FromPoint(3);
        row.Borders.Bottom.Color = Line;
        row.Borders.Bottom.Width = 0.5;
        row.Format.Font.Bold = bold;
        for (var i = 0; i < cells.Length; i++)
            row.Cells[i].AddParagraph(cells[i]);
    }

    public static string KindName(string kind) => kind switch
    {
        StatementEntryKinds.Invoice => T("pdf.comprobante"),
        StatementEntryKinds.CreditNote => T("pdf.nota_de_credito"),
        StatementEntryKinds.DebitNote => T("pdf.nota_de_debito"),
        StatementEntryKinds.Payment => T("pdf.cobro"),
        _ => kind,
    };
}
