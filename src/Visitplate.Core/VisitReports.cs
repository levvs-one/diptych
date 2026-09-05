using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using MigraDoc.Rendering;
using PdfSharp.Pdf.IO;

namespace Visitplate.Core;

public static class VisitReports
{
    internal const int MaximumPages = 150;
    internal const long MaximumPdfBytes = 128L * 1024 * 1024;
    internal const long MaximumDerivativeBytes = 96L * 1024 * 1024;
    private static readonly SemaphoreSlim RendererGate = new(1, 1);

    public static ImmutableArray<VisitIssue> Validate(VisitDocument document)
    {
        var issues = ImmutableArray.CreateBuilder<VisitIssue>();
        try { VisitProjects.Validate(document); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            issues.Add(new VisitIssue(VisitIssueSeverity.Error, "InvalidDocument", exception.Message));
            return issues.ToImmutable();
        }
        if (string.IsNullOrWhiteSpace(document.Details.Title))
            issues.Add(new VisitIssue(VisitIssueSeverity.Error, "TitleRequired", "Укажите название отчёта."));
        if (string.IsNullOrWhiteSpace(document.Details.Site))
            issues.Add(new VisitIssue(VisitIssueSeverity.Error, "SiteRequired", "Укажите объект выезда."));
        if (string.IsNullOrWhiteSpace(document.Details.Author))
            issues.Add(new VisitIssue(VisitIssueSeverity.Error, "AuthorRequired", "Укажите исполнителя."));
        if (document.Observations.IsEmpty)
            issues.Add(new VisitIssue(VisitIssueSeverity.Error, "ObservationRequired", "Добавьте хотя бы один узел."));
        if (!document.Observations.Any(observation => !observation.Photos.IsEmpty))
            issues.Add(new VisitIssue(VisitIssueSeverity.Error, "PhotoRequired", "Назначьте хотя бы одну фотографию узлу."));
        foreach (Observation observation in document.Observations)
        {
            if (string.IsNullOrWhiteSpace(observation.Title))
                issues.Add(new VisitIssue(VisitIssueSeverity.Error, "ObservationTitleRequired",
                    "Укажите название узла.", observation.Id));
            if (observation.Photos.Any(photo => photo.Role == PhotoRole.Before)
                && !observation.Photos.Any(photo => photo.Role == PhotoRole.After))
                issues.Add(new VisitIssue(VisitIssueSeverity.Warning, "AfterPhotoMissing",
                    "В узле есть снимок до работ, но нет снимка после. Отчёт покажет только назначенную фотографию.", observation.Id));
        }
        return issues.ToImmutable();
    }

