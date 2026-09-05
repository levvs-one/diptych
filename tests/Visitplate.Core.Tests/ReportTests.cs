using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Pdf.IO;
using PdfSharp.Logging;

namespace Visitplate.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ReportTests
{
    private string directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        string root = Environment.GetEnvironmentVariable("VISITPLATE_TEST_ROOT") ?? Path.GetTempPath();
        directory = Path.Combine(root, nameof(ReportTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [TestMethod]
    public async Task PreparePublishAndNativeReadPreserveOriginalsAndEmbedCompleteFonts()
    {
        VisitProject project = await CreateReport();
        var snapshots = project.Document.Assets.Select(asset =>
        {
            string path = VisitPaths.AssetPath(project, asset);
            return (path, Bytes: File.ReadAllBytes(path), Time: File.GetLastWriteTimeUtc(path), Attributes: File.GetAttributes(path));
        }).ToArray();
        ReportDraft draft = await VisitReports.PrepareAsync(project);
        Assert.IsTrue(draft.PageCount >= 1);
        Assert.AreEqual(2, draft.PhotoCount);
        string output = await VisitReports.PublishAsync(project, draft, Path.Combine(directory, "result.pdf"));
        CollectionAssert.AreEqual(File.ReadAllBytes(draft.Path), File.ReadAllBytes(output));
        Assert.AreEqual(draft.Sha256, Hash(output));
        using FileStream stream = File.OpenRead(output);
        using var random = stream.AsRandomAccessStream();
        var native = await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(random);
        Assert.AreEqual((uint)draft.PageCount, native.PageCount);
        using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var fontResources = pdf.Pages[0].Resources.Elements.GetDictionary("/Font")!;
        var embedded = new HashSet<string>();
        foreach (var key in fontResources.Elements.Keys)
        {
            var font = fontResources.Elements.GetDictionary(key)!;
            var descendants = font.Elements.GetArray("/DescendantFonts")!;
            var descendant = descendants.Elements.GetDictionary(0)!;
            var descriptor = descendant.Elements.GetDictionary("/FontDescriptor")!;
            byte[] bytes = descriptor.Elements.GetDictionary("/FontFile2")!.Stream.UnfilteredValue;
            embedded.Add(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        Assert.IsTrue(embedded.Contains("f5f552c8c5edb61fe6efb824baf4d4de47b1a8689ab4925ff43f7bd6a4ebece5"));
        Assert.IsTrue(embedded.Contains("3a08a47daa00cade516425c15c57615aef2fd418ec9811a7b9f465088f92cc05"));
        foreach (var snapshot in snapshots)
        {
            CollectionAssert.AreEqual(snapshot.Bytes, File.ReadAllBytes(snapshot.path));
            Assert.AreEqual(snapshot.Time, File.GetLastWriteTimeUtc(snapshot.path));
            Assert.AreEqual(snapshot.Attributes, File.GetAttributes(snapshot.path));
        }
    }

    [TestMethod]
    public async Task RepeatedGenerationUsesInitializedFontsAndNewDrafts()
    {
        VisitProject project = await CreateReport();
        ReportDraft first = await VisitReports.PrepareAsync(project);
        ReportDraft second = await VisitReports.PrepareAsync(project);
        Assert.AreNotEqual(first.Path, second.Path);
        Assert.AreEqual(first.PageCount, second.PageCount);
        Assert.AreEqual(first.Sha256, Hash(first.Path));
        Assert.AreEqual(second.Sha256, Hash(second.Path));
    }

    [TestMethod]
    public async Task OccupiedOutputAndProjectOutputNeverOverwrite()
    {
        VisitProject project = await CreateReport();
        ReportDraft draft = await VisitReports.PrepareAsync(project);
        string target = Path.Combine(directory, "occupied.pdf");
        byte[] bytes = [4, 3, 2, 1];
        File.WriteAllBytes(target, bytes);
        await Assert.ThrowsAsync<IOException>(() => VisitReports.PublishAsync(project, draft, target));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(target));
        await Assert.ThrowsAsync<ArgumentException>(() => VisitReports.PublishAsync(project, draft,
            Path.Combine(project.DirectoryPath, "result.pdf")));
        Assert.IsFalse(File.Exists(Path.Combine(project.DirectoryPath, "result.pdf")));
    }

    [TestMethod]
    public async Task StaleRevisionAndWrongProjectDraftAreRejected()
    {
        VisitProject project = await CreateReport();
        ReportDraft draft = await VisitReports.PrepareAsync(project);
        VisitProject next = await VisitProjects.SaveAsync(project, project.Document with
        {
            Details = project.Document.Details with { Site = "Другой объект" }
        });
        string target = Path.Combine(directory, "stale.pdf");
        await Assert.ThrowsAsync<InvalidDataException>(() => VisitReports.PublishAsync(next, draft, target));
        await Assert.ThrowsAsync<IOException>(() => VisitReports.PublishAsync(project, draft, target));
        VisitProject other = await VisitProjects.CreateAsync(Path.Combine(directory, "other"), project.Document.Details);
        await Assert.ThrowsAsync<InvalidDataException>(() => VisitReports.PublishAsync(other, draft, target));
        Assert.IsFalse(File.Exists(target));
    }

    [TestMethod]
    public async Task ChangedDraftAndForgedDraftPathAreRejected()
    {
        VisitProject project = await CreateReport();
        ReportDraft draft = await VisitReports.PrepareAsync(project);
        var forged = new ReportDraft(Path.Combine(directory, "external.pdf"), draft.ProjectId, draft.Revision,
            draft.DocumentFingerprint, draft.Sha256, draft.PageCount, draft.PhotoCount, draft.Warnings);
        await Assert.ThrowsAsync<InvalidDataException>(() => VisitReports.PublishAsync(project, forged,
            Path.Combine(directory, "forged.pdf")));
        byte[] bytes = File.ReadAllBytes(draft.Path);
        bytes[^1] ^= 1;
        File.WriteAllBytes(draft.Path, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => VisitReports.PublishAsync(project, draft,
            Path.Combine(directory, "changed.pdf")));
    }

    [TestMethod]
    public async Task ChangedOriginalBlocksPreparationAndPublicationAndKeepsRecoveryPath()
    {
        VisitProject project = await CreateReport();
        ReportDraft draft = await VisitReports.PrepareAsync(project);
        string source = VisitPaths.AssetPath(project, project.Document.Assets[0]);
        byte[] bytes = File.ReadAllBytes(source);
        bytes[^1] ^= 1;
        File.WriteAllBytes(source, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => VisitReports.PrepareAsync(project));
        string target = Path.Combine(directory, "changed-source.pdf");
        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            VisitReports.PublishAsync(project, draft, target));
        Assert.IsFalse(File.Exists(target));
        Assert.IsTrue(failure.Data["PartialPath"] is string partial && File.Exists(partial));
    }

    [TestMethod]
    public async Task UnsupportedGlyphCannotProduceSuccessfulDraft()
    {
        VisitProject project = await CreateReport();
        Observation node = project.Document.Observations[0] with { Finding = "Нет glyph: \U0010FFFF" };
        project = await VisitProjects.SaveAsync(project, project.Document with { Observations = [node] });
        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() => VisitReports.PrepareAsync(project));
        StringAssert.Contains(failure.Message, "U+10FFFF");
        Assert.AreEqual(0, Directory.GetFiles(project.DirectoryPath, "report.pdf", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    [DataRow(VisitPhase.Normalizing)]
    [DataRow(VisitPhase.Paginating)]
    [DataRow(VisitPhase.Verifying)]
    public async Task CancellationAtRealStageNeverPublishesPdf(VisitPhase phase)
    {
        VisitProject project = await CreateReport();
        using var cancellation = new CancellationTokenSource();
        bool reached = false;
        var progress = new DirectProgress(update =>
        {
            if (update.Phase != phase) return;
            reached = true;
            cancellation.Cancel();
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => VisitReports.PrepareAsync(project, progress, cancellation.Token));
        Assert.IsTrue(reached);
        Assert.AreEqual(0, Directory.GetFiles(project.DirectoryPath, "report.pdf", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task CancelledPublishDoesNotCreateDestinationOrPartial()
    {
        VisitProject project = await CreateReport();
        ReportDraft draft = await VisitReports.PrepareAsync(project);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        string target = Path.Combine(directory, "cancelled.pdf");
        await Assert.ThrowsAsync<OperationCanceledException>(() => VisitReports.PublishAsync(project, draft, target, cancellation.Token));
        Assert.IsFalse(File.Exists(target));
        Assert.AreEqual(0, Directory.GetFiles(directory, "*.partial").Length);
    }

    [TestMethod]
    public async Task ValidationRejectsInvalidStructureAndReportsMissingAfterWithoutFakeImage()
    {
        VisitProject project = await CreateReport();
        Assert.IsTrue(VisitReports.Validate(null!).Any(issue => issue.Severity == VisitIssueSeverity.Error));
        VisitDocument noTitle = project.Document with { Details = project.Document.Details with { Title = " " } };
        Assert.IsTrue(VisitReports.Validate(noTitle).Any(issue => issue.Code == "TitleRequired"));
        Assert.IsTrue(VisitReports.Validate(project.Document with { Observations = [] }).Any(issue => issue.Code == "PhotoRequired"));
        Observation node = project.Document.Observations[0] with { Photos = [project.Document.Observations[0].Photos[0]] };
        project = await VisitProjects.SaveAsync(project, project.Document with { Observations = [node] });
        ReportDraft draft = await VisitReports.PrepareAsync(project);
        Assert.AreEqual(1, draft.PhotoCount);
        Assert.IsTrue(draft.Warnings.Any(issue => issue.Code == "AfterPhotoMissing"));
    }

    [TestMethod]
    public async Task LongNotesFlowAndNodeIdentifierRepeatsOnContinuedPages()
    {
        VisitProject project = await CreateReport();
        string text = string.Concat(Enumerable.Repeat("Длинная кириллическая запись не должна теряться. ", 70));
        Observation first = project.Document.Observations[0] with { Finding = text, WorkDone = text, Remaining = text };
        Observation second = first with { Id = Guid.NewGuid(), Title = "Следующий узел" };
        project = await VisitProjects.SaveAsync(project, project.Document with { Observations = [first, second] });
        ReportDraft draft = await VisitReports.PrepareAsync(project);
        Assert.IsTrue(draft.PageCount >= 4);
        var photos = new Dictionary<(Guid AssetId, int Turns), PreparedPhoto>();
        foreach (PhotoUse use in first.Photos)
            photos.Add((use.AssetId, 0), new PreparedPhoto(use.AssetId, "unused.jpg", 400, 300, 1, ""));
        Document layout = ReportLayout.Create(project.Document, photos);
        Assert.AreEqual(2, layout.Sections.Count);
        Assert.AreEqual("УЗЕЛ 01", ((Text)((Paragraph)layout.Sections[0]!.Headers.Primary.Elements[0]!).Elements[0]!).Content);
        Assert.IsFalse(layout.Styles[StyleNames.Normal]!.ParagraphFormat.KeepTogether);
    }

    [TestMethod]
    public async Task MissingImageBeforeAndAfterLayoutTriggersFailClosedFontGuard()
    {
        _ = await VisitReports.PrepareAsync(await CreateReport());
        foreach (bool late in new[] { false, true })
        {
            string path = Path.Combine(directory, $"missing-{late}.jpg");
            if (late) File.Copy(Path.Combine(directory, "before.jpg"), path);
            var document = new Document();
            document.Styles[StyleNames.Normal]!.Font.Name = ReportFonts.Family;
            document.AddSection().AddImage(path).Width = Unit.FromMillimeter(60);
            var renderer = new PdfDocumentRenderer { Document = document };
            renderer.PdfDocument.RenderEvents.RenderTextEvent += ReportFonts.RequireSupportedText;
            renderer.PrepareRenderPages();
            if (late) File.Delete(path);
            Assert.Throws<InvalidDataException>(() => renderer.RenderPages(1, renderer.PageCount));
        }
    }

    [TestMethod]
    [DataRow("title", "Название отчёта")]
    [DataRow("node", "Узел 01, название")]
    [DataRow("body", "Узел 01, наблюдение")]
    [DataRow("caption", "Узел 01, подпись фотографии")]
    [DataRow("nbsp", "Узел 01, наблюдение")]
    [DataRow("tab", "Узел 01, наблюдение")]
    public async Task UnbreakableTextCannotBecomeAnOffPageSuccessfulPdf(string field, string location)
    {
        VisitProject project = await CreateReport();
        Observation node = project.Document.Observations[0];
        VisitDetails details = project.Document.Details;
        switch (field)
        {
            case "title": details = details with { Title = new string('Ж', 4000) }; break;
            case "node": node = node with { Title = new string('Щ', 4000) }; break;
            case "body": node = node with { Finding = new string('Ы', 4000) }; break;
            case "caption": node = node with { Photos = [node.Photos[0] with { Caption = new string('Ш', 400) }, node.Photos[1]] }; break;
            case "nbsp": node = node with { Finding = string.Concat(Enumerable.Repeat("Слово\u00a0", 300)) }; break;
            case "tab": node = node with { Finding = "\t" + new string('Ы', 3999) }; break;
            default: throw new ArgumentException("Unknown test field.");
        }
        project = await VisitProjects.SaveAsync(project, project.Document with { Details = details, Observations = [node] });
        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() => VisitReports.PrepareAsync(project));
        StringAssert.Contains(failure.Message, location);
        StringAssert.Contains(failure.Message, "пробелом или переводом строки");
        Assert.AreEqual(0, Directory.GetFiles(project.DirectoryPath, "*.pdf", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    [DataRow('\n')]
    public async Task UnsplitCaptionTableCannotExtendBelowPrintableArea(char separator)
    {
        VisitProject project = await CreateReport();
        Observation node = project.Document.Observations[0];
        node = node with { Photos = [node.Photos[0] with
        {
            Caption = string.Concat(Enumerable.Repeat("А" + separator, 199)) + "А"
        }, node.Photos[1]] };
        project = await VisitProjects.SaveAsync(project, project.Document with { Observations = [node] });
        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() => VisitReports.PrepareAsync(project));
        StringAssert.Contains(failure.Message, "Узел 01, подпись фотографии");
        StringAssert.Contains(failure.Message, "границы печати");
        Assert.AreEqual(0, Directory.GetFiles(project.DirectoryPath, "*.pdf", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task FittingCaptionTabsArePaginatedWithoutAnArbitraryWhitespaceLimit()
    {
        VisitProject project = await CreateReport();
        Observation node = project.Document.Observations[0];
        node = node with { Photos = [node.Photos[0] with
        {
            Caption = string.Concat(Enumerable.Repeat("А\t", 199)) + "А"
        }, node.Photos[1]] };
        project = await VisitProjects.SaveAsync(project, project.Document with { Observations = [node] });
        ReportDraft draft = await VisitReports.PrepareAsync(project);
        Assert.IsTrue(File.Exists(draft.Path));
    }

    [TestMethod]
    public async Task TextWidthUsesActualFontMetricsInsteadOfCharacterLimit()
    {
        VisitProject project = await CreateReport();
        Observation node = project.Document.Observations[0];
        node = node with { Photos = [node.Photos[0] with { Caption = new string('i', 100) }, node.Photos[1]] };
        project = await VisitProjects.SaveAsync(project, project.Document with { Observations = [node] });
        ReportDraft draft = await VisitReports.PrepareAsync(project);
        Assert.IsTrue(File.Exists(draft.Path));
        node = node with { Photos = [node.Photos[0] with { Caption = new string('Ж', 100) }, node.Photos[1]] };
        project = await VisitProjects.SaveAsync(project, project.Document with { Observations = [node] });
        await Assert.ThrowsAsync<InvalidDataException>(() => VisitReports.PrepareAsync(project));
    }

    [TestMethod]
    [DataRow(LogLevel.Warning, "Unexpected warning", false)]
    [DataRow(LogLevel.Error, "Unexpected error", false)]
    [DataRow(LogLevel.Error, "Font embedding option was already set to EmbedCompleteFontFile. Setting to TryComputeSubset is ignored. ", false)]
    [DataRow(LogLevel.Error, "Font embedding option was already set to EmbedCompleteFontFile. Setting to TryComputeSubset is ignored.", true)]
    public async Task OnlyExactExceptionFreePinnedFontPolicyDiagnosticIsPermitted(LogLevel level, string message, bool exception)
    {
        _ = await VisitReports.PrepareAsync(await CreateReport());
        ReportFonts.BeginRender();
        LogHost.Factory.CreateLogger("Visitplate.NegativeTest").Log(level, new EventId(47), message,
            exception ? new IOException("Synthetic diagnostic exception") : null, static (state, _) => state);
        Assert.Throws<InvalidDataException>(ReportFonts.RequireCleanRender);
        ReportFonts.BeginRender();
    }

    private async Task<VisitProject> CreateReport()
    {
        WriteFixture("before.jpg", 30);
        WriteFixture("after.jpg", 180);
        VisitProject project = await VisitProjects.CreateAsync(Path.Combine(directory, "project"),
            new VisitDetails("Проверка фотоотчёта", "Синтетический объект", new DateOnly(2026, 9, 5), "Тестовая программа"));
        ImportResult imported = await VisitPhotos.ImportAsync(project, [Path.Combine(directory, "before.jpg"), Path.Combine(directory, "after.jpg")]);
        Assert.IsFalse(imported.Issues.Any(issue => issue.Severity == VisitIssueSeverity.Error), string.Join("; ", imported.Issues));
        project = imported.Project;
        var observation = new Observation(Guid.NewGuid(), "Тестовый узел", "Ёлка, съёмка, № 7, угол 90°, площадь 2 м².",
            "Проверена программа, не реальная работа.", "", ObservationStatus.Recorded,
            [new PhotoUse(project.Document.Assets[0].Id, PhotoRole.Before, "Синтетический кадр A"),
             new PhotoUse(project.Document.Assets[1].Id, PhotoRole.After, "Синтетический кадр B")]);
        return await VisitProjects.SaveAsync(project, project.Document with { Observations = [observation] });
    }

    private void WriteFixture(string name, byte value)
    {
        byte[] pixels = Enumerable.Repeat(value, 400 * 300 * 3).ToArray();
        BitmapSource bitmap = BitmapSource.Create(400, 300, 96, 96, PixelFormats.Bgr24, null, pixels, 1200);
        var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(Path.Combine(directory, name), FileMode.CreateNew);
        encoder.Save(stream);
    }

    private static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private sealed class DirectProgress(Action<VisitProgress> update) : IProgress<VisitProgress>
    {
        public void Report(VisitProgress value) => update(value);
    }
}
