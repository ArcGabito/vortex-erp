using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using QRCoder;
using VortexApi.Suite.Money;
using VortexApi.Suite.Regional;
using VortexApi.Suite.Taxes;
using VortexErp.Core.Domain;
using VortexErp.Core.Services;

using static VortexErp.Core.Texts.CoreText;

namespace VortexErp.Core.Pdf;

/// <summary>
/// Representación impresa de una factura o boleta (A4, fase 7a): emisor con su RUC, el número, el cliente, las líneas, las
/// operaciones gravadas, exoneradas e inafectas con el IGV, la forma de pago y, si es electrónica, el código QR y el resumen
/// de la firma con la leyenda de SUNAT.
/// </summary>
public static class InvoicePdf
{
    private static readonly Color Brand = new(0x51, 0x2B, 0xD4);
    private static readonly Color Ink = new(0x16, 0x15, 0x1D);
    private static readonly Color Muted = new(0x66, 0x65, 0x6F);
    private static readonly Color Line = new(0xE6, 0xE6, 0xEA);

    public static byte[] Generate(InvoiceRow invoice, IReadOnlyList<InvoiceLineRow> lines, CompanyInfo? company, TaxRegime regime,
        FiscalDocumentRow? fiscal, CultureInfo culture)
    {
        PdfFonts.Register();
        var currency = Currencies.Get(invoice.CurrencyCode);
        string Money(decimal value) => currency.Format(value, culture);
        var electronic = fiscal is not null;
        var title = (invoice.Kind == InvoiceKinds.Invoice, electronic) switch
        {
            (true, true) => T("pdf.factura_electronica"),
            (true, false) => T("pdf.factura"),
            (false, true) => T("pdf.boleta_electronica"),
            _ => T("pdf.boleta"),
        };

        var document = new Document();
        document.Info.Title = $"{title} {invoice.Number}";
        document.Info.Author = company?.TradeName ?? company?.LegalName ?? "Vortex ERP";
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = PdfFonts.Family;
        normal.Font.Size = 8.5;
        normal.Font.Color = Ink;

        var section = document.AddSection();
        section.PageSetup = document.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = section.PageSetup.BottomMargin = Unit.FromCentimeter(1.6);
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(1.6);

        // Emisor a la izquierda; recuadro con RUC, tipo y número a la derecha (como lo piden las representaciones de SUNAT).
        var header = section.AddTable();
        header.AddColumn(Unit.FromCentimeter(11));
        header.AddColumn(Unit.FromCentimeter(6.8));
        var row = header.AddRow();
        var left = row.Cells[0];
        var name = left.AddParagraph(company?.TradeName ?? company?.LegalName ?? "");
        name.Format.Font.Size = 13;
        name.Format.Font.Bold = true;
        if (company?.TradeName is not null)
            left.AddParagraph(company.LegalName).Format.Font.Color = Muted;
        var box = row.Cells[1];
        box.Borders.Width = 1;
        box.Borders.Color = Brand;
        box.Format.Alignment = ParagraphAlignment.Center;
        box.Format.SpaceBefore = Unit.FromPoint(4);
        box.Format.SpaceAfter = Unit.FromPoint(4);
        var ruc = box.AddParagraph($"RUC {company?.TaxId ?? "—"}");
        ruc.Format.Font.Bold = true;
        var kind = box.AddParagraph(title.ToUpper(culture));
        kind.Format.Font.Bold = true;
        kind.Format.Font.Color = Brand;
        kind.Format.Font.Size = 11;
        box.AddParagraph(invoice.Number).Format.Font.Bold = true;

        var issued = RegionalSettings.ToCompanyTime(invoice.IssuedAt, company?.TimeZone);
        var info = section.AddTable();
        info.Format.SpaceBefore = Unit.FromCentimeter(0.5);
        info.AddColumn(Unit.FromCentimeter(3.2));
        info.AddColumn(Unit.FromCentimeter(14.6));
        void Info(string label, string value)
        {
            var r = info.AddRow();
            r.Cells[0].AddParagraph(label).Format.Font.Color = Muted;
            r.Cells[1].AddParagraph(value);
        }
        Info(T("pdf.cliente").TrimEnd(':', ' '), invoice.CustomerName ?? T("fiscal.clientes_varios"));
        if (invoice.CustomerDocument is { Length: > 0 } doc)
            Info(T("pdf.documento"), doc);
        Info(T("pdf.fecha_emision"), issued.ToString("d", culture) + " " + issued.ToString("HH:mm", culture));
        Info(T("pdf.moneda_label"), $"{currency.Code} ({currency.Symbol})");
        var credit = invoice.DueDate is { } dueDate && invoice.Total - invoice.Paid > 0 && dueDate > DateOnly.FromDateTime(issued.DateTime);
        Info(T("pdf.forma_de_pago"), credit
            ? T("pdf.credito_cuota", Money(invoice.Total - invoice.Paid), invoice.DueDate!.Value.ToString("d", culture))
            : T("pdf.contado"));

        // Líneas: con precios con IGV (Perú), el precio unitario e importe van con impuesto.
        var table = section.AddTable();
        table.Format.SpaceBefore = Unit.FromCentimeter(0.5);
        table.Borders.Bottom.Color = Line;
        table.Borders.Bottom.Width = 0.5;
        foreach (var width in new[] { 1.6, 10.2, 3.0, 3.0 })
            table.AddColumn(Unit.FromCentimeter(width));
        table.Columns[0].Format.Alignment = ParagraphAlignment.Right;
        table.Columns[2].Format.Alignment = ParagraphAlignment.Right;
        table.Columns[3].Format.Alignment = ParagraphAlignment.Right;
        var head = table.AddRow();
        head.HeadingFormat = true;
        head.Format.Font.Bold = true;
        head.Format.Font.Color = Muted;
        head.Cells[0].AddParagraph(T("pdf.cant"));
        head.Cells[1].AddParagraph(T("pdf.descripcion"));
        head.Cells[2].AddParagraph(T("pdf.precio_unitario"));
        head.Cells[3].AddParagraph(T("pdf.importe"));
        foreach (var l in lines)
        {
            var r = table.AddRow();
            r.Cells[0].AddParagraph(l.Quantity.ToString(l.Quantity % 1 == 0 ? "#,0" : "#,0.###", culture));
            r.Cells[1].AddParagraph(l.Description);
            r.Cells[2].AddParagraph(Money(l.UnitPrice));
            r.Cells[3].AddParagraph(Money(l.Total));
        }

        // Totales por tipo de operación, como los arma el XML.
        decimal taxed = 0, exempt = 0, unaffected = 0;
        foreach (var l in lines)
        {
            var (category, rate) = FiscalService.Category(regime, l.TaxCode);
            if (category == VortexApi.Client.VortexFiscalTaxCategories.Taxed)
                taxed += regime.PricesIncludeTax ? currency.Round(l.Total / (1 + rate)) : l.Total;
            else if (category == VortexApi.Client.VortexFiscalTaxCategories.Exempt)
                exempt += l.Total;
            else
                unaffected += l.Total;
        }
        var totals = section.AddTable();
        totals.Format.SpaceBefore = Unit.FromCentimeter(0.3);
        totals.AddColumn(Unit.FromCentimeter(14.8));
        totals.AddColumn(Unit.FromCentimeter(3.0));
        totals.Columns[0].Format.Alignment = ParagraphAlignment.Right;
        totals.Columns[1].Format.Alignment = ParagraphAlignment.Right;
        void Total(string label, decimal value, bool bold = false)
        {
            var r = totals.AddRow();
            r.Format.Font.Bold = bold;
            r.Cells[0].AddParagraph(label);
            r.Cells[1].AddParagraph(Money(value));
        }
        if (taxed > 0)
            Total(T("pdf.op_gravada"), taxed);
        if (exempt > 0)
            Total(T("pdf.op_exonerada"), exempt);
        if (unaffected > 0)
            Total(T("pdf.op_inafecta"), unaffected);
        Total(regime.TaxName, invoice.Tax);
        Total(T("pdf.total"), invoice.Total, bold: true);

        if (fiscal is not null)
        {
            // Código QR (texto de SUNAT: RUC|tipo|serie|número|IGV|total|fecha|tipo doc|doc|resumen|) y resumen de la firma.
            var qrText = fiscal.Qr ?? FiscalService.QrText(company?.TaxId ?? "", fiscal.Kind, fiscal.Series, fiscal.Number, invoice.Tax, invoice.Total,
                DateOnly.FromDateTime(issued.DateTime), invoice.CustomerDocument, fiscal.Hash);
            var foot = section.AddTable();
            foot.Format.SpaceBefore = Unit.FromCentimeter(0.6);
            foot.AddColumn(Unit.FromCentimeter(3.4));
            foot.AddColumn(Unit.FromCentimeter(14.4));
            var r = foot.AddRow();
            var image = r.Cells[0].AddImage("base64:" + Convert.ToBase64String(QrImage(qrText)));
            image.Width = Unit.FromCentimeter(3);
            image.LockAspectRatio = true;
            var legend = r.Cells[1];
            legend.AddParagraph(T(invoice.Kind == InvoiceKinds.Invoice ? "pdf.representacion_factura" : "pdf.representacion_boleta"));
            if (fiscal.Hash is { Length: > 0 } hash)
                legend.AddParagraph($"{T("pdf.resumen")}: {hash}").Format.Font.Color = Muted;
            var status = legend.AddParagraph(T("pdf.estado_sunat", T("fiscal.estado." + fiscal.Status)));
            status.Format.Font.Color = Muted;
            status.Format.SpaceBefore = Unit.FromPoint(4);
        }

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        using var memory = new MemoryStream();
        renderer.PdfDocument.Save(memory);
        return memory.ToArray();
    }

    /// <summary>El QR como imagen BMP (nivel de corrección M); PDFsharp no lee el PNG de 1 bit que arma QRCoder.</summary>
    public static byte[] QrImage(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new BitmapByteQRCode(data).GetGraphic(8);
    }
}