    public static Task<ReportDraft> PrepareAsync(VisitProject project, IProgress<VisitProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using FileStream projectLock = VisitProjects.AcquireLock(project.DirectoryPath);
            await VisitProjects.RequireCurrentAsync(project, cancellationToken).ConfigureAwait(false);
            ImmutableArray<VisitIssue> issues = Validate(project.Document);
            if (issues.Any(issue => issue.Severity == VisitIssueSeverity.Error))
                throw new InvalidDataException(string.Join(" ", issues.Where(issue => issue.Severity == VisitIssueSeverity.Error)
                    .Select(issue => issue.Message)));
            string draftRoot = VisitPaths.RequireLocalPath(Path.Combine(project.DirectoryPath, ".visitplate-drafts"));
            Directory.CreateDirectory(draftRoot);
            string directory = VisitPaths.RequireLocalPath(Path.Combine(draftRoot, Guid.NewGuid().ToString("N")));
            if (Path.Exists(directory)) throw new IOException("Папка черновика уже существует.");
            Directory.CreateDirectory(directory);
            string partial = Path.Combine(directory, "report.pdf.partial");
            var lockedPhotos = new List<FileStream>();
            try
            {
                PhotoUse[] uses = project.Document.Observations.SelectMany(observation => observation.Photos)
                    .DistinctBy(use => (use.AssetId, use.ManualQuarterTurns)).ToArray();
                var photos = new Dictionary<(Guid AssetId, int Turns), PreparedPhoto>();
                long bytes = 0;
                for (int index = 0; index < uses.Length; index++)
                {
                    PhotoUse use = uses[index];
                    progress?.Report(new VisitProgress(VisitPhase.Normalizing, index, uses.Length));
                    cancellationToken.ThrowIfCancellationRequested();
                    string path = Path.Combine(directory, $"{use.AssetId:N}-{use.ManualQuarterTurns}.jpg");
                    PreparedPhoto photo = await VisitPhotos.PrepareImageAsync(project, use, path, cancellationToken).ConfigureAwait(false);
                    bytes = checked(bytes + photo.Length);
                    if (bytes > MaximumDerivativeBytes)
                        throw new InvalidDataException("Производные фотографии превышают лимит 96 МиБ. Разделите отчёт.");
                    FileStream stream = OpenRead(photo.Path);
                    lockedPhotos.Add(stream);
                    if (stream.Length != photo.Length || await HashAsync(stream, cancellationToken).ConfigureAwait(false) != photo.Sha256)
                        throw new InvalidDataException("Производная фотография изменилась до рисования PDF.");
                    photos.Add((use.AssetId, use.ManualQuarterTurns), photo);
                }

                progress?.Report(new VisitProgress(VisitPhase.Normalizing, uses.Length, uses.Length));
                await RendererGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                int pageCount;
                try
                {
                    ReportFonts.Initialize();
                    ReportFonts.BeginRender();
                    ReportLayout.RequireTextFits(project.Document);
                    var renderer = new PdfDocumentRenderer { Document = ReportLayout.Create(project.Document, photos) };
                    using var outputDocument = renderer.PdfDocument;
                    outputDocument.RenderEvents.RenderTextEvent += ReportFonts.RequireSupportedText;
                    progress?.Report(new VisitProgress(VisitPhase.Paginating, 0));
                    cancellationToken.ThrowIfCancellationRequested();
                    renderer.PrepareRenderPages();
                    cancellationToken.ThrowIfCancellationRequested();
                    pageCount = renderer.PageCount;
                    if (pageCount is < 1 or > MaximumPages)
                        throw new InvalidDataException("Отчёт должен содержать от 1 до 150 страниц. Разделите отчёт.");
                    ReportLayout.RequirePageBounds(renderer);
                    for (int page = 1; page <= pageCount; page++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        renderer.RenderPages(page, page);
                        progress?.Report(new VisitProgress(VisitPhase.Paginating, page, pageCount));
                    }
                    ReportFonts.RequireCleanRender();
                    cancellationToken.ThrowIfCancellationRequested();
                    using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        outputDocument.Save(output, false);
                        output.Flush(flushToDisk: true);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (new FileInfo(partial).Length > MaximumPdfBytes)
                        throw new InvalidDataException("PDF превышает лимит 128 МиБ. Разделите отчёт.");
                    using var check = PdfReader.Open(partial, PdfDocumentOpenMode.Import);
                    if (check.PageCount != pageCount)
                        throw new InvalidDataException("Проверка записанного PDF не подтвердила число страниц.");
                }
                finally { RendererGate.Release(); }

                progress?.Report(new VisitProgress(VisitPhase.Verifying, 0, 1));
                cancellationToken.ThrowIfCancellationRequested();
                await VerifyOriginalsAsync(project, cancellationToken).ConfigureAwait(false);
                await VisitProjects.RequireCurrentAsync(project, cancellationToken).ConfigureAwait(false);
                string digest;
                using (FileStream input = OpenRead(partial))
                    digest = await HashAsync(input, cancellationToken).ConfigureAwait(false);
                string draftPath = VisitPaths.RequireLocalPath(Path.Combine(directory, "report.pdf"));
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(partial, draftPath, overwrite: false);
                progress?.Report(new VisitProgress(VisitPhase.Verifying, 1, 1));
                cancellationToken.ThrowIfCancellationRequested();
                return new ReportDraft(draftPath, project.Document.Id, project.Document.Revision,
                    project.DocumentFingerprint, digest, pageCount,
                    project.Document.Observations.Sum(observation => observation.Photos.Length), issues);
            }
            catch (Exception exception)
            {
                exception.Data["DraftDirectory"] = directory;
                throw;
            }
            finally
            {
                foreach (FileStream photo in lockedPhotos) photo.Dispose();
            }
        }, cancellationToken);
    }

    public static async Task<string> PublishAsync(VisitProject project, ReportDraft draft, string newPdfPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(draft);
        RequireDraftIdentity(project, draft);
        string source = RequireDraftPath(project, draft.Path);
        string destination = VisitPaths.RequireLocalPath(newPdfPath);
        if (!Path.GetExtension(destination).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || VisitPaths.IsWithin(project.DirectoryPath, destination))
            throw new ArgumentException("Сохраняйте PDF вне рабочей папки проекта с расширением .pdf.", nameof(newPdfPath));
        string parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Папка сохранения PDF не найдена.");
        if (Path.Exists(destination)) throw new IOException("Файл назначения уже существует. Выберите новое имя.");
        using (FileStream projectLock = VisitProjects.AcquireLock(project.DirectoryPath))
            await VisitProjects.RequireCurrentAsync(project, cancellationToken).ConfigureAwait(false);

        using FileStream input = OpenRead(source);
        if (input.Length is <= 0 or > MaximumPdfBytes || await HashAsync(input, cancellationToken).ConfigureAwait(false) != draft.Sha256)
            throw new InvalidDataException("Просмотренный PDF изменился. Подготовьте новый черновик.");
        input.Position = 0;
        string partial = VisitPaths.RequireLocalPath(Path.Combine(parent, $".visitplate-{Guid.NewGuid():N}.pdf.partial"));
        try
        {
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                output.Position = 0;
                if (output.Length != input.Length || await HashAsync(output, cancellationToken).ConfigureAwait(false) != draft.Sha256)
                    throw new IOException("Проверка копии PDF не подтвердила его содержимое.");
            }
            using FileStream projectLock = VisitProjects.AcquireLock(project.DirectoryPath);
            await VisitProjects.RequireCurrentAsync(project, cancellationToken).ConfigureAwait(false);
            await VerifyOriginalsAsync(project, cancellationToken).ConfigureAwait(false);
            RequireDraftIdentity(project, draft);
            VisitPaths.RequireLocalPath(source);
            VisitPaths.RequireLocalPath(destination);
            VisitPaths.RequireLocalPath(partial);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, destination, overwrite: false);
            return destination;
        }
        catch (Exception exception)
        {
            exception.Data["PartialPath"] = partial;
            throw;
        }
    }

    private static void RequireDraftIdentity(VisitProject project, ReportDraft draft)
    {
        if (draft.ProjectId != project.Document.Id || draft.Revision != project.Document.Revision
            || draft.DocumentFingerprint != project.DocumentFingerprint || draft.PageCount is < 1 or > MaximumPages
            || draft.Sha256 is not { Length: 64 })
            throw new InvalidDataException("Черновик относится к другому проекту или редакции. Подготовьте PDF заново.");
    }

    private static string RequireDraftPath(VisitProject project, string path)
    {
        string source = VisitPaths.RequireLocalPath(path);
        string root = VisitPaths.RequireLocalPath(Path.Combine(project.DirectoryPath, ".visitplate-drafts"));
        string? parent = Path.GetDirectoryName(source);
        if (parent is null || Path.GetFileName(source) != "report.pdf"
            || !string.Equals(Path.GetDirectoryName(parent), root, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(parent), "N", out Guid id) || id == Guid.Empty)
            throw new InvalidDataException("Черновик должен находиться в собственной папке подготовки Visitplate.");
        return source;
    }

    private static FileStream OpenRead(string path) => new(VisitPaths.RequireLocalPath(path), FileMode.Open,
        FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<string> HashAsync(Stream stream, CancellationToken cancellationToken) =>
        Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));

    private static async Task VerifyOriginalsAsync(VisitProject project, CancellationToken cancellationToken)
    {
        HashSet<Guid> used = project.Document.Observations.SelectMany(observation => observation.Photos)
            .Select(photo => photo.AssetId).ToHashSet();
        foreach (PhotoAsset asset in project.Document.Assets.Where(asset => used.Contains(asset.Id)))
        {
            using FileStream input = OpenRead(VisitPaths.AssetPath(project, asset));
            if (input.Length != asset.Length || await HashAsync(input, cancellationToken).ConfigureAwait(false) != asset.Sha256)
                throw new InvalidDataException($"Оригинал изменился: {asset.OriginalFileName}. PDF не опубликован.");
        }
    }
}
