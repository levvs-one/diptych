using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using Visitplate.Core;

namespace Visitplate.App.Tests;

[TestClass]
public sealed class PreviewIntegrityTests
{
    [TestMethod]
    public Task IndependentPreviewsCanRenderConcurrentlyAndCloseSeparately() => ApplicationLifetime.RunAsync(async () =>
    {
        await using var fixture = await ComponentFixture.CreateAsync(photos: true, observationCount: 1);
        ReportDraft draft = await VisitReports.PrepareAsync(fixture.Project);
        Type type = fixture.Get<object>("preview").GetType();
        using var first = (IDisposable)Activator.CreateInstance(type, nonPublic: true)!;
        using var second = (IDisposable)Activator.CreateInstance(type, nonPublic: true)!;
        MethodInfo open = type.GetMethod("OpenAsync")!;
        MethodInfo render = type.GetMethod("RenderAsync")!;
        await Task.WhenAll((Task)open.Invoke(first, [draft, CancellationToken.None])!,
            (Task)open.Invoke(second, [draft, CancellationToken.None])!);
        BitmapSource[] images = await Task.WhenAll(
            (Task<BitmapSource>)render.Invoke(first, [0, CancellationToken.None])!,
            (Task<BitmapSource>)render.Invoke(second, [0, CancellationToken.None])!);
        Assert.IsTrue(images.All(image => image.IsFrozen));
        Assert.AreEqual(images[0].PixelWidth, images[1].PixelWidth);
        Assert.AreEqual(images[0].PixelHeight, images[1].PixelHeight);
        int stride = images[0].PixelWidth * 4;
        byte[] firstPixels = new byte[stride * images[0].PixelHeight];
        byte[] secondPixels = new byte[firstPixels.Length];
        images[0].CopyPixels(firstPixels, stride, 0);
        images[1].CopyPixels(secondPixels, stride, 0);
        CollectionAssert.AreEqual(firstPixels, secondPixels);
        first.Dispose();
        BitmapSource remaining = await (Task<BitmapSource>)render.Invoke(second, [0, CancellationToken.None])!;
        Assert.IsTrue(remaining.IsFrozen);
        Assert.AreEqual(draft.PageCount, type.GetProperty("PageCount")!.GetValue(second));
        second.Dispose();
        using var exclusive = new FileStream(draft.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.AreEqual(draft.Sha256, Convert.ToHexStringLower(SHA256.HashData(exclusive)));
    });

    [TestMethod]
    public Task CancellationReleasesFailedOpenAndDoesNotPoisonLaterRendering() => ApplicationLifetime.RunAsync(async () =>
    {
        await using var fixture = await ComponentFixture.CreateAsync(photos: true, observationCount: 1);
        ReportDraft draft = await VisitReports.PrepareAsync(fixture.Project);
        object preview = fixture.Get<object>("preview");
        Type type = preview.GetType();
        MethodInfo open = type.GetMethod("OpenAsync")!;
        MethodInfo render = type.GetMethod("RenderAsync")!;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await (Task)open.Invoke(preview, [draft, cancellation.Token])!);
        Assert.AreEqual(0, type.GetProperty("PageCount")!.GetValue(preview));
        using (var exclusive = new FileStream(draft.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.IsGreaterThan(0L, exclusive.Length);

        await (Task)open.Invoke(preview, [draft, CancellationToken.None])!;
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await (Task<BitmapSource>)render.Invoke(preview, [0, cancellation.Token])!);
        BitmapSource image = await (Task<BitmapSource>)render.Invoke(preview, [0, CancellationToken.None])!;
        Assert.IsTrue(image.IsFrozen);
        Assert.IsGreaterThan(0, image.PixelWidth);
        Assert.AreEqual(draft.PageCount, type.GetProperty("PageCount")!.GetValue(preview));
        await Assert.ThrowsAsync<IOException>(() => File.WriteAllBytesAsync(draft.Path, [1, 2, 3]));
        ((IDisposable)preview).Dispose();
        ((IDisposable)preview).Dispose();
        Assert.AreEqual(0, type.GetProperty("PageCount")!.GetValue(preview));
        using (var exclusive = new FileStream(draft.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.IsGreaterThan(0L, exclusive.Length);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await (Task<BitmapSource>)render.Invoke(preview, [0, CancellationToken.None])!);
    });

    [TestMethod]
    [DataRow("hash")]
    [DataRow("page-count")]
    [DataRow("invalid-pdf")]
    [DataRow("size")]
    public Task RejectedDraftLeavesNoReadablePreviewOrLockedHandle(string failure) => ApplicationLifetime.RunAsync(async () =>
    {
        await using var fixture = await ComponentFixture.CreateAsync(photos: true, observationCount: 1);
        ReportDraft valid = await VisitReports.PrepareAsync(fixture.Project);
        object preview = fixture.Get<object>("preview");
        Type type = preview.GetType();
        MethodInfo open = type.GetMethod("OpenAsync")!;
        await (Task)open.Invoke(preview, [valid, CancellationToken.None])!;
        Assert.AreEqual(valid.PageCount, type.GetProperty("PageCount")!.GetValue(preview));

        string path = Path.Combine(fixture.Root, "rejected.pdf");
        File.Copy(valid.Path, path);
        string hash = valid.Sha256;
        int pageCount = valid.PageCount;
        switch (failure)
        {
            case "hash":
                await File.WriteAllBytesAsync(path, "synthetic changed PDF bytes"u8.ToArray());
                break;
            case "page-count":
                pageCount++;
                break;
            case "invalid-pdf":
                byte[] invalid = "synthetic input that is not a PDF"u8.ToArray();
                await File.WriteAllBytesAsync(path, invalid);
                hash = Convert.ToHexStringLower(SHA256.HashData(invalid));
                break;
            case "size":
                using (var oversized = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                    oversized.SetLength(128L * 1024 * 1024 + 1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failure));
        }
        // Malformed internal descriptors are test inputs, never a public constructor in the app.
        ReportDraft rejected = (ReportDraft)typeof(ReportDraft).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke([path, valid.ProjectId, valid.Revision, valid.DocumentFingerprint, hash, pageCount, valid.PhotoCount, valid.Warnings]);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await (Task)open.Invoke(preview, [rejected, CancellationToken.None])!);
        Assert.AreEqual(0, type.GetProperty("PageCount")!.GetValue(preview));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsGreaterThan(0L, exclusive.Length);
        using var oldExclusive = new FileStream(valid.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.AreEqual(valid.Sha256, Convert.ToHexStringLower(SHA256.HashData(oldExclusive)));
    });
}
