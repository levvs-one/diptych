using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Visitplate.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PhotoTests
{
    private string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        string root = Environment.GetEnvironmentVariable("VISITPLATE_TEST_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "Visitplate.Tests");
        _directory = Path.Combine(root, nameof(PhotoTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestMethod]
    public async Task ImportPreservesSourceBytesTimesAttributesAndCommitsRevision()
    {
        string source = WriteJpeg("synthetic-camera.jpg", 80, 40, 6, privateMetadata: true);
        byte[] originalBytes = File.ReadAllBytes(source);
        DateTime modified = File.GetLastWriteTimeUtc(source);
        FileAttributes attributes = File.GetAttributes(source);
        VisitProject project = await CreateProject();
        ImportResult result = await VisitPhotos.ImportAsync(project, [source]);

        Assert.AreEqual(0, result.Issues.Length, IssueText(result));
        Assert.AreEqual(project.Document.Revision + 1, result.Project.Document.Revision);
        PhotoAsset asset = result.Project.Document.Assets.Single();
        Assert.AreEqual(80, asset.PixelWidth);
        Assert.AreEqual(40, asset.PixelHeight);
        Assert.AreEqual(6, asset.ExifOrientation);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(originalBytes)), asset.Sha256);
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(source));
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(VisitPaths.AssetPath(result.Project, asset)));
        Assert.AreEqual(modified, File.GetLastWriteTimeUtc(source));
        Assert.AreEqual(attributes, File.GetAttributes(source));
        VisitProject reopened = await VisitProjects.OpenAsync(result.Project.DirectoryPath);
        Assert.AreEqual(asset, reopened.Document.Assets.Single());
    }

    [TestMethod]
    public async Task AllEightExifOrientationsAndManualTurnsPreserveCornerIdentity()
    {
        // Synthetic camera chart: red/green top, blue/yellow bottom, no user photographs.
        int[][] corners =
        [
            [0, 1, 2, 3], [1, 0, 3, 2], [3, 2, 1, 0], [2, 3, 0, 1],
            [0, 2, 1, 3], [2, 0, 3, 1], [3, 1, 2, 0], [1, 3, 0, 2]
        ];
        VisitProject project = await CreateProject();
        for (int orientation = 1; orientation <= 8; orientation++)
        {
            string source = WriteJpeg($"synthetic-orientation-{orientation}.jpg", 80, 40, orientation);
            ImportResult imported = await VisitPhotos.ImportAsync(project, [source]);
            Assert.IsFalse(imported.Issues.Any(issue => issue.Severity == VisitIssueSeverity.Error), IssueText(imported));
            project = imported.Project;
            PhotoAsset asset = project.Document.Assets[^1];
            int[] expected = corners[orientation - 1];
            for (int turns = 0; turns < 4; turns++)
            {
                BitmapSource preview = await VisitPhotos.LoadPreviewAsync(project, asset.Id, turns);
                Assert.IsTrue(preview.IsFrozen);
                bool swapped = (orientation >= 5) != (turns % 2 == 1);
                Assert.AreEqual(swapped ? 40 : 80, preview.PixelWidth, $"orientation={orientation}, turns={turns}");
                Assert.AreEqual(swapped ? 80 : 40, preview.PixelHeight);
                CollectionAssert.AreEqual(expected, ReadCornerIds(preview), $"orientation={orientation}, turns={turns}");
                expected = [expected[2], expected[0], expected[3], expected[1]];
            }
        }
    }

    [TestMethod]
    public async Task PreviewAndDerivativeAreBoundedWithoutUpscaling()
    {
        string source = WriteJpeg("synthetic-wide.jpg", 4000, 2000, 6);
        VisitProject project = (await VisitPhotos.ImportAsync(await CreateProject(), [source])).Project;
        PhotoAsset asset = project.Document.Assets.Single();
        BitmapSource thumbnail = await VisitPhotos.LoadPreviewAsync(project, asset.Id, 0, 320);
        Assert.AreEqual(160, thumbnail.PixelWidth);
        Assert.AreEqual(320, thumbnail.PixelHeight);
        BitmapSource selected = await VisitPhotos.LoadPreviewAsync(project, asset.Id, 1);
        Assert.AreEqual(800, selected.PixelWidth);
        Assert.AreEqual(400, selected.PixelHeight);
        var use = new PhotoUse(asset.Id, PhotoRole.Overview, "Тест", 0);
        PreparedPhoto prepared = await VisitPhotos.PrepareImageAsync(project, use, DerivativePath(project, use));
        Assert.AreEqual(1024, prepared.PixelWidth);
        Assert.AreEqual(2048, prepared.PixelHeight);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(prepared.Path))), prepared.Sha256);
        Assert.AreEqual(new FileInfo(prepared.Path).Length, prepared.Length);
    }

    [TestMethod]
    public async Task TransparentPngIsCompositedOnWhiteInPreviewAndJpeg()
    {
        string source = Path.Combine(_directory, "synthetic-alpha.png");
        var pixels = new byte[40 * 20 * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index + 2] = 255;
            pixels[index + 3] = (index / 4) % 40 < 20 ? (byte)0 : (byte)128;
        }
        SavePng(source, BitmapSource.Create(40, 20, 96, 96, PixelFormats.Bgra32, null, pixels, 160));
        ImportResult imported = await VisitPhotos.ImportAsync(await CreateProject(), [source]);
        Assert.IsFalse(imported.Issues.Any(issue => issue.Severity == VisitIssueSeverity.Error), IssueText(imported));
        Assert.IsTrue(imported.Issues.Any(issue => issue.Code == "PngOrientationReview"));
        PhotoAsset asset = imported.Project.Document.Assets.Single();
        BitmapSource preview = await VisitPhotos.LoadPreviewAsync(imported.Project, asset.Id, 0);
        CollectionAssert.AreEqual(new byte[] { 255, 255, 255 }, ReadBgr(preview, 5, 10));
        CollectionAssert.AreEqual(new byte[] { 127, 127, 255 }, ReadBgr(preview, 30, 10));
        var use = new PhotoUse(asset.Id, PhotoRole.Overview, "Прозрачность");
        PreparedPhoto prepared = await VisitPhotos.PrepareImageAsync(imported.Project, use, DerivativePath(imported.Project, use));
        using var stream = File.OpenRead(prepared.Path);
        BitmapFrame frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.IsTrue(ReadBgr(frame, 5, 10).All(value => value >= 250));
        byte[] pink = ReadBgr(frame, 30, 10);
        Assert.IsTrue(pink[0] is >= 120 and <= 135 && pink[1] is >= 120 and <= 135 && pink[2] >= 245);
    }

    [TestMethod]
    public async Task EmbeddedSrgbProfileIsConvertedWithoutChangingCornerColours()
    {
        string source = Path.Combine(_directory, "synthetic-profile.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Chart(80, 40), null, null,
            new System.Collections.ObjectModel.ReadOnlyCollection<ColorContext>([new ColorContext(PixelFormats.Bgra32)])));
        using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write))
            encoder.Save(stream);
        using (var stream = File.OpenRead(source))
        {
            BitmapFrame frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];
            Assert.AreEqual(1, frame.ColorContexts.Count, "Fixture must contain an actual colour context.");
        }
        ImportResult imported = await VisitPhotos.ImportAsync(await CreateProject(), [source]);
        Assert.IsFalse(imported.Issues.Any(issue => issue.Severity == VisitIssueSeverity.Error), IssueText(imported));
        BitmapSource preview = await VisitPhotos.LoadPreviewAsync(imported.Project,
            imported.Project.Document.Assets.Single().Id, 0);
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, ReadCornerIds(preview));
    }

    [TestMethod]
    public async Task UnprofiledCmykJpegIsRejectedWithAnExplicitColourReason()
    {
        string source = Path.Combine(_directory, "synthetic-cmyk.jpg");
        BitmapSource pixels = BitmapSource.Create(80, 40, 96, 96, PixelFormats.Cmyk32, null,
            new byte[80 * 40 * 4], 80 * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
        encoder.Frames.Add(BitmapFrame.Create(pixels, null, null, null));
        using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write))
            encoder.Save(stream);
        ImportResult result = await VisitPhotos.ImportAsync(await CreateProject(), [source]);
        Assert.AreEqual(0, result.Project.Document.Assets.Length);
        Assert.AreEqual("synthetic-cmyk.jpg", result.Issues.Single().FileName);
        StringAssert.Contains(result.Issues.Single().Message, "Цветовой формат");
    }

    [TestMethod]
    public async Task PngWithUnmanagedGammaIsRejectedInsteadOfChangingColoursSilently()
    {
        string source = Path.Combine(_directory, "synthetic-linear-gamma.png");
        var metadata = new BitmapMetadata("png");
        metadata.SetQuery("/gAMA/ImageGamma", 100000U);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Chart(80, 40), null, metadata, null));
        using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write))
            encoder.Save(stream);
        using (var stream = File.OpenRead(source))
        {
            BitmapFrame frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];
            Assert.AreEqual(100000U, ((BitmapMetadata)frame.Metadata).GetQuery("/gAMA/ImageGamma"));
            Assert.IsTrue(frame.ColorContexts is null || frame.ColorContexts.Count == 0);
        }
        ImportResult result = await VisitPhotos.ImportAsync(await CreateProject(), [source]);
        Assert.AreEqual(0, result.Project.Document.Assets.Length);
        Assert.AreEqual("synthetic-linear-gamma.png", result.Issues.Single().FileName);
        StringAssert.Contains(result.Issues.Single().Message, "gamma");
    }

    [TestMethod]
    public async Task TwoHundredAssetBudgetHasNoSilentTruncation()
    {
        string source = WriteJpeg("synthetic-repeated.jpg", 32, 16);
        ImmutableArray<string> sources = Enumerable.Repeat(source, 201).ToImmutableArray();
        ImportResult result = await VisitPhotos.ImportAsync(await CreateProject(), sources);
        Assert.AreEqual(200, result.Project.Document.Assets.Length, IssueText(result));
        Assert.AreEqual(200, result.Project.Document.Assets.Select(asset => asset.Id).Distinct().Count());
        Assert.AreEqual(1, result.Issues.Length);
        StringAssert.Contains(result.Issues.Single().Message, "200 фотографий");
        Assert.AreEqual("synthetic-repeated.jpg", result.Issues.Single().FileName);
    }

    [TestMethod]
    public async Task DerivativeRemovesCameraMetadataAndThumbnailButDoesNotModifyOriginal()
    {
        string source = WriteJpeg("synthetic-private-metadata.jpg", 80, 40, 8, privateMetadata: true);
        byte[] bytes = File.ReadAllBytes(source);
        VisitProject project = (await VisitPhotos.ImportAsync(await CreateProject(), [source])).Project;
        PhotoAsset asset = project.Document.Assets.Single();
        using (var original = File.OpenRead(source))
        {
            BitmapFrame frame = BitmapDecoder.Create(original, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            var metadata = (BitmapMetadata)frame.Metadata;
            Assert.AreEqual("SYNTHETIC-SERIAL", metadata.GetQuery("/app1/ifd/exif/{ushort=42033}"));
            Assert.AreEqual("N", metadata.GetQuery("/app1/ifd/gps/{ushort=1}"));
            Assert.IsNotNull(frame.Thumbnail);
        }
        var use = new PhotoUse(asset.Id, PhotoRole.After, "Синтетический снимок", 1);
        PreparedPhoto prepared = await VisitPhotos.PrepareImageAsync(project, use, DerivativePath(project, use));
        using (var derivative = File.OpenRead(prepared.Path))
        {
            BitmapFrame frame = BitmapDecoder.Create(derivative, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            var metadata = frame.Metadata as BitmapMetadata;
            Assert.IsNull(metadata?.GetQuery("/app1/ifd/gps"));
            Assert.IsNull(metadata?.GetQuery("/app1/ifd/exif/{ushort=42033}"));
            Assert.IsNull(metadata?.GetQuery("/app1/ifd/exif/{ushort=36867}"));
            Assert.IsNull(metadata?.GetQuery("/app1/ifd/{ushort=274}"));
            Assert.IsNull(metadata?.GetQuery("/xmp"));
            Assert.IsNull(frame.Thumbnail);
        }
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(VisitPaths.AssetPath(project, asset)));
    }

    [TestMethod]
    public async Task InvalidFilesHaveNamedReasonsAndDoNotHideValidImport()
    {
        string empty = Path.Combine(_directory, "empty.jpg");
        File.WriteAllBytes(empty, []);
        string malformed = Path.Combine(_directory, "broken.png");
        File.WriteAllBytes(malformed, [1, 2, 3, 4, 5]);
        string wrongFormat = Path.Combine(_directory, "png-named-jpeg.jpg");
        SavePng(wrongFormat, Chart(80, 40));
        string invalidExif = WriteJpeg("invalid-orientation.jpg", 80, 40, 9);
        string valid = WriteJpeg("valid.jpg", 80, 40);
        ImportResult result = await VisitPhotos.ImportAsync(await CreateProject(), [empty, malformed, wrongFormat, invalidExif, valid]);
        Assert.AreEqual(1, result.Project.Document.Assets.Length, IssueText(result));
        VisitIssue[] errors = result.Issues.Where(issue => issue.Severity == VisitIssueSeverity.Error).ToArray();
        Assert.AreEqual(4, errors.Length);
        CollectionAssert.AreEquivalent(new[] { "empty.jpg", "broken.png", "png-named-jpeg.jpg", "invalid-orientation.jpg" },
            errors.Select(issue => issue.FileName).ToArray());
        Assert.IsTrue(errors.All(issue => !string.IsNullOrWhiteSpace(issue.Message)));
    }

    [TestMethod]
    public async Task FileByteAndPixelLimitsRejectBeforeFullDecode()
    {
        string largeFile = Path.Combine(_directory, "oversized.jpg");
        using (var file = new FileStream(largeFile, FileMode.CreateNew, FileAccess.Write))
            file.SetLength(VisitPhotos.MaximumFileBytes + 1);
        string wide = Path.Combine(_directory, "over-wide.png");
        SavePng(wide, Mono(30001, 1));
        string megapixels = Path.Combine(_directory, "over-100mp.png");
        SavePng(megapixels, Mono(10001, 10000));
        ImportResult result = await VisitPhotos.ImportAsync(await CreateProject(), [largeFile, wide, megapixels]);
        Assert.AreEqual(0, result.Project.Document.Assets.Length, IssueText(result));
        Assert.AreEqual(3, result.Issues.Count(issue => issue.Severity == VisitIssueSeverity.Error));
        Assert.IsTrue(result.Issues.Single(issue => issue.FileName == "oversized.jpg").Message.Contains("50 МиБ", StringComparison.Ordinal));
        Assert.IsTrue(result.Issues.Single(issue => issue.FileName == "over-100mp.png").Message.Contains("100 мегапикселей", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ModifiedOriginalAndForgedDimensionsAreRejectedBeforePreview()
    {
        string source = WriteJpeg("synthetic-integrity.jpg", 80, 40);
        VisitProject project = (await VisitPhotos.ImportAsync(await CreateProject(), [source])).Project;
        PhotoAsset asset = project.Document.Assets.Single();
        var forged = new VisitProject(project.DirectoryPath,
            project.Document with { Assets = [asset with { PixelWidth = 79 }] }, project.DocumentFingerprint);
        await Assert.ThrowsAsync<InvalidDataException>(() => VisitPhotos.LoadPreviewAsync(forged, asset.Id, 0));
        byte[] bytes = File.ReadAllBytes(VisitPaths.AssetPath(project, asset));
        bytes[^1] ^= 1;
        File.WriteAllBytes(VisitPaths.AssetPath(project, asset), bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => VisitPhotos.LoadPreviewAsync(project, asset.Id, 0));
    }

    [TestMethod]
    public async Task DerivativeNeverOverwritesAndRejectsPathsOutsideIssuedGrammar()
    {
        string source = WriteJpeg("synthetic-output.jpg", 80, 40);
        VisitProject project = (await VisitPhotos.ImportAsync(await CreateProject(), [source])).Project;
        var use = new PhotoUse(project.Document.Assets.Single().Id, PhotoRole.Overview, "");
        string destination = DerivativePath(project, use);
        byte[] sentinel = [12, 34, 56];
        File.WriteAllBytes(destination, sentinel);
        await Assert.ThrowsAsync<IOException>(() => VisitPhotos.PrepareImageAsync(project, use, destination));
        CollectionAssert.AreEqual(sentinel, File.ReadAllBytes(destination));
        await Assert.ThrowsAsync<ArgumentException>(() => VisitPhotos.PrepareImageAsync(project, use, source));
        await Assert.ThrowsAsync<ArgumentException>(() => VisitPhotos.PrepareImageAsync(project, use,
            Path.Combine(Path.GetDirectoryName(destination)!, "incorrect.jpg")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => VisitPhotos.LoadPreviewAsync(project, use.AssetId, 4));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => VisitPhotos.LoadPreviewAsync(project, use.AssetId, 0, 801));
    }

    [TestMethod]
    public async Task CancellationCannotRegisterOrPublishIncompletePhotos()
    {
        string source = WriteJpeg("synthetic-cancel.jpg", 80, 40);
        VisitProject project = await CreateProject();
        using var cancellation = new CancellationTokenSource();
        var progress = new SynchronousProgress(value =>
        {
            if (value.Phase == VisitPhase.Normalizing)
                cancellation.Cancel();
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            VisitPhotos.ImportAsync(project, [source], progress, cancellation.Token));
        Assert.AreEqual(0, (await VisitProjects.OpenAsync(project.DirectoryPath)).Document.Assets.Length);
        VisitProject imported = (await VisitPhotos.ImportAsync(project, [source])).Project;
        var use = new PhotoUse(imported.Document.Assets.Single().Id, PhotoRole.Overview, "");
        string destination = DerivativePath(imported, use);
        await Assert.ThrowsAsync<OperationCanceledException>(() => VisitPhotos.PrepareImageAsync(imported, use, destination,
            cancellation.Token));
        Assert.IsFalse(File.Exists(destination));
        await Assert.ThrowsAsync<OperationCanceledException>(() => VisitPhotos.LoadPreviewAsync(imported, use.AssetId, 0,
            cancellationToken: cancellation.Token));
    }

    [TestMethod]
    public async Task StaleRegistrationReportsConflictAndKeepsRecoverableOriginals()
    {
        string first = WriteJpeg("first.jpg", 80, 40);
        string second = WriteJpeg("second.jpg", 80, 40);
        VisitProject initial = await CreateProject();
        VisitProject current = (await VisitPhotos.ImportAsync(initial, [first])).Project;
        IOException exception = await Assert.ThrowsAsync<IOException>(() => VisitPhotos.ImportAsync(initial, [second]));
        StringAssert.Contains(exception.Message, "не зарегистрированы");
        Assert.AreEqual(1, (await VisitProjects.OpenAsync(initial.DirectoryPath)).Document.Assets.Length);
        Assert.AreEqual(2, Directory.GetFiles(Path.Combine(current.DirectoryPath, "originals"), "*.jpg").Length);
    }

    [TestMethod]
    public async Task LocalPathPolicyRejectsNetworkGrammarBeforeAccess()
    {
        ImportResult result = await VisitPhotos.ImportAsync(await CreateProject(),
            [@"\\not-a-real-host.invalid\share\photo.jpg", @"\\?\C:\photo.jpg", "relative.jpg"]);
        Assert.AreEqual(3, result.Issues.Length);
        Assert.AreEqual(0, result.Project.Document.Assets.Length);
        Assert.IsTrue(result.Issues.All(issue => issue.Message.Contains("локальном диске", StringComparison.Ordinal)));
    }

    private Task<VisitProject> CreateProject() => VisitProjects.CreateAsync(Path.Combine(_directory, "project"),
        new VisitDetails("Синтетический тест", "Тестовая схема", new DateOnly(2026, 9, 5), "Тест"));

    private static string IssueText(ImportResult result) => string.Join(Environment.NewLine,
        result.Issues.Select(issue => $"{issue.FileName}: {issue.Message}"));

    private static string DerivativePath(VisitProject project, PhotoUse use)
    {
        string directory = Path.Combine(project.DirectoryPath, ".visitplate-drafts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{use.AssetId:N}-{use.ManualQuarterTurns}.jpg");
    }

    private string WriteJpeg(string name, int width, int height, int? orientation = null, bool privateMetadata = false)
    {
        string path = Path.Combine(_directory, name);
        var metadata = new BitmapMetadata("jpg");
        if (orientation.HasValue)
            metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)orientation.Value);
        if (privateMetadata)
        {
            metadata.SetQuery("/app1/ifd/exif/{ushort=42033}", "SYNTHETIC-SERIAL");
            metadata.SetQuery("/app1/ifd/exif/{ushort=36867}", "2026:01:02 03:04:05");
            metadata.SetQuery("/app1/ifd/gps/{ushort=1}", "N");
            metadata.SetQuery("/app1/ifd/gps/{ushort=2}", new ulong[] { (1UL << 32) | 55, (1UL << 32) | 45, 1UL << 32 });
            metadata.SetQuery("/xmp/dc:description", "Synthetic private fixture");
        }
        var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
        encoder.Frames.Add(BitmapFrame.Create(Chart(width, height), privateMetadata ? Chart(16, 8) : null, metadata, null));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        encoder.Save(stream);
        return path;
    }

    private static BitmapSource Chart(int width, int height)
    {
        var data = new byte[width * height * 3];
        byte[][] colors = [[0, 0, 255], [0, 255, 0], [255, 0, 0], [0, 255, 255]];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int index = (y >= height / 2 ? 2 : 0) + (x >= width / 2 ? 1 : 0);
                colors[index].CopyTo(data, (y * width + x) * 3);
            }
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, data, width * 3);
    }

    private static BitmapSource Mono(int width, int height)
    {
        int stride = (width + 7) / 8;
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Indexed1,
            new BitmapPalette([Colors.Black, Colors.White]), new byte[stride * height], stride);
    }

    private static void SavePng(string path, BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source, null, null, null));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        encoder.Save(stream);
    }

    private static byte[] ReadBgr(BitmapSource image, int x, int y)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        var bytes = new byte[3];
        converted.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), bytes, 3, 0);
        return bytes;
    }

    private static int[] ReadCornerIds(BitmapSource image)
    {
        (int X, int Y)[] corners = [(5, 5), (image.PixelWidth - 6, 5), (5, image.PixelHeight - 6),
            (image.PixelWidth - 6, image.PixelHeight - 6)];
        return corners.Select(point =>
        {
            byte[] pixel = ReadBgr(image, point.X, point.Y);
            if (pixel[2] > 200 && pixel[1] > 200 && pixel[0] < 40) return 3;
            if (pixel[2] > 200 && pixel[1] < 40 && pixel[0] < 40) return 0;
            if (pixel[1] > 200 && pixel[2] < 40 && pixel[0] < 40) return 1;
            if (pixel[0] > 200 && pixel[1] < 40 && pixel[2] < 40) return 2;
            throw new AssertFailedException($"Неожиданный цвет угла: {string.Join(',', pixel)}");
        }).ToArray();
    }

    private sealed class SynchronousProgress(Action<VisitProgress> report) : IProgress<VisitProgress>
    {
        public void Report(VisitProgress value) => report(value);
    }
}
