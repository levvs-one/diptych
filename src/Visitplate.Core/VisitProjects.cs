using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Visitplate.Core;

public static class VisitProjects
{
    public const string DocumentFileName = "visitplate.json";
    internal const int MaximumJsonBytes = 2 * 1024 * 1024;
    internal const long MaximumAssetBytes = 50L * 1024 * 1024;
    internal const long MaximumOriginalBytes = 2L * 1024 * 1024 * 1024;
    internal const int MaximumAssets = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 16,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                typeInfo =>
                {
                    if (typeInfo.Type != typeof(PhotoAsset)) return;
                    // JsonIgnore alone accepts an input member silently; the portable schema has no path member.
                    JsonPropertyInfo? path = typeInfo.Properties.FirstOrDefault(property => property.Name == "relativePath");
                    if (path is not null) typeInfo.Properties.Remove(path);
                },
            },
        },
    };

    public static async Task<VisitProject> CreateAsync(string newDirectory, VisitDetails details,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(details);
        string directory = VisitPaths.RequireLocalPath(newDirectory);
        string? parent = Path.GetDirectoryName(directory);
        if (parent is null || !Directory.Exists(parent))
            throw new DirectoryNotFoundException("Родительская папка проекта должна существовать.");
        if (Path.Exists(directory))
            throw new IOException("Проект создаётся только в новой папке.");

        var document = new VisitDocument(1, Guid.NewGuid(), 0, details, [], []);
        Validate(document);
        byte[] bytes = Serialize(document);
        // Directory.Move supplies the no-overwrite commit that CreateDirectory does not have.
        string partialDirectory = Path.Combine(parent, $".visitplate-create-{Guid.NewGuid():N}.partial");
        VisitPaths.RequireLocalPath(partialDirectory);
        if (Path.Exists(partialDirectory))
            throw new IOException("Временная папка проекта уже существует.");
        Directory.CreateDirectory(partialDirectory);
        try
        {
            Directory.CreateDirectory(Path.Combine(partialDirectory, "originals"));
            string manifestPath = Path.Combine(partialDirectory, DocumentFileName);
            await WriteNewJsonAsync(manifestPath, bytes, cancellationToken).ConfigureAwait(false);
            await CheckWrittenJsonAsync(manifestPath, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            VisitPaths.RequireLocalPath(directory);
            Directory.Move(partialDirectory, directory);
            return new VisitProject(directory, document, Fingerprint(bytes));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or NotSupportedException or OperationCanceledException)
        {
            exception.Data["PartialPath"] = partialDirectory;
            throw;
        }
    }

    public static async Task<VisitProject> OpenAsync(string directory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string localDirectory = VisitPaths.RequireLocalPath(directory);
        VisitProject project = await ReadProjectAsync(localDirectory, cancellationToken).ConfigureAwait(false);
        using FileStream projectLock = AcquireLock(localDirectory);
        await RequireCurrentAsync(project, cancellationToken).ConfigureAwait(false);
        await VerifyAssetsAsync(project, project.Document.Assets, cancellationToken).ConfigureAwait(false);
        return project;
    }

    public static async Task<VisitProject> SaveAsync(VisitProject project, VisitDocument document,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(document);
        Validate(document);
        RequireMatchingIdentity(project, document);
        if (!document.Assets.SequenceEqual(project.Document.Assets))
            throw new InvalidDataException("Обычное сохранение не может менять список оригиналов.");

        using FileStream projectLock = AcquireLock(project.DirectoryPath);
        await RequireCurrentAsync(project, cancellationToken).ConfigureAwait(false);
        return await CommitAsync(project, document, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<VisitProject> RegisterAssetsAsync(VisitProject project,
        ImmutableArray<PhotoAsset> newAssets, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(project);
        if (newAssets.IsDefault)
            throw new ArgumentException("Список новых оригиналов не задан.", nameof(newAssets));
        using FileStream projectLock = AcquireLock(project.DirectoryPath);
        await RequireCurrentAsync(project, cancellationToken).ConfigureAwait(false);
        if (newAssets.IsEmpty)
            return project;
        VisitDocument document = project.Document with { Assets = project.Document.Assets.AddRange(newAssets) };
        Validate(document);
        await VerifyAssetsAsync(project, newAssets, cancellationToken).ConfigureAwait(false);
        return await CommitAsync(project, document, cancellationToken).ConfigureAwait(false);
    }

    internal static FileStream AcquireLock(string directory)
    {
        string localDirectory = VisitPaths.RequireLocalPath(directory);
        if (!Directory.Exists(localDirectory))
            throw new DirectoryNotFoundException("Папка проекта не найдена.");
        string lockPath = VisitPaths.RequireLocalPath(Path.Combine(localDirectory, ".visitplate.lock"));
        return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    // The caller holds the same cooperative lock through the subsequent publication/commit.
    internal static async Task RequireCurrentAsync(VisitProject project, CancellationToken cancellationToken = default)
    {
        VisitProject current = await ReadProjectAsync(project.DirectoryPath, cancellationToken).ConfigureAwait(false);
        if (current.Document.Id != project.Document.Id || current.Document.Revision != project.Document.Revision
            || current.DocumentFingerprint != project.DocumentFingerprint)
            throw new IOException("Проект изменён другим экземпляром или внешним редактором. Откройте его заново.");
    }

    internal static void Validate(VisitDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != 1 || document.Id == Guid.Empty || document.Revision < 0)
            throw new InvalidDataException("Недопустимая версия схемы, идентификатор или ревизия проекта.");
        if (document.Details is null || document.Assets.IsDefault || document.Observations.IsDefault)
            throw new InvalidDataException("Обязательные поля проекта не заданы.");
        if (document.Assets.Length > MaximumAssets || document.Observations.Length > 100)
            throw new InvalidDataException("Лимит проекта: 200 оригиналов и 100 записей.");

        int textLength = 0;
        CheckText(document.Details.Title, 4000, ref textLength);
        CheckText(document.Details.Site, 4000, ref textLength);
        CheckText(document.Details.Author, 4000, ref textLength);
        if (document.Details.Customer is not null) CheckText(document.Details.Customer, 4000, ref textLength);
        if (document.Details.Reference is not null) CheckText(document.Details.Reference, 4000, ref textLength);
        var assetIds = new HashSet<Guid>();
        long totalBytes = 0;
        foreach (PhotoAsset asset in document.Assets)
        {
            if (asset is null || asset.Id == Guid.Empty || !assetIds.Add(asset.Id) || !Enum.IsDefined(asset.Format))
                throw new InvalidDataException("Недопустимый или повторный идентификатор оригинала.");
            if (string.IsNullOrWhiteSpace(asset.OriginalFileName) || asset.OriginalFileName.Length > 255
                || asset.OriginalFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || asset.OriginalFileName.Any(char.IsControl) || asset.OriginalFileName is "." or "..")
                throw new InvalidDataException("Имя исходного снимка должно быть обычным именем файла без пути.");
            CheckText(asset.OriginalFileName, 255, ref textLength);
            if (asset.Length is <= 0 or > MaximumAssetBytes || asset.PixelWidth is <= 0 or > 30000
                || asset.PixelHeight is <= 0 or > 30000 || (long)asset.PixelWidth * asset.PixelHeight > 100_000_000
                || asset.ExifOrientation is not (null or >= 1 and <= 8) || !IsHash(asset.Sha256))
                throw new InvalidDataException("Недопустимые размеры, ориентация или SHA-256 оригинала.");
            totalBytes += asset.Length;
            if (totalBytes > MaximumOriginalBytes)
                throw new InvalidDataException("Оригиналы превышают лимит 2 GiB.");
        }

        var observationIds = new HashSet<Guid>();
        int photoUses = 0;
        foreach (Observation observation in document.Observations)
        {
            if (observation is null || observation.Id == Guid.Empty || !observationIds.Add(observation.Id)
                || !Enum.IsDefined(observation.Status) || observation.Photos.IsDefault || observation.Photos.Length > MaximumAssets)
                throw new InvalidDataException("Недопустимая или повторная запись проекта.");
            photoUses += observation.Photos.Length;
            if (photoUses > MaximumAssets)
                throw new InvalidDataException("В проекте допустимо не более 200 использований снимков.");
            CheckText(observation.Title, 4000, ref textLength);
            CheckText(observation.Finding, 4000, ref textLength);
            CheckText(observation.WorkDone, 4000, ref textLength);
            CheckText(observation.Remaining, 4000, ref textLength);
            Guid? before = null;
            Guid? after = null;
            foreach (PhotoUse use in observation.Photos)
            {
                if (use is null || !assetIds.Contains(use.AssetId) || !Enum.IsDefined(use.Role)
                    || use.ManualQuarterTurns is < 0 or > 3)
                    throw new InvalidDataException("Недопустимая ссылка, роль или поворот снимка.");
                CheckText(use.Caption, 400, ref textLength);
                if (use.Role == PhotoRole.Before)
                {
                    if (before.HasValue) throw new InvalidDataException("В записи допустим только один снимок «до».");
                    before = use.AssetId;
                }
                else if (use.Role == PhotoRole.After)
                {
                    if (after.HasValue) throw new InvalidDataException("В записи допустим только один снимок «после».");
                    after = use.AssetId;
                }
            }
            if (before.HasValue && before == after)
                throw new InvalidDataException("Один оригинал нельзя использовать одновременно как «до» и «после».");
        }
    }

    private static void CheckText(string text, int maximumLength, ref int totalLength)
    {
        if (text is null || text.Length > maximumLength)
            throw new InvalidDataException($"Текст должен быть задан и содержать не более {maximumLength} символов.");
        // Preserve the user's whitespace and line breaks; reject only non-text control characters.
        if (text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new InvalidDataException("Текст содержит недопустимые управляющие символы.");
        for (int index = 0; index < text.Length; index++)
        {
            if (!char.IsSurrogate(text[index])) continue;
            if (!char.IsHighSurrogate(text[index]) || index + 1 == text.Length || !char.IsLowSurrogate(text[index + 1]))
                throw new InvalidDataException("Текст содержит повреждённую последовательность Unicode.");
            index++;
        }
        totalLength += text.Length;
        if (totalLength > 250_000)
            throw new InvalidDataException("Текст проекта превышает лимит 250 000 символов.");
    }

    private static bool IsHash(string hash) => hash is { Length: 64 }
        && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RequireMatchingIdentity(VisitProject project, VisitDocument document)
    {
        if (document.Id != project.Document.Id || document.Revision != project.Document.Revision)
            throw new InvalidDataException("Сохранение должно сохранять идентификатор и текущую ревизию снимка проекта.");
    }

    private static async Task<VisitProject> ReadProjectAsync(string directory, CancellationToken cancellationToken)
    {
        string manifestPath = VisitPaths.RequireLocalPath(Path.Combine(directory, DocumentFileName));
        byte[] bytes = await ReadJsonBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        VisitDocument document = Deserialize(bytes);
        return new VisitProject(directory, document, Fingerprint(bytes));
    }

    private static VisitDocument Deserialize(byte[] bytes)
    {
        try
        {
            VisitDocument document = JsonSerializer.Deserialize<VisitDocument>(bytes, JsonOptions)
                ?? throw new InvalidDataException("JSON проекта не может быть null.");
            Validate(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("JSON проекта повреждён или не соответствует схеме.", exception);
        }
    }

    private static byte[] Serialize(VisitDocument document)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > MaximumJsonBytes)
            throw new InvalidDataException("JSON проекта превышает лимит 2 MiB.");
        return bytes;
    }

    private static async Task<byte[]> ReadJsonBytesAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumJsonBytes)
            throw new InvalidDataException("JSON проекта превышает лимит 2 MiB.");
        using var bytes = new MemoryStream((int)stream.Length);
        byte[] buffer = new byte[64 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > MaximumJsonBytes)
                throw new InvalidDataException("JSON проекта превышает лимит 2 MiB.");
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }

    private static string Fingerprint(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task VerifyAssetsAsync(VisitProject project, ImmutableArray<PhotoAsset> assets,
        CancellationToken cancellationToken)
    {
        foreach (PhotoAsset asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = VisitPaths.AssetPath(project, asset);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != asset.Length)
                throw new InvalidDataException($"Длина оригинала изменилась: {asset.OriginalFileName}");
            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (hash != asset.Sha256)
                throw new InvalidDataException($"SHA-256 оригинала не совпадает: {asset.OriginalFileName}");
        }
    }

    private static async Task<VisitProject> CommitAsync(VisitProject project, VisitDocument document,
        CancellationToken cancellationToken)
    {
        if (document.Revision == long.MaxValue)
            throw new InvalidDataException("Достигнут предел ревизий проекта.");
        VisitDocument next = document with { Revision = document.Revision + 1 };
        byte[] bytes = Serialize(next);
        string temporaryPath = VisitPaths.RequireLocalPath(Path.Combine(project.DirectoryPath,
            $".visitplate-{Guid.NewGuid():N}.partial"));
        string backupPath = VisitPaths.RequireLocalPath(Path.Combine(project.DirectoryPath,
            $"visitplate-{document.Revision}-{Guid.NewGuid():N}.backup.json"));
        try
        {
            await WriteNewJsonAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            await CheckWrittenJsonAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            await RequireCurrentAsync(project, cancellationToken).ConfigureAwait(false);
            string manifestPath = VisitPaths.RequireLocalPath(Path.Combine(project.DirectoryPath, DocumentFileName));
            VisitPaths.RequireLocalPath(temporaryPath);
            VisitPaths.RequireLocalPath(backupPath);
            if (Path.Exists(backupPath)) throw new IOException("Файл резервной копии уже существует.");
            cancellationToken.ThrowIfCancellationRequested();
            // No delete/move fallback: a failed replace leaves recovery evidence instead of hiding a failed save.
            File.Replace(temporaryPath, manifestPath, backupPath);
            return new VisitProject(project.DirectoryPath, next, Fingerprint(bytes));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or NotSupportedException or OperationCanceledException)
        {
            exception.Data["PartialPath"] = temporaryPath;
            exception.Data["BackupPath"] = backupPath;
            throw;
        }
    }

    private static async Task WriteNewJsonAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task CheckWrittenJsonAsync(string path, byte[] expected, CancellationToken cancellationToken)
    {
        byte[] actual = await ReadJsonBytesAsync(path, cancellationToken).ConfigureAwait(false);
        Deserialize(actual);
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new IOException("Проверка записанного JSON не подтвердила его содержимое.");
    }
}
