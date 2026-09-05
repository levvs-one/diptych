using System.Globalization;
using System.IO;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Drawing;

namespace Visitplate.Core;

internal static class ReportLayout
{
    internal static Document Create(VisitDocument source,
        IReadOnlyDictionary<(Guid AssetId, int Turns), PreparedPhoto> photos)
    {
        var document = new Document();
        document.Info.Title = source.Details.Title;
        document.Info.Author = source.Details.Author;
        ConfigureStyles(document);
        for (int index = 0; index < source.Observations.Length; index++)
        {
            Observation observation = source.Observations[index];
            Section section = document.AddSection();
            section.PageSetup.PageFormat = PageFormat.A4;
            section.PageSetup.SectionStart = BreakType.BreakNextPage;
            section.PageSetup.LeftMargin = Unit.FromMillimeter(16);
            section.PageSetup.RightMargin = Unit.FromMillimeter(16);
            section.PageSetup.TopMargin = Unit.FromMillimeter(18);
            section.PageSetup.BottomMargin = Unit.FromMillimeter(18);
            section.PageSetup.HeaderDistance = Unit.FromMillimeter(7);
            section.PageSetup.FooterDistance = Unit.FromMillimeter(8);
            section.Headers.Primary.AddParagraph($"УЗЕЛ {index + 1:00}").Style = "Metadata";
            Paragraph footer = section.Footers.Primary.AddParagraph("VISITPLATE   /   ");
            footer.Style = "Metadata";
            footer.AddPageField();
            footer.AddText(" / ");
            footer.AddNumPagesField();

            if (index == 0)
            {
                section.AddParagraph(source.Details.Title, "ReportTitle");
                Detail(section, "Объект", source.Details.Site);
                Detail(section, "Исполнитель", source.Details.Author);
                Detail(section, "Дата выезда", source.Details.VisitDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
                Detail(section, "Заказчик", source.Details.Customer);
                Detail(section, "Номер заявки", source.Details.Reference);
            }
            section.AddParagraph($"{index + 1:00} / {observation.Title}", StyleNames.Heading1);
            section.AddParagraph(observation.Status switch
            {
                ObservationStatus.Recorded => "Зафиксировано",
                ObservationStatus.Completed => "Выполнено",
                ObservationStatus.FollowUp => "Нужен следующий выезд",
                _ => throw new InvalidOperationException("Неизвестный статус записи.")
            }, "Metadata");

            PhotoUse? before = observation.Photos.FirstOrDefault(photo => photo.Role == PhotoRole.Before);
            PhotoUse? after = observation.Photos.FirstOrDefault(photo => photo.Role == PhotoRole.After);
            if (before is not null && after is not null)
                AddPair(section, before, after, photos);
            else if (before is not null || after is not null)
                AddSingle(section, (before ?? after)!, photos, before is not null ? "До" : "После");
            TextBlock(section, "Наблюдение", observation.Finding);
            TextBlock(section, "Выполнено", observation.WorkDone);
            TextBlock(section, "Осталось", observation.Remaining);
            foreach (PhotoUse overview in observation.Photos.Where(photo => photo.Role == PhotoRole.Overview))
                AddSingle(section, overview, photos, "Общий вид");
            foreach (DocumentObject element in section.Elements.OfType<DocumentObject>())
                element.Tag = $"Узел {index + 1:00}" + (element is Table ? ", фотографии и подписи" : ", текст");
        }
        return document;
    }

    internal static void RequireTextFits(VisitDocument source)
    {
        using var measure = XGraphics.CreateMeasureContext(new XSize(595.276, 841.89),
            XGraphicsUnit.Point, XPageDirection.Downwards);
        var title = new XFont(ReportFonts.Family, 22, XFontStyleEx.Bold);
        var heading = new XFont(ReportFonts.Family, 14, XFontStyleEx.Bold);
        var body = new XFont(ReportFonts.Family, 10.5);
        var metadata = new XFont(ReportFonts.Family, 9);
        Check(source.Details.Title, title, 178, "Название отчёта");
        Check($"Объект: {source.Details.Site}", metadata, 178, "Объект");
        Check($"Исполнитель: {source.Details.Author}", metadata, 178, "Исполнитель");
        Check($"Заказчик: {source.Details.Customer}", metadata, 178, "Заказчик");
        Check($"Номер заявки: {source.Details.Reference}", metadata, 178, "Номер заявки");
        for (int index = 0; index < source.Observations.Length; index++)
        {
            Observation node = source.Observations[index];
            string location = $"Узел {index + 1:00}";
            Check($"{index + 1:00} / {node.Title}", heading, 178, $"{location}, название");
            Check(node.Finding, body, 178, $"{location}, наблюдение");
            Check(node.WorkDone, body, 178, $"{location}, выполнено");
            Check(node.Remaining, body, 178, $"{location}, осталось");
            bool paired = node.Photos.Any(photo => photo.Role == PhotoRole.Before)
                && node.Photos.Any(photo => photo.Role == PhotoRole.After);
            foreach (PhotoUse photo in node.Photos)
                Check(photo.Caption, metadata, paired && photo.Role != PhotoRole.Overview ? 85 : 178,
                    $"{location}, подпись фотографии {photo.AssetId:N}");
        }

        void Check(string text, XFont font, double millimeters, string location)
        {
            // MigraDoc does not wrap an oversized word. NBSP is deliberately not a break opportunity.
            foreach (string word in text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = word.Split('\u00ad');
                for (int index = 0; index < parts.Length; index++)
                    if (measure.MeasureString(parts[index] + (index < parts.Length - 1 ? "-" : ""), font).Width
                        > Unit.FromMillimeter(millimeters).Point)
                        throw new InvalidDataException($"{location}: фрагмент текста не помещается по ширине. "
                            + "Разделите длинный фрагмент пробелом или переводом строки. PDF не подготовлен.");
            }
        }
    }

    internal static void RequirePageBounds(PdfDocumentRenderer renderer)
    {
        for (int page = 1; page <= renderer.PageCount; page++)
            foreach (RenderInfo info in renderer.DocumentRenderer.GetRenderInfoFromPage(page) ?? [])
            {
                Area area = info.LayoutInfo.ContentArea;
                if (area.X.Point < Unit.FromMillimeter(16).Point - 0.1
                    || area.X.Point + area.Width.Point > Unit.FromMillimeter(194).Point + 0.1
                    || area.Y.Point < Unit.FromMillimeter(18).Point - 0.1
                    || area.Y.Point + area.Height.Point > Unit.FromMillimeter(279).Point + 0.1)
                    throw new InvalidDataException($"{info.DocumentObject.Tag}, страница {page}: "
                        + "блок выходит за границы печати. Перенесите длинную подпись в наблюдение или разделите запись. PDF не подготовлен.");
            }
    }

    private static void ConfigureStyles(Document document)
    {
        Style normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = ReportFonts.Family;
        normal.Font.Size = 10.5;
        normal.Font.Color = Color.Parse("#282C2C");
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(7);
        normal.ParagraphFormat.WidowControl = true;
        normal.ParagraphFormat.KeepTogether = false;
        Style title = document.AddStyle("ReportTitle", StyleNames.Normal);
        title.Font.Size = 22;
        title.Font.Bold = true;
        Style heading = document.Styles[StyleNames.Heading1]!;
        heading.Font.Size = 14;
        heading.Font.Bold = true;
        heading.Font.Color = Color.Parse("#9A3D25");
        heading.ParagraphFormat.SpaceBefore = Unit.FromPoint(10);
        heading.ParagraphFormat.KeepWithNext = true;
        heading.ParagraphFormat.KeepTogether = false;
        Style subheading = document.Styles[StyleNames.Heading2]!;
        subheading.Font.Size = 11;
        subheading.Font.Bold = true;
        subheading.ParagraphFormat.KeepWithNext = true;
        document.AddStyle("Metadata", StyleNames.Normal).Font.Size = 9;
        document.AddStyle("PhotoLabel", StyleNames.Normal).Font.Bold = true;
    }

    private static void Detail(Section section, string label, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            section.AddParagraph($"{label}: {text}", "Metadata");
    }

    private static void TextBlock(Section section, string label, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        section.AddParagraph(label, StyleNames.Heading2);
        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
            section.AddParagraph(line);
    }

    private static void AddPair(Section section, PhotoUse before, PhotoUse after,
        IReadOnlyDictionary<(Guid AssetId, int Turns), PreparedPhoto> photos)
    {
        PhotoUse[] uses = [before, after];
        double imageHeight = uses.Max(use =>
        {
            PreparedPhoto photo = photos[(use.AssetId, use.ManualQuarterTurns)];
            return Math.Min(88, 83.0 * photo.PixelHeight / photo.PixelWidth);
        });
        foreach (PhotoUse use in uses)
            RequireCaptionFits(use.Caption, 85, imageHeight, section.Document!.Sections.Count, use.AssetId);
        Table table = section.AddTable();
        table.AddColumn(Unit.FromMillimeter(89));
        table.AddColumn(Unit.FromMillimeter(89));
        table.LeftPadding = 0;
        table.RightPadding = Unit.FromMillimeter(4);
        Row labels = table.AddRow();
        labels.KeepWith = 2;
        labels.Cells[0].AddParagraph("До").Style = "PhotoLabel";
        labels.Cells[1].AddParagraph("После").Style = "PhotoLabel";
        Row images = table.AddRow();
        images.KeepWith = 1;
        Row captions = table.AddRow();
        for (int column = 0; column < uses.Length; column++)
        {
            PhotoUse use = uses[column];
            PreparedPhoto photo = photos[(use.AssetId, use.ManualQuarterTurns)];
            var image = images.Cells[column].AddImage(photo.Path);
            image.Width = Unit.FromMillimeter(Math.Min(83, 88.0 * photo.PixelWidth / photo.PixelHeight));
            captions.Cells[column].AddParagraph(use.Caption).Style = "Metadata";
        }
    }

    private static void AddSingle(Section section, PhotoUse use,
        IReadOnlyDictionary<(Guid AssetId, int Turns), PreparedPhoto> photos, string label)
    {
        PreparedPhoto photo = photos[(use.AssetId, use.ManualQuarterTurns)];
        RequireCaptionFits(use.Caption, 178, Math.Min(105, 174.0 * photo.PixelHeight / photo.PixelWidth),
            section.Document!.Sections.Count, use.AssetId);
        Table table = section.AddTable();
        table.AddColumn(Unit.FromMillimeter(178));
        table.LeftPadding = 0;
        table.RightPadding = 0;
        Row heading = table.AddRow();
        heading.KeepWith = 2;
        heading.Cells[0].AddParagraph(label).Style = "PhotoLabel";
        Row imageRow = table.AddRow();
        imageRow.KeepWith = 1;
        var image = imageRow.Cells[0].AddImage(photo.Path);
        image.Width = Unit.FromMillimeter(Math.Min(174, 105.0 * photo.PixelWidth / photo.PixelHeight));
        table.AddRow().Cells[0].AddParagraph(use.Caption).Style = "Metadata";
    }

    private static void RequireCaptionFits(string caption, double width, double imageHeight, int node, Guid assetId)
    {
        // Table rows can silently overflow. Let MigraDoc paginate the same caption outside a table first.
        var probe = new Document();
        ConfigureStyles(probe);
        Section section = probe.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.LeftMargin = Unit.FromMillimeter(16);
        section.PageSetup.RightMargin = Unit.FromMillimeter(194 - width);
        section.PageSetup.TopMargin = Unit.FromMillimeter(18);
        section.PageSetup.BottomMargin = Unit.FromMillimeter(18);
        section.AddParagraph("До", "PhotoLabel");
        Paragraph reservedImageHeight = section.AddParagraph();
        reservedImageHeight.Format.LineSpacingRule = LineSpacingRule.Exactly;
        reservedImageHeight.Format.LineSpacing = Unit.FromMillimeter(imageHeight);
        reservedImageHeight.Format.SpaceAfter = 0;
        section.AddParagraph(caption, "Metadata");
        var renderer = new PdfDocumentRenderer { Document = probe };
        using var pdf = renderer.PdfDocument;
        renderer.PrepareRenderPages();
        if (renderer.PageCount != 1)
            throw new InvalidDataException($"Узел {node:00}, подпись фотографии {assetId:N}: "
                + "фотография с подписью выходит за границы печати. Перенесите длинную подпись в наблюдение. PDF не подготовлен.");
    }
}
