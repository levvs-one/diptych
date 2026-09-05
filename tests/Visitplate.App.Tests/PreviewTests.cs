using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Visitplate.Core;

namespace Visitplate.App.Tests;

[TestClass]
public sealed class PreviewTests
{
    [TestMethod]
    public Task ActualPdfPreviewFitsAfterFirstLayoutAndPublishesExactBytes() => ApplicationLifetime.RunAsync(async () =>
    {
        await using var fixture = await ComponentFixture.CreateAsync(photos: true, observationCount: 1);
        ReportDraft draft = await VisitReports.PrepareAsync(fixture.Project);
        object preview = fixture.Get<object>("preview");
        await (Task)preview.GetType().GetMethod("OpenAsync")!.Invoke(preview, [draft, CancellationToken.None])!;
        fixture.Set("draft", draft);
        fixture.Control<Grid>("EditorPane").Visibility = Visibility.Collapsed;
        fixture.Control<Grid>("PreviewPane").Visibility = Visibility.Visible;
        await (Task)fixture.Call("ShowPageAsync", 0)!;
        var image = fixture.Control<Image>("PdfImage");
        Assert.IsInstanceOfType<BitmapSource>(image.Source);
        var bitmap = (BitmapSource)image.Source;
        Assert.IsTrue(bitmap.IsFrozen);
        Assert.IsLessThanOrEqualTo(2048, Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
        var content = (Grid)fixture.Window.Content;
        content.Measure(new Size(1320, 880));
        content.Arrange(new Rect(0, 0, 1320, 880));
        content.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var scroll = fixture.Control<ScrollViewer>("PdfScroll");
        double expected = Math.Min(Math.Max(200, scroll.ActualWidth - 24),
            Math.Max(200, scroll.ActualHeight - 24) * bitmap.PixelWidth / bitmap.PixelHeight);
        Assert.IsGreaterThan(200d, expected, "Тестовый viewport должен быть больше начального размера-заглушки 200 px.");
        Assert.AreEqual(expected, image.Width, 0.01, "Первый реальный layout должен пересчитать масштаб.");
        await Assert.ThrowsAsync<IOException>(() => File.WriteAllBytesAsync(draft.Path, [1, 2, 3]));
        string published = await VisitReports.PublishAsync(fixture.Project, draft, Path.Combine(fixture.Root, "published.pdf"));
        Assert.AreEqual(draft.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(published))));
        Assert.AreEqual(draft.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(draft.Path))));
        Assert.IsTrue(fixture.Control<Button>("PublishButton").IsEnabled);
        fixture.Call("BackToEditorClick", fixture.Window, new RoutedEventArgs());
        fixture.Control<TextBox>("FindingBox").Text = "Повторный просмотр после правки в том же окне";
        await fixture.SaveAsync();
        ReportDraft second = await VisitReports.PrepareAsync(fixture.Project);
        Assert.AreEqual(draft.Revision + 1, second.Revision);
        await (Task)preview.GetType().GetMethod("OpenAsync")!.Invoke(preview, [second, CancellationToken.None])!;
        fixture.Set("draft", second);
        fixture.Control<Grid>("EditorPane").Visibility = Visibility.Collapsed;
        fixture.Control<Grid>("PreviewPane").Visibility = Visibility.Visible;
        await (Task)fixture.Call("ShowPageAsync", 0)!;
        Assert.IsInstanceOfType<BitmapSource>(image.Source);
        Assert.IsTrue(((BitmapSource)image.Source).IsFrozen);
        Assert.AreNotSame(bitmap, image.Source);
        content.Measure(new Size(1320, 880));
        content.Arrange(new Rect(0, 0, 1320, 880));
        content.UpdateLayout();
        string secondPublished = await VisitReports.PublishAsync(fixture.Project, second, Path.Combine(fixture.Root, "second.pdf"));
        Assert.AreEqual(second.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(secondPublished))));
        Assert.AreEqual(draft.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(published))));
        Assert.IsFalse(fixture.Window.IsVisible);
    });

    [TestMethod]
    public Task EditingInvalidatesActualPreparedDraftAndDisablesPublishing() => ApplicationLifetime.RunAsync(async () =>
    {
        await using var fixture = await ComponentFixture.CreateAsync(photos: true, observationCount: 1);
        ReportDraft draft = await VisitReports.PrepareAsync(fixture.Project);
        object preview = fixture.Get<object>("preview");
        await (Task)preview.GetType().GetMethod("OpenAsync")!.Invoke(preview, [draft, CancellationToken.None])!;
        fixture.Set("draft", draft);
        await (Task)fixture.Call("ShowPageAsync", 0)!;
        Assert.IsInstanceOfType<BitmapSource>(fixture.Control<Image>("PdfImage").Source);
        fixture.Call("RefreshButtons");
        Assert.IsTrue(fixture.Control<Button>("PublishButton").IsEnabled);
        fixture.Control<TextBox>("FindingBox").Text = "Правка после подготовки PDF";
        Assert.IsNull(fixture.Get<object?>("draft"));
        Assert.IsNull(fixture.Control<Image>("PdfImage").Source);
        Assert.IsFalse(fixture.Control<Button>("PublishButton").IsEnabled);
        Assert.IsTrue(fixture.Get<bool>("pendingInput"));
        Assert.AreEqual(0, preview.GetType().GetProperty("PageCount")!.GetValue(preview));
        using var exclusive = new FileStream(draft.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.AreEqual(draft.Sha256, Convert.ToHexStringLower(SHA256.HashData(exclusive)));
    });

    [TestMethod]
    public Task CorruptedImageDecoderFailureIsRecognizedAsExpected() => ApplicationLifetime.RunAsync(() =>
    {
        using var bytes = new MemoryStream([0xff, 0xd8, 0xff, 0xd9]);
        Exception failure = Assert.Throws<FormatException>(() =>
            new JpegBitmapDecoder(bytes, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat
                | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnDemand));
        bool expected = (bool)typeof(MainWindow).GetMethod("Expected", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [failure])!;
        Assert.IsTrue(expected);
        return Task.CompletedTask;
    });
}
