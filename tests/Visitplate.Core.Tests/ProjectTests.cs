using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Visitplate.Core;

namespace Visitplate.Core.Tests;

[TestClass]
public sealed class ProjectTests
{
    internal static VisitDetails Details { get; } = new("Выезд: насосная № 2", "ул. Ёлочная, 7", new DateOnly(2026, 9, 5),
        "Лев Васильев", "Заказчик", "Акт 17");

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SaveUsesRealReplaceAndRetainsEachPreviousVersion(bool ntfs)
    {
        using var fixture = new FileFixture(ntfs);
        VisitProject first = await VisitProjects.CreateAsync(fixture.PathFor("project"), Details);
        string manifest = Path.Combine(first.DirectoryPath, VisitProjects.DocumentFileName);
        byte[] firstBytes = await File.ReadAllBytesAsync(manifest);
        Assert.AreEqual(0L, first.Document.Revision);
        VisitProject second = await VisitProjects.SaveAsync(first, first.Document with
        {
            Observations = [Note() with { Finding = "Неполная запись\nРазмер 12 × 18 мм; температура 21 °C" }],
        });
        byte[] secondBytes = await File.ReadAllBytesAsync(manifest);
        VisitProject third = await VisitProjects.SaveAsync(second, second.Document with { Details = Details with { Title = "" } });
        Assert.AreEqual(2L, third.Document.Revision);
        string[] backups = Directory.GetFiles(first.DirectoryPath, "*.backup.json");
        Assert.HasCount(2, backups);
        string[] hashes = backups.Select(path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        CollectionAssert.Contains(hashes, Convert.ToHexStringLower(SHA256.HashData(firstBytes)));
        CollectionAssert.Contains(hashes, Convert.ToHexStringLower(SHA256.HashData(secondBytes)));
        VisitProject reopened = await VisitProjects.OpenAsync(third.DirectoryPath);
        Assert.AreEqual(third.DocumentFingerprint, reopened.DocumentFingerprint);
        Assert.AreEqual("", reopened.Document.Details.Title);
        Assert.AreEqual(second.Document.Observations[0].Finding, reopened.Document.Observations[0].Finding);
        Assert.HasCount(0, Directory.GetFiles(first.DirectoryPath, "*.partial"));
    }

    [TestMethod]
    public async Task RegisteredOriginalSurvivesSaveAndPortableFolderMoveUnchanged()
    {
        using var fixture = new FileFixture();
        VisitProject project = await VisitProjects.CreateAsync(fixture.PathFor("project"), Details);
        PhotoAsset asset = FileFixture.WriteAsset(project, [1, 3, 5, 7, 9]);
        string originalPath = VisitPaths.AssetPath(project, asset);
        var original = FileFixture.Snapshot(originalPath);
        project = await VisitProjects.RegisterAssetsAsync(project, [asset]);
        project = await VisitProjects.SaveAsync(project, project.Document with
        {
            Observations = [Note() with { Photos = [new PhotoUse(asset.Id, PhotoRole.Before, "Положение до работ", 3)] }],
        });
        Assert.AreEqual(original, FileFixture.Snapshot(originalPath));
        Assert.DoesNotContain("relativePath", await File.ReadAllTextAsync(Path.Combine(project.DirectoryPath, VisitProjects.DocumentFileName)));
        string moved = fixture.PathFor("moved");
        Directory.Move(project.DirectoryPath, moved);
        VisitProject opened = await VisitProjects.OpenAsync(moved);
        Assert.AreEqual(project.DocumentFingerprint, opened.DocumentFingerprint);
        Assert.AreEqual(3, opened.Document.Observations[0].Photos[0].ManualQuarterTurns);
        Assert.AreEqual(asset, opened.Document.Assets[0]);
        Assert.AreEqual(original, FileFixture.Snapshot(VisitPaths.AssetPath(opened, asset)));
    }

    [TestMethod]
    public async Task SameRevisionExternalByteEditPreventsSaveAndIsPreserved()
    {
        using var fixture = new FileFixture();
        VisitProject project = await VisitProjects.CreateAsync(fixture.PathFor("project"), Details);
        string path = Path.Combine(project.DirectoryPath, VisitProjects.DocumentFileName);
        string original = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, original + "\n");
        byte[] external = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsExactlyAsync<IOException>(() => VisitProjects.SaveAsync(project, project.Document));
        CollectionAssert.AreEqual(external, await File.ReadAllBytesAsync(path));
        VisitProject reopened = await VisitProjects.OpenAsync(project.DirectoryPath);
        Assert.AreEqual(project.Document.Revision, reopened.Document.Revision);
        Assert.AreNotEqual(project.DocumentFingerprint, reopened.DocumentFingerprint);
    }

