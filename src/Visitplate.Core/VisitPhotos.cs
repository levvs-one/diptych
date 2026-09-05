using System.Collections.Immutable;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Visitplate.Core;

internal sealed record PreparedPhoto(Guid AssetId, string Path, int PixelWidth, int PixelHeight,
    long Length, string Sha256);

public static class VisitPhotos
{
    internal const long MaximumFileBytes = 50L * 1024 * 1024;
    internal const long MaximumProjectBytes = 2L * 1024 * 1024 * 1024;
    internal const int MaximumAssets = 200;
    private const int CopyBufferSize = 128 * 1024;
    private static readonly SemaphoreSlim ImagingGate = new(1, 1);

    public static Task<ImportResult> ImportAsync(VisitProject project, ImmutableArray<string> sourcePaths,
        IProgress<VisitProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (sourcePaths.IsDefault)
            throw new ArgumentException("Список фотографий не задан.", nameof(sourcePaths));

        return Task.Run(async () =>
        {
            var assets = ImmutableArray.CreateBuilder<PhotoAsset>();
            var issues = ImmutableArray.CreateBuilder<VisitIssue>();
            long totalBytes = project.Document.Assets.Sum(asset => asset.Length);
            string originals = VisitPaths.RequireLocalPath(Path.Combine(project.DirectoryPath, "originals"));
            if (!Directory.Exists(originals))
                throw new DirectoryNotFoundException("Папка оригиналов проекта отсутствует.");

            for (int index = 0; index < sourcePaths.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sourcePath = sourcePaths[index];
                string fileName = string.IsNullOrWhiteSpace(sourcePath) ? "(пустой путь)"
                    : string.IsNullOrEmpty(Path.GetFileName(sourcePath)) ? sourcePath : Path.GetFileName(sourcePath);
                string? partialPath = null;
                try
                {
                    if (project.Document.Assets.Length + assets.Count >= MaximumAssets)
                        throw new InvalidDataException("В проекте допускается не более 200 фотографий.");

                    string source = VisitPaths.RequireLocalPath(sourcePath);
                    if (!string.IsNullOrEmpty(Path.GetFileName(source)))
                        fileName = Path.GetFileName(source);
                    string extension = Path.GetExtension(source).ToLowerInvariant();
                    PhotoFormat format = extension switch
                    {
                        ".jpg" or ".jpeg" => PhotoFormat.Jpeg,
                        ".png" => PhotoFormat.Png,
                        _ => throw new InvalidDataException("Поддерживаются только файлы JPEG и PNG.")
                    };
                    var before = new FileInfo(source);
                    long length = before.Length;
                    DateTime modified = before.LastWriteTimeUtc;
                    FileAttributes attributes = before.Attributes;
                    ValidateLength(length);
                    if (length > MaximumProjectBytes - totalBytes)
                        throw new InvalidDataException("Общий размер оригиналов не может превышать 2 ГиБ.");

                    Guid id = Guid.NewGuid();
                    string finalPath = VisitPaths.RequireLocalPath(Path.Combine(originals,
                        $"{id:N}.{(format == PhotoFormat.Jpeg ? "jpg" : "png")}"));
                    partialPath = finalPath + ".partial";
                    progress?.Report(new VisitProgress(VisitPhase.Importing, index, sourcePaths.Length, fileName));
                    string digest;
                    await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                        CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                    {
                        var buffer = new byte[CopyBufferSize];
                        long copied = 0;
                        int count;
                        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                        {
                            copied += count;
                            if (copied > length || copied > MaximumFileBytes)
                                throw new IOException("Размер исходной фотографии изменился во время копирования.");
                            hash.AppendData(buffer, 0, count);
                            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        }
                        if (copied != length)
                            throw new IOException("Исходная фотография изменилась во время копирования.");
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                        digest = Convert.ToHexStringLower(hash.GetHashAndReset());
                        before.Refresh();
                        if (before.Length != length || before.LastWriteTimeUtc != modified || before.Attributes != attributes)
                            throw new IOException("Исходная фотография изменилась во время копирования.");
                    }

                    progress?.Report(new VisitProgress(VisitPhase.Normalizing, index, sourcePaths.Length, fileName));
                    PhotoAsset accepted;
                    await ImagingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        using var copy = OpenVerifiedOriginal(partialPath, length, digest, cancellationToken);
                        ImageHeader header = ReadHeader(copy, format);
                        // A bounded decode rejects broken pixel data and unsupported colour before registration.
                        _ = Normalize(copy, header, 0, 320, cancellationToken);
                        accepted = new PhotoAsset(id, format, fileName, length, digest,
                            header.Width, header.Height, header.Orientation);
                    }
                    finally { ImagingGate.Release(); }
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(partialPath, finalPath, overwrite: false);
                    partialPath = null;
                    assets.Add(accepted);
                    totalBytes += length;
                    if (format == PhotoFormat.Png)
                        issues.Add(new VisitIssue(VisitIssueSeverity.Warning, "PngOrientationReview",
                            "Проверьте ориентацию PNG: варианты метаданных eXIf могут не поддерживаться Windows. "
                            + "При необходимости задайте ручной поворот.", AssetId: id, FileName: fileName));
                }
                catch (Exception exception) when (IsImageOrFileError(exception))
                {
                    string retained = partialPath is not null && File.Exists(partialPath)
                        ? " Неполная копия .partial оставлена в папке originals; она не зарегистрирована." : string.Empty;
                    issues.Add(new VisitIssue(VisitIssueSeverity.Error, "PhotoImportRejected",
                        exception.Message + retained, FileName: fileName));
                }
                progress?.Report(new VisitProgress(VisitPhase.Importing, index + 1, sourcePaths.Length, fileName));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (assets.Count == 0)
                return new ImportResult(project, issues.ToImmutable());
            try
            {
                VisitProject updated = await VisitProjects.RegisterAssetsAsync(project, assets.ToImmutable(),
                    cancellationToken).ConfigureAwait(false);
                return new ImportResult(updated, issues.ToImmutable());
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                throw new IOException("Копии фотографий проверены, но не зарегистрированы: " + exception.Message
                    + " Оригиналы оставлены в папке originals для восстановления.", exception);
            }
        }, cancellationToken);
    }