    [TestMethod]
    public async Task StaleSnapshotCannotOverwriteNewerRevision()
    {
        using var fixture = new FileFixture();
        VisitProject first = await VisitProjects.CreateAsync(fixture.PathFor("project"), Details);
        VisitProject second = await VisitProjects.OpenAsync(first.DirectoryPath);
        VisitProject saved = await VisitProjects.SaveAsync(first, first.Document with { Details = Details with { Title = "Первая правка" } });
        await Assert.ThrowsExactlyAsync<IOException>(() => VisitProjects.SaveAsync(second, second.Document));
        Assert.AreEqual(saved.DocumentFingerprint, (await VisitProjects.OpenAsync(saved.DirectoryPath)).DocumentFingerprint);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CallerCannotChooseNewProjectIdentityOrRevision(bool changeIdentity)
    {
        using var fixture = new FileFixture();
        VisitProject project = await VisitProjects.CreateAsync(fixture.PathFor("project"), Details);
        VisitDocument changed = changeIdentity ? project.Document with { Id = Guid.NewGuid() }
            : project.Document with { Revision = project.Document.Revision + 1 };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VisitProjects.SaveAsync(project, changed));
        Assert.AreEqual(project.DocumentFingerprint, (await VisitProjects.OpenAsync(project.DirectoryPath)).DocumentFingerprint);
    }