    public static Task<BitmapSource> LoadPreviewAsync(VisitProject project, Guid assetId,
        int manualQuarterTurns, int maxDimension = 800, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ValidateTurns(manualQuarterTurns);
        if (maxDimension is < 1 or > 800)
            throw new ArgumentOutOfRangeException(nameof(maxDimension), "Предпросмотр ограничен 800 пикселями.");
        PhotoAsset asset = RequireAsset(project, assetId);
        return Task.Run(async () =>
        {
            await ImagingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using FileStream original = OpenVerifiedOriginal(VisitPaths.AssetPath(project, asset), asset.Length,
                    asset.Sha256, cancellationToken);
                ImageHeader header = ReadHeader(original, asset.Format);
                RequireMatchingHeader(asset, header);
                return Normalize(original, header, manualQuarterTurns, maxDimension, cancellationToken);
            }
            finally { ImagingGate.Release(); }
        }, cancellationToken);
    }

    internal static Task<PreparedPhoto> PrepareImageAsync(VisitProject project, PhotoUse use,
        string newDerivativePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(use);
        ValidateTurns(use.ManualQuarterTurns);
        PhotoAsset asset = RequireAsset(project, use.AssetId);
        string destination = RequireDerivativePath(project, use, newDerivativePath);
        return Task.Run(async () =>
        {
            await ImagingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                BitmapSource normalized;
                using (FileStream original = OpenVerifiedOriginal(VisitPaths.AssetPath(project, asset), asset.Length,
                    asset.Sha256, cancellationToken))
                {
                    ImageHeader header = ReadHeader(original, asset.Format);
                    RequireMatchingHeader(asset, header);
                    normalized = Normalize(original, header, use.ManualQuarterTurns, 2048, cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
                encoder.Frames.Add(BitmapFrame.Create(normalized, null, null, null));
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, CopyBufferSize, FileOptions.Asynchronous))
                {
                    encoder.Save(output);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                long length = new FileInfo(destination).Length;
                ValidateLength(length);
                string digest;
                using (var result = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    digest = HashBounded(result, length, cancellationToken);
                    result.Position = 0;
                    ImageHeader header = ReadHeader(result, PhotoFormat.Jpeg);
                    if (header.Format != PhotoFormat.Jpeg || header.Width != normalized.PixelWidth
                        || header.Height != normalized.PixelHeight || header.Orientation is not null)
                        throw new InvalidDataException("Проверка производной фотографии не пройдена.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                return new PreparedPhoto(asset.Id, destination, normalized.PixelWidth, normalized.PixelHeight,
                    length, digest);
            }
            finally { ImagingGate.Release(); }
        }, cancellationToken);
    }

    private static PhotoAsset RequireAsset(VisitProject project, Guid assetId) =>
        project.Document.Assets.FirstOrDefault(asset => asset.Id == assetId)
        ?? throw new ArgumentException("Фотография не зарегистрирована в проекте.", nameof(assetId));

    private static string RequireDerivativePath(VisitProject project, PhotoUse use, string path)
    {
        string destination = VisitPaths.RequireLocalPath(path);
        string root = VisitPaths.RequireLocalPath(Path.Combine(project.DirectoryPath, ".visitplate-drafts"));
        string? directory = Path.GetDirectoryName(destination);
        if (directory is null || !VisitPaths.IsWithin(root, directory)
            || !string.Equals(Path.GetDirectoryName(directory), root, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(directory), "N", out Guid draftId) || draftId == Guid.Empty
            || !string.Equals(Path.GetFileName(destination), $"{use.AssetId:N}-{use.ManualQuarterTurns}.jpg",
                StringComparison.Ordinal)
            || !Directory.Exists(directory))
            throw new ArgumentException("Производная должна находиться в новой папке черновика Visitplate.", nameof(path));
        return destination;
    }

    private static FileStream OpenVerifiedOriginal(string path, long length, string expectedHash,
        CancellationToken cancellationToken)
    {
        ValidateLength(length);
        var stream = new FileStream(VisitPaths.RequireLocalPath(path), FileMode.Open, FileAccess.Read,
            FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
        try
        {
            if (stream.Length != length || !string.Equals(HashBounded(stream, length, cancellationToken),
                expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Оригинал фотографии изменён: размер или SHA-256 не совпадает.");
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static string HashBounded(Stream stream, long expectedLength, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferSize];
        long read = 0;
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            read += count;
            if (read > expectedLength || read > MaximumFileBytes)
                throw new InvalidDataException("Размер фотографии изменился во время проверки.");
            hash.AppendData(buffer, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (read != expectedLength)
            throw new InvalidDataException("Фотография прочитана не полностью.");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static ImageHeader ReadHeader(Stream stream, PhotoFormat format)
    {
        stream.Position = 0;
        const BitmapCreateOptions options = BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat
            | BitmapCreateOptions.IgnoreColorProfile;
        // Explicit codecs reject renamed formats without discovering unrelated installed decoders.
        BitmapDecoder decoder = format switch
        {
            PhotoFormat.Jpeg => new JpegBitmapDecoder(stream, options, BitmapCacheOption.OnDemand),
            PhotoFormat.Png => new PngBitmapDecoder(stream, options, BitmapCacheOption.OnDemand),
            _ => throw new InvalidDataException("Поддерживаются только JPEG и PNG.")
        };
        if (decoder.Frames.Count != 1)
            throw new InvalidDataException("Многокадровые изображения не поддерживаются.");
        BitmapFrame frame = decoder.Frames[0];
        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        if (width <= 0 || height <= 0 || width > 30000 || height > 30000 || (long)width * height > 100_000_000)
            throw new InvalidDataException("Фотография превышает 100 мегапикселей или 30 000 пикселей по стороне.");
        BitmapMetadata? metadata = frame.Metadata as BitmapMetadata;
        int? orientation = null;
        if (format == PhotoFormat.Jpeg && metadata?.GetQuery("/app1/ifd/{ushort=274}") is object rawOrientation)
        {
            if (rawOrientation is not ushort value || value is < 1 or > 8)
                throw new InvalidDataException("Некорректная EXIF-ориентация фотографии: ожидается значение 1-8.");
            orientation = value;
        }
        if (!IsSupportedPixelFormat(frame.Format))
            throw new InvalidDataException($"Цветовой формат {frame.Format} не поддерживается. Сохраните RGB JPEG/PNG.");
        var contexts = frame.ColorContexts;
        if (contexts is { Count: > 1 })
            throw new InvalidDataException("Несколько цветовых профилей фотографии не поддерживаются.");
        ColorContext? colorContext = contexts is { Count: 1 } ? contexts[0] : null;
        if (colorContext is null && metadata is not null)
        {
            if (format == PhotoFormat.Jpeg && metadata.GetQuery("/app1/ifd/exif/{ushort=40961}") is ushort colorSpace
                && colorSpace != 1)
                throw new InvalidDataException("JPEG объявляет неизвестное цветовое пространство без ICC-профиля.");
            if (format == PhotoFormat.Png && metadata.GetQuery("/sRGB/RenderingIntent") is null
                && (metadata.GetQuery("/gAMA/ImageGamma") is not null || metadata.GetQuery("/cHRM/WhitePointX") is not null))
                throw new InvalidDataException("PNG с отдельной gamma/цветностью без sRGB или ICC не поддерживается.");
            if (format == PhotoFormat.Png && metadata.GetQuery("/iCCP/ProfileName") is not null)
                throw new InvalidDataException("ICC-профиль PNG не удалось прочитать.");
        }
        return new ImageHeader(format, width, height, orientation, colorContext);
    }

    private static bool IsSupportedPixelFormat(PixelFormat format) =>
        format == PixelFormats.Bgr24 || format == PixelFormats.Rgb24 || format == PixelFormats.Bgr32
        || format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 || format == PixelFormats.Rgb48
        || format == PixelFormats.Rgba64 || format == PixelFormats.Prgba64 || format == PixelFormats.Gray8
        || format == PixelFormats.Gray16 || format == PixelFormats.Gray4 || format == PixelFormats.Gray2
        || format == PixelFormats.BlackWhite || format == PixelFormats.Indexed1 || format == PixelFormats.Indexed2
        || format == PixelFormats.Indexed4 || format == PixelFormats.Indexed8;

    private static BitmapSource Normalize(Stream stream, ImageHeader header, int turns, int maxDimension,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;
        image.StreamSource = stream;
        if (Math.Max(header.Width, header.Height) > maxDimension)
        {
            if (header.Width >= header.Height)
                image.DecodePixelWidth = maxDimension;
            else
                image.DecodePixelHeight = maxDimension;
        }
        image.EndInit();
        cancellationToken.ThrowIfCancellationRequested();
        BitmapSource pixels = header.ColorContext is null
            ? new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0)
            : new ColorConvertedBitmap(image, header.ColorContext, new ColorContext(PixelFormats.Bgra32), PixelFormats.Bgra32);

        Matrix matrix = (header.Orientation ?? 1) switch
        {
            1 => Matrix.Identity,
            2 => new Matrix(-1, 0, 0, 1, 0, 0),
            3 => new Matrix(-1, 0, 0, -1, 0, 0),
            4 => new Matrix(1, 0, 0, -1, 0, 0),
            5 => new Matrix(0, 1, 1, 0, 0, 0),
            6 => new Matrix(0, 1, -1, 0, 0, 0),
            7 => new Matrix(0, -1, -1, 0, 0, 0),
            8 => new Matrix(0, -1, 1, 0, 0, 0),
            _ => throw new InvalidDataException("Неизвестная ориентация фотографии.")
        };
        matrix.Rotate(turns * 90);
        if (!matrix.IsIdentity)
            pixels = new TransformedBitmap(pixels, new MatrixTransform(matrix));
        int width = pixels.PixelWidth;
        int height = pixels.PixelHeight;
        if (width <= 0 || height <= 0 || Math.Max(width, height) > maxDimension)
            throw new InvalidDataException("Декодер не соблюдает ограничение размера производной фотографии.");

        int stride = checked(width * 4);
        var bgra = new byte[checked(stride * height)];
        pixels.CopyPixels(bgra, stride, 0);
        cancellationToken.ThrowIfCancellationRequested();
        int rgbStride = checked(width * 3);
        var bgr = new byte[checked(rgbStride * height)];
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int input = y * stride + x * 4;
                int output = y * rgbStride + x * 3;
                int alpha = bgra[input + 3];
                for (int channel = 0; channel < 3; channel++)
                    bgr[output + channel] = (byte)((bgra[input + channel] * alpha + 255 * (255 - alpha) + 127) / 255);
            }
        }
        // Pixel-only construction prevents EXIF, GPS, XMP and embedded thumbnails from following the frame.
        BitmapSource normalized = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, bgr, rgbStride);
        normalized.Freeze();
        return normalized;
    }

    private static void RequireMatchingHeader(PhotoAsset asset, ImageHeader header)
    {
        if (asset.Format != header.Format || asset.PixelWidth != header.Width || asset.PixelHeight != header.Height
            || asset.ExifOrientation != header.Orientation)
            throw new InvalidDataException("Заголовок фотографии не совпадает с сохранёнными данными проекта.");
    }

    private static void ValidateLength(long length)
    {
        if (length <= 0 || length > MaximumFileBytes)
            throw new InvalidDataException("Размер фотографии должен быть больше нуля и не превышать 50 МиБ.");
    }

    private static void ValidateTurns(int turns)
    {
        if (turns is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(turns), "Поворот задаётся числом 0, 1, 2 или 3.");
    }

    private static bool IsImageOrFileError(Exception exception) => exception is IOException
        or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException
        or COMException or FormatException or OverflowException;

    private sealed record ImageHeader(PhotoFormat Format, int Width, int Height, int? Orientation,
        ColorContext? ColorContext);
}