    [TestMethod]
    public async Task RevisionOverflowIsRejectedWithoutPartialWrite()
    {
        using var fixture = new FileFixture();
        VisitProject project = await VisitProjects.CreateAsync(fixture.PathFor("project"), Details);
        string path = Path.Combine(project.DirectoryPath, VisitProjects.DocumentFileName);
        JsonObject json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        json["revision"] = long.MaxValue;
        await File.WriteAllTextAsync(path, json.ToJsonString());
        project = await VisitProjects.OpenAsync(project.DirectoryPath);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VisitProjects.SaveAsync(project, project.Document));
        Assert.HasCount(0, Directory.GetFiles(project.DirectoryPath, "*.partial"));
        Assert.AreEqual(long.MaxValue, (await VisitProjects.OpenAsync(project.DirectoryPath)).Document.Revision);
    }

    [TestMethod]
    public async Task RegistrationIsAppendOnlyAndEmptyRegistrationDoesNotCreateRevision()
    {
        using var fixture = new FileFixture();
        VisitProject project = await VisitProjects.CreateAsync(fixture.PathFor("project"), Details);
        PhotoAsset asset = FileFixture.WriteAsset(project);
        project = await VisitProjects.RegisterAssetsAsync(project, [asset]);
        VisitProject unchanged = await VisitProjects.RegisterAssetsAsync(project, []);
        Assert.AreSame(project, unchanged);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VisitProjects.RegisterAssetsAsync(project, [asset]));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => VisitProjects.RegisterAssetsAsync(project, default));
        Assert.AreEqual(project.DocumentFingerprint, (await VisitProjects.OpenAsync(project.DirectoryPath)).DocumentFingerprint);
    }

    [TestMethod]
    public async Task CooperativeLockExcludesOpenSaveAndRegistrationButStaleLockFileDoesNot()
    {
        using var fixture = new FileFixture();
        VisitProject project = await VisitProjects.CreateAsync(fixture.PathFor("project"), Details);
        using (VisitProjects.AcquireLock(project.DirectoryPath))
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => VisitProjects.OpenAsync(project.DirectoryPath));
            await Assert.ThrowsExactlyAsync<IOException>(() => VisitProjects.SaveAsync(project, project.Document));
            await Assert.ThrowsExactlyAsync<IOException>(() => VisitProjects.RegisterAssetsAsync(project, []));
        }
        Assert.IsTrue(File.Exists(Path.Combine(project.DirectoryPath, ".visitplate.lock")));
        Assert.AreEqual(project.DocumentFingerprint, (await VisitProjects.OpenAsync(project.DirectoryPath)).DocumentFingerprint);
    }

    [TestMethod]
    public async Task ExistingDirectoryOrFileIsNeverAdoptedOrOverwritten()
    {
        using var fixture = new FileFixture();
        string foreignFile = fixture.Write("foreign\\private.txt", [4, 5, 6]);
        var original = FileFixture.Snapshot(foreignFile);
        await Assert.ThrowsExactlyAsync<IOException>(() => VisitProjects.CreateAsync(Path.GetDirectoryName(foreignFile)!, Details));
        await Assert.ThrowsExactlyAsync<IOException>(() => VisitProjects.CreateAsync(foreignFile, Details));
        Assert.AreEqual(original, FileFixture.Snapshot(foreignFile));
        Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(foreignFile)!, VisitProjects.DocumentFileName)));
    }

    [TestMethod]
    public async Task OpeningForeignOrMalformedFolderDoesNotCreateLockFile()
    {
        using var fixture = new FileFixture();
        string file = fixture.Write("foreign\\private.txt", [1]);
        string directory = Path.GetDirectoryName(file)!;
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => VisitProjects.OpenAsync(directory));
        Assert.IsFalse(File.Exists(Path.Combine(directory, ".visitplate.lock")));
        await File.WriteAllTextAsync(Path.Combine(directory, VisitProjects.DocumentFileName), "{}");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VisitProjects.OpenAsync(directory));
        Assert.IsFalse(File.Exists(Path.Combine(directory, ".visitplate.lock")));
    }

    [TestMethod]
    public async Task FailedReplacePreservesManifestAndReportsRetainedPartial()
    {
        using var fixture = new FileFixture();
        VisitProject project = await VisitProjects.CreateAsync(fixture.PathFor("project"), Details);
        string manifest = Path.Combine(project.DirectoryPath, VisitProjects.DocumentFileName);
        byte[] original = await File.ReadAllBytesAsync(manifest);
        IOException failure;
        using (var reader = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            failure = await Assert.ThrowsAsync<IOException>(() => VisitProjects.SaveAsync(project,
                project.Document with { Details = Details with { Title = "Новая версия" } }));
        }
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(manifest));
        Assert.IsInstanceOfType<string>(failure.Data["PartialPath"]);
        string partial = (string)failure.Data["PartialPath"]!;
        Assert.IsTrue(VisitPaths.IsWithin(project.DirectoryPath, partial));
        Assert.IsTrue(File.Exists(partial));
        Assert.AreEqual(1, JsonNode.Parse(await File.ReadAllTextAsync(partial))!["revision"]!.GetValue<int>());
        Assert.AreEqual(project.DocumentFingerprint, (await VisitProjects.OpenAsync(project.DirectoryPath)).DocumentFingerprint);
    }

    [TestMethod]
    public async Task CancellationBeforeEachOperationLeavesProjectAndSourceUnchanged()
    {
        using var fixture = new FileFixture();
        VisitProject project = await VisitProjects.CreateAsync(fixture.PathFor("project"), Details);
        PhotoAsset asset = FileFixture.WriteAsset(project);
        var original = FileFixture.Snapshot(VisitPaths.AssetPath(project, asset));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => VisitProjects.CreateAsync(fixture.PathFor("canceled"), Details, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => VisitProjects.OpenAsync(project.DirectoryPath, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => VisitProjects.SaveAsync(project, project.Document, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => VisitProjects.RegisterAssetsAsync(project, [asset], cancellation.Token));
        Assert.IsFalse(Directory.Exists(fixture.PathFor("canceled")));
        Assert.AreEqual(project.DocumentFingerprint, (await VisitProjects.OpenAsync(project.DirectoryPath)).DocumentFingerprint);
        Assert.AreEqual(original, FileFixture.Snapshot(VisitPaths.AssetPath(project, asset)));
    }

    [TestMethod]
    [DataRow("remove")]
    [DataRow("replace")]
    [DataRow("append")]
    [DataRow("reorder")]
    public async Task OrdinarySaveCannotMutateImmutableAssetRegistry(string mutation)
    {
        using var fixture = new FileFixture();
        VisitProject project = await VisitProjects.CreateAsync(fixture.PathFor("project"), Details);
        PhotoAsset first = FileFixture.WriteAsset(project);
        PhotoAsset second = FileFixture.WriteAsset(project);
        project = await VisitProjects.RegisterAssetsAsync(project, [first, second]);
        ImmutableArray<PhotoAsset> assets = mutation switch
        {
            "remove" => [first],
            "replace" => [first with { PixelWidth = 2 }, second],
            "append" => [first, second, first with { Id = Guid.NewGuid() }],
            _ => [second, first],
        };
        VisitDocument changed = project.Document with { Assets = assets };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VisitProjects.SaveAsync(project, changed));
        Assert.AreEqual(project.DocumentFingerprint, (await VisitProjects.OpenAsync(project.DirectoryPath)).DocumentFingerprint);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("size")]
    [DataRow("hash")]
    public async Task RegistrationRejectsUnverifiedFilesWithoutChangingManifest(string corruption)
    {
        using var fixture = new FileFixture();
        VisitProject project = await VisitProjects.CreateAsync(fixture.PathFor("project"), Details);
        PhotoAsset asset = FileFixture.WriteAsset(project);
        if (corruption == "missing") asset = asset with { Id = Guid.NewGuid() };
        else if (corruption == "size") asset = asset with { Length = asset.Length + 1 };
        else asset = asset with { Sha256 = new string('0', 64) };
        if (corruption == "missing")
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => VisitProjects.RegisterAssetsAsync(project, [asset]));
        else
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VisitProjects.RegisterAssetsAsync(project, [asset]));
        Assert.AreEqual(project.DocumentFingerprint, (await VisitProjects.OpenAsync(project.DirectoryPath)).DocumentFingerprint);
    }

    [TestMethod]
    public async Task OpenRejectsChangedOriginalEvenWhenLengthAndTimestampArePreserved()
    {
        using var fixture = new FileFixture();
        VisitProject project = await VisitProjects.CreateAsync(fixture.PathFor("project"), Details);
        PhotoAsset asset = FileFixture.WriteAsset(project);
        project = await VisitProjects.RegisterAssetsAsync(project, [asset]);
        string path = VisitPaths.AssetPath(project, asset);
        DateTime timestamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllBytesAsync(path, new byte[asset.Length]);
        File.SetLastWriteTimeUtc(path, timestamp);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VisitProjects.OpenAsync(project.DirectoryPath));
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("{")]
    [DataRow("[]")]
    [DataRow("{}")]
    [DataRow("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    public async Task MalformedJsonIsRejected(string json)
    {
        using var fixture = new FileFixture();
        VisitProject project = await VisitProjects.CreateAsync(fixture.PathFor("project"), Details);
        await File.WriteAllTextAsync(Path.Combine(project.DirectoryPath, VisitProjects.DocumentFileName), json);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VisitProjects.OpenAsync(project.DirectoryPath));
    }

    [TestMethod]
    [DataRow("duplicateRoot")]
    [DataRow("duplicateNested")]
    [DataRow("unknownRoot")]
    [DataRow("unknownNested")]
    [DataRow("nullDetails")]
    [DataRow("nullTitle")]
    [DataRow("nullAssets")]
    [DataRow("nullAsset")]
    [DataRow("nullObservations")]
    [DataRow("nullObservation")]
    [DataRow("missingTitle")]
    [DataRow("missingAssets")]
    [DataRow("relativePathInjection")]
    public async Task StrictJsonRejectsDuplicateUnknownNullAndMissingMembers(string corruption)
    {
        using var fixture = new FileFixture();
        VisitProject project = await VisitProjects.CreateAsync(fixture.PathFor("project"), Details);
        PhotoAsset asset = FileFixture.WriteAsset(project);
        project = await VisitProjects.RegisterAssetsAsync(project, [asset]);
        string path = Path.Combine(project.DirectoryPath, VisitProjects.DocumentFileName);
        string text = await File.ReadAllTextAsync(path);
        JsonObject json = JsonNode.Parse(text)!.AsObject();
        switch (corruption)
        {
            case "duplicateRoot": text = text.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1,\"schemaVersion\": 1", StringComparison.Ordinal); break;
            case "duplicateNested": text = text.Replace("\"author\":", "\"author\":\"external\",\"author\":", StringComparison.Ordinal); break;
            case "unknownRoot": json["foreign"] = true; break;
            case "unknownNested": json["details"]!["foreign"] = true; break;
            case "nullDetails": json["details"] = null; break;
            case "nullTitle": json["details"]!["title"] = null; break;
            case "nullAssets": json["assets"] = null; break;
            case "nullAsset": json["assets"]![0] = null; break;
            case "nullObservations": json["observations"] = null; break;
            case "nullObservation": json["observations"] = new JsonArray((JsonNode?)null); break;
            case "missingTitle": json["details"]!.AsObject().Remove("title"); break;
            case "missingAssets": json.Remove("assets"); break;
            case "relativePathInjection": json["assets"]![0]!["relativePath"] = "../../private.jpg"; break;
        }
        if (!corruption.StartsWith("duplicate", StringComparison.Ordinal)) text = json.ToJsonString();
        await File.WriteAllTextAsync(path, text);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VisitProjects.OpenAsync(project.DirectoryPath));
    }

    [TestMethod]
    public async Task OversizedAndExcessivelyDeepJsonAreRejected()
    {
        using var fixture = new FileFixture();
        VisitProject project = await VisitProjects.CreateAsync(fixture.PathFor("project"), Details);
        string path = Path.Combine(project.DirectoryPath, VisitProjects.DocumentFileName);
        await File.WriteAllBytesAsync(path, new byte[VisitProjects.MaximumJsonBytes + 1]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VisitProjects.OpenAsync(project.DirectoryPath));
        await File.WriteAllTextAsync(path, new string('[', 17) + "0" + new string(']', 17));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VisitProjects.OpenAsync(project.DirectoryPath));
    }

    [TestMethod]
    [DataRow("schema")]
    [DataRow("id")]
    [DataRow("revision")]
    [DataRow("details")]
    [DataRow("assetsDefault")]
    [DataRow("observationsDefault")]
    [DataRow("titleNull")]
    [DataRow("longText")]
    [DataRow("control")]
    [DataRow("surrogate")]
    [DataRow("duplicateObservation")]
    [DataRow("nullObservation")]
    [DataRow("status")]
    [DataRow("photosDefault")]
    [DataRow("tooManyObservations")]
    [DataRow("totalText")]
    public void StructuralDocumentErrorsAreRejectedBeforeIo(string error)
    {
        VisitDocument document = EmptyDocument();
        Observation note = Note();
        document = error switch
        {
            "schema" => document with { SchemaVersion = 2 },
            "id" => document with { Id = Guid.Empty },
            "revision" => document with { Revision = -1 },
            "details" => document with { Details = null! },
            "assetsDefault" => document with { Assets = default },
            "observationsDefault" => document with { Observations = default },
            "titleNull" => document with { Details = Details with { Title = null! } },
            "longText" => document with { Details = Details with { Site = new string('x', 4001) } },
            "control" => document with { Details = Details with { Site = "bad\0text" } },
            "surrogate" => document with { Details = Details with { Site = "bad\ud800text" } },
            "duplicateObservation" => document with { Observations = [note, note] },
            "nullObservation" => document with { Observations = [null!] },
            "status" => document with { Observations = [note with { Status = (ObservationStatus)99 }] },
            "photosDefault" => document with { Observations = [note with { Photos = default }] },
            "tooManyObservations" => document with { Observations = Enumerable.Range(0, 101).Select(_ => Note()).ToImmutableArray() },
            _ => document with { Observations = Enumerable.Range(0, 64).Select(_ => Note() with { Finding = new string('x', 4000) }).ToImmutableArray() },
        };
        Assert.ThrowsExactly<InvalidDataException>(() => VisitProjects.Validate(document));
    }

    [TestMethod]
    [DataRow("duplicate")]
    [DataRow("emptyId")]
    [DataRow("format")]
    [DataRow("filePath")]
    [DataRow("filenameNull")]
    [DataRow("filenameUnicode")]
    [DataRow("zeroLength")]
    [DataRow("largeLength")]
    [DataRow("zeroWidth")]
    [DataRow("largeWidth")]
    [DataRow("largePixels")]
    [DataRow("orientation")]
    [DataRow("hash")]
    [DataRow("hashUppercase")]
    [DataRow("hashNull")]
    [DataRow("null")]
    [DataRow("tooMany")]
    [DataRow("totalLength")]
    public void StructuralAssetErrorsAreRejectedBeforeIo(string error)
    {
        PhotoAsset asset = Descriptor();
        PhotoAsset changed = error switch
        {
            "emptyId" => asset with { Id = Guid.Empty },
            "format" => asset with { Format = (PhotoFormat)55 },
            "filePath" => asset with { OriginalFileName = @"..\source.jpg" },
            "filenameNull" => asset with { OriginalFileName = null! },
            "filenameUnicode" => asset with { OriginalFileName = "broken\ud800.jpg" },
            "zeroLength" => asset with { Length = 0 },
            "largeLength" => asset with { Length = VisitProjects.MaximumAssetBytes + 1 },
            "zeroWidth" => asset with { PixelWidth = 0 },
            "largeWidth" => asset with { PixelWidth = 30001 },
            "largePixels" => asset with { PixelWidth = 20000, PixelHeight = 20000 },
            "orientation" => asset with { ExifOrientation = 9 },
            "hash" => asset with { Sha256 = "invalid" },
            "hashUppercase" => asset with { Sha256 = new string('A', 64) },
            "hashNull" => asset with { Sha256 = null! },
            _ => asset,
        };
        ImmutableArray<PhotoAsset> assets = error switch
        {
            "duplicate" => [asset, asset],
            "null" => [null!],
            "tooMany" => Enumerable.Range(0, 201).Select(_ => Descriptor()).ToImmutableArray(),
            "totalLength" => Enumerable.Range(0, 41).Select(_ => Descriptor() with { Length = VisitProjects.MaximumAssetBytes }).ToImmutableArray(),
            _ => [changed],
        };
        Assert.ThrowsExactly<InvalidDataException>(() => VisitProjects.Validate(EmptyDocument() with { Assets = assets }));
    }

    [TestMethod]
    [DataRow("missingAsset")]
    [DataRow("role")]
    [DataRow("turnsNegative")]
    [DataRow("turnsFour")]
    [DataRow("captionLong")]
    [DataRow("captionNull")]
    [DataRow("duplicateBefore")]
    [DataRow("duplicateAfter")]
    [DataRow("bothRoles")]
    [DataRow("nullUse")]
    [DataRow("totalUses")]
    public void StructuralPhotoUseErrorsAreRejectedBeforeIo(string error)
    {
        PhotoAsset asset = Descriptor();
        PhotoUse use = new(asset.Id, PhotoRole.Overview, "");
        PhotoUse changed = error switch
        {
            "missingAsset" => use with { AssetId = Guid.NewGuid() },
            "role" => use with { Role = (PhotoRole)88 },
            "turnsNegative" => use with { ManualQuarterTurns = -1 },
            "turnsFour" => use with { ManualQuarterTurns = 4 },
            "captionLong" => use with { Caption = new string('x', 401) },
            "captionNull" => use with { Caption = null! },
            _ => use,
        };
        ImmutableArray<PhotoUse> uses = error switch
        {
            "duplicateBefore" => [use with { Role = PhotoRole.Before }, use with { Role = PhotoRole.Before }],
            "duplicateAfter" => [use with { Role = PhotoRole.After }, use with { Role = PhotoRole.After }],
            "bothRoles" => [use with { Role = PhotoRole.Before }, use with { Role = PhotoRole.After }],
            "nullUse" => [null!],
            _ => [changed],
        };
        ImmutableArray<Observation> notes = error == "totalUses"
            ? [Note() with { Photos = Enumerable.Repeat(use, 101).ToImmutableArray() }, Note() with { Photos = Enumerable.Repeat(use, 100).ToImmutableArray() }]
            : [Note() with { Photos = uses }];
        Assert.ThrowsExactly<InvalidDataException>(() => VisitProjects.Validate(EmptyDocument() with { Assets = [asset], Observations = notes }));
    }

    [TestMethod]
    public void IncompleteNotesAndPerUseRotationRemainEditable()
    {
        PhotoAsset asset = Descriptor();
        VisitDocument document = EmptyDocument() with
        {
            Details = new VisitDetails("", "", default, ""),
            Assets = [asset],
            Observations = [Note() with { Photos = [new PhotoUse(asset.Id, PhotoRole.Before, "", 1)] },
                Note() with { Photos = [new PhotoUse(asset.Id, PhotoRole.After, "", 3)] }],
        };
        VisitProjects.Validate(document);
        Assert.AreEqual(1, document.Observations[0].Photos[0].ManualQuarterTurns);
        Assert.AreEqual(3, document.Observations[1].Photos[0].ManualQuarterTurns);
        Assert.IsNull(asset.ExifOrientation);
    }

    private static VisitDocument EmptyDocument() => new(1, Guid.NewGuid(), 0, Details, [], []);
    private static Observation Note() => new(Guid.NewGuid(), "", "", "", "", ObservationStatus.Recorded, []);
    private static PhotoAsset Descriptor() => new(Guid.NewGuid(), PhotoFormat.Jpeg, "original.jpg", 4, new string('a', 64), 1, 1, null);
}
