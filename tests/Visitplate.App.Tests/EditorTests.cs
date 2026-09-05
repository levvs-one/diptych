using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Visitplate.Core;

namespace Visitplate.App.Tests;

[TestClass]
public sealed class EditorTests
{
    [TestMethod]
    public Task EditorCommitAndSaveRoundTripPreservesEveryField() => ApplicationLifetime.RunAsync(async () =>
    {
        await using var fixture = await ComponentFixture.CreateAsync();
        fixture.Control<TextBox>("TitleBox").Text = "  Отчёт без усечения  ";
        fixture.Control<TextBox>("SiteBox").Text = "Корпус Б";
        fixture.Control<TextBox>("AuthorBox").Text = "Автор Ёлкин";
        fixture.Control<TextBox>("CustomerBox").Text = "";
        fixture.Control<TextBox>("ReferenceBox").Text = "Заявка № 18";
        fixture.Control<TextBox>("ObservationTitleBox").Text = "Насос";
        fixture.Control<TextBox>("FindingBox").Text = "Первая строка\nВторая строка";
        fixture.Control<TextBox>("WorkDoneBox").Text = "Соединение подтянуто";
        fixture.Control<TextBox>("RemainingBox").Text = "Контроль завтра";
        fixture.Control<ComboBox>("StatusBox").SelectedValue = ObservationStatus.FollowUp;
        Assert.IsTrue(fixture.Control<Button>("SaveButton").IsEnabled);
        Assert.IsTrue(await fixture.RunOperationAsync(_ => fixture.SaveAsync()));
        VisitProject opened = await VisitProjects.OpenAsync(fixture.Project.DirectoryPath);
        Assert.AreEqual("  Отчёт без усечения  ", opened.Document.Details.Title);
        Assert.AreEqual("Корпус Б", opened.Document.Details.Site);
        Assert.AreEqual("Автор Ёлкин", opened.Document.Details.Author);
        Assert.IsNull(opened.Document.Details.Customer);
        Assert.AreEqual("Заявка № 18", opened.Document.Details.Reference);
        Assert.AreEqual("Насос", opened.Document.Observations[0].Title);
        Assert.AreEqual("Первая строка\nВторая строка", opened.Document.Observations[0].Finding);
        Assert.AreEqual("Соединение подтянуто", opened.Document.Observations[0].WorkDone);
        Assert.AreEqual("Контроль завтра", opened.Document.Observations[0].Remaining);
        Assert.AreEqual(ObservationStatus.FollowUp, opened.Document.Observations[0].Status);
        object firstRow = fixture.Control<ListBox>("ObservationsList").Items[0];
        Assert.AreEqual("Насос", firstRow.GetType().GetProperty("Title")!.GetValue(firstRow));
        Assert.IsFalse(fixture.Control<Button>("SaveButton").IsEnabled);
        Assert.IsTrue(fixture.Control<TextBlock>("SaveStateText").Text.StartsWith("Сохранено", StringComparison.Ordinal));
    });

    [TestMethod]
    public Task NodeSelectionCommitsTextToPreviousNode() => ApplicationLifetime.RunAsync(async () =>
    {
        await using var fixture = await ComponentFixture.CreateAsync();
        fixture.Control<TextBox>("FindingBox").Text = "Правка только первого узла";
        fixture.Control<ListBox>("ObservationsList").SelectedIndex = 1;
        Assert.AreEqual("Правка только первого узла", fixture.Document.Observations[0].Finding);
        Assert.AreEqual("Наблюдение 2", fixture.Control<TextBox>("FindingBox").Text);
        fixture.Control<TextBox>("FindingBox").Text = "Правка второго узла";
        fixture.Control<ListBox>("ObservationsList").SelectedIndex = 0;
        Assert.AreEqual("Правка второго узла", fixture.Document.Observations[1].Finding);
        Assert.AreEqual("Правка только первого узла", fixture.Control<TextBox>("FindingBox").Text);
        await fixture.SaveAsync();
    });

    [TestMethod]
    public Task InvalidDateRollsBackNodeSelectionWithoutReplacingPendingText() => ApplicationLifetime.RunAsync(async () =>
    {
        await using var fixture = await ComponentFixture.CreateAsync();
        Guid selected = fixture.Document.Observations[0].Id;
        fixture.Control<DatePicker>("DateBox").Text = "";
        fixture.Control<TextBox>("FindingBox").Text = "Ещё не сохранённый текст";
        fixture.Control<ListBox>("ObservationsList").SelectedIndex = 1;
        Assert.AreEqual(0, fixture.Control<ListBox>("ObservationsList").SelectedIndex);
        Assert.AreEqual(selected, fixture.Get<Guid?>("selectedObservation"));
        Assert.AreEqual("Ещё не сохранённый текст", fixture.Control<TextBox>("FindingBox").Text);
        Assert.AreEqual("", fixture.Control<DatePicker>("DateBox").Text);
        fixture.Control<DatePicker>("DateBox").Text = "05.09.2026";
        await fixture.SaveAsync();
        Assert.AreEqual("Ещё не сохранённый текст", fixture.Project.Document.Observations[0].Finding);
    });

    [TestMethod]
    public Task CaptionSelectionCommitsToCorrectPhotoAndInvalidDateRollsItBack() => ApplicationLifetime.RunAsync(async () =>
    {
        await using var fixture = await ComponentFixture.CreateAsync(photos: true);
        var list = fixture.Control<ListBox>("UsesList");
        list.SelectedIndex = 0;
        fixture.Control<TextBox>("CaptionBox").Text = "Новая подпись до";
        list.SelectedIndex = 1;
        Assert.AreEqual("Новая подпись до", fixture.Document.Observations[0].Photos[0].Caption);
        Assert.AreEqual("После работ", fixture.Control<TextBox>("CaptionBox").Text);
        fixture.Control<DatePicker>("DateBox").Text = "";
        fixture.Control<TextBox>("CaptionBox").Text = "Ожидающая подпись после";
        list.SelectedIndex = 0;
        Assert.AreEqual(1, list.SelectedIndex);
        Assert.AreEqual(1, fixture.Get<int>("selectedUse"));
        Assert.AreEqual("Ожидающая подпись после", fixture.Control<TextBox>("CaptionBox").Text);
        fixture.Control<DatePicker>("DateBox").Text = "05.09.2026";
        await fixture.SaveAsync();
        Assert.AreEqual("Ожидающая подпись после", fixture.Project.Document.Observations[0].Photos[1].Caption);
        Assert.AreEqual("Ожидающая подпись после", fixture.Control<TextBlock>("AfterCaption").Text);
    });

    [TestMethod]
    public Task FailedRealSavePreservesTextSnapshotAndEnablesControls() => ApplicationLifetime.RunAsync(async () =>
    {
        await using var fixture = await ComponentFixture.CreateAsync();
        VisitProject original = fixture.Project;
        string path = Path.Combine(original.DirectoryPath, VisitProjects.DocumentFileName);
        await File.AppendAllTextAsync(path, "\n");
        fixture.Control<TextBox>("FindingBox").Text = "Не терять при конфликте";
        Assert.IsFalse(await fixture.RunOperationAsync(_ => fixture.SaveAsync()));
        Assert.AreSame(original, fixture.Project);
        Assert.AreEqual("Не терять при конфликте", fixture.Document.Observations[0].Finding);
        Assert.AreEqual("Не терять при конфликте", fixture.Control<TextBox>("FindingBox").Text);
        Assert.IsNull(fixture.Get<object?>("operation"));
        Assert.IsTrue(fixture.Control<Grid>("Workspace").IsEnabled);
        Assert.IsTrue(fixture.Control<WrapPanel>("CommandsPanel").IsEnabled);
        Assert.IsTrue(fixture.Control<Button>("SaveButton").IsEnabled);
        Assert.IsFalse(fixture.Autosave.IsEnabled);
        Assert.Contains("изменён", fixture.Control<TextBlock>("StatusText").Text);
    });

    [TestMethod]
    public Task ActualAutosaveTimerWritesAfterInputAndStopsWhenClean() => ApplicationLifetime.RunAsync(async () =>
    {
        await using var fixture = await ComponentFixture.CreateAsync();
        fixture.Autosave.Interval = TimeSpan.FromMilliseconds(30);
        fixture.Control<TextBox>("FindingBox").Text = "Текст для настоящего таймера";
        Assert.IsTrue(fixture.Autosave.IsEnabled);
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        poll.Tick += (_, _) =>
        {
            if (fixture.Project.Document.Observations[0].Finding == "Текст для настоящего таймера"
                && fixture.Get<object?>("operation") is null
                && fixture.Get<Task?>("backgroundSave") is null) saved.TrySetResult();
        };
        poll.Start();
        try { await saved.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { poll.Stop(); }
        Assert.IsFalse(fixture.Autosave.IsEnabled);
        Assert.IsFalse(fixture.Get<bool>("pendingInput"));
        VisitProject reopened = await VisitProjects.OpenAsync(fixture.Project.DirectoryPath);
        Assert.AreEqual("Текст для настоящего таймера", reopened.Document.Observations[0].Finding);
        fixture.Call("ScheduleSave");
        Assert.IsFalse(fixture.Autosave.IsEnabled);
    });

    [TestMethod]
    public Task SchedulingCanResumeStoppedDirtyAutosave() => ApplicationLifetime.RunAsync(async () =>
    {
        await using var fixture = await ComponentFixture.CreateAsync();
        fixture.Control<TextBox>("FindingBox").Text = "Ожидает сохранения";
        fixture.Autosave.Stop();
        fixture.Call("ScheduleSave");
        Assert.IsTrue(fixture.Autosave.IsEnabled);
        await fixture.SaveAsync();
    });

    [TestMethod]
    public Task BackgroundSaveKeepsEditorEnabledAndRetainsLaterUncommittedText() => ApplicationLifetime.RunAsync(async () =>
    {
        await using var fixture = await ComponentFixture.CreateAsync();
        fixture.Control<TextBox>("FindingBox").Text = "Записываемый снимок";
        fixture.Call("AutosaveTick", fixture.Window, EventArgs.Empty);
        Task saving = fixture.Get<Task>("backgroundSave");
        Assert.IsFalse(saving.IsCompleted, "Правка должна попасть в настоящий незавершённый файловый save.");
        Assert.IsTrue(fixture.Control<Grid>("Workspace").IsEnabled);
        Assert.IsTrue(fixture.Control<TextBox>("FindingBox").IsEnabled);
        fixture.Control<TextBox>("FindingBox").Text = "Новые клавиши во время записи";
        await saving;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.AreEqual("Записываемый снимок", fixture.Project.Document.Observations[0].Finding);
        Assert.AreEqual("Новые клавиши во время записи", fixture.Control<TextBox>("FindingBox").Text);
        Assert.IsTrue(fixture.Get<bool>("pendingInput"));
        Assert.IsTrue(fixture.Autosave.IsEnabled);
        Assert.IsTrue(await fixture.RunOperationAsync(_ => fixture.SaveAsync()));
        Assert.AreEqual("Новые клавиши во время записи", fixture.Project.Document.Observations[0].Finding);
    });

    [TestMethod]
    public Task BackgroundSaveRebasesConcurrentNodeMoveAndRetainsPendingText() => ApplicationLifetime.RunAsync(async () =>
    {
        await using var fixture = await ComponentFixture.CreateAsync();
        Guid first = fixture.Document.Observations[0].Id;
        fixture.Control<TextBox>("FindingBox").Text = "До перестановки";
        fixture.Call("AutosaveTick", fixture.Window, EventArgs.Empty);
        Task saving = fixture.Get<Task>("backgroundSave");
        Assert.IsFalse(saving.IsCompleted);
        fixture.Call("MoveObservation", 1);
        fixture.Control<TextBox>("FindingBox").Text = "После перестановки";
        await saving;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.AreEqual(first, fixture.Project.Document.Observations[0].Id);
        Assert.AreEqual(first, fixture.Document.Observations[1].Id);
        Assert.AreEqual(fixture.Project.Document.Revision, fixture.Document.Revision);
        Assert.AreEqual("После перестановки", fixture.Control<TextBox>("FindingBox").Text);
        Assert.IsTrue(fixture.Get<bool>("pendingInput"));
        Assert.IsTrue(await fixture.RunOperationAsync(_ => fixture.SaveAsync()));
        VisitProject opened = await VisitProjects.OpenAsync(fixture.Project.DirectoryPath);
        Assert.AreEqual(first, opened.Document.Observations[1].Id);
        Assert.AreEqual("После перестановки", opened.Document.Observations[1].Finding);
    });

    [TestMethod]
    public Task ManualSaveWaitsForBackgroundSnapshotThenPersistsLaterInput() => ApplicationLifetime.RunAsync(async () =>
    {
        await using var fixture = await ComponentFixture.CreateAsync();
        long initialRevision = fixture.Project.Document.Revision;
        fixture.Control<TextBox>("FindingBox").Text = "Фоновая редакция";
        fixture.Call("AutosaveTick", fixture.Window, EventArgs.Empty);
        Assert.IsFalse(fixture.Get<Task>("backgroundSave").IsCompleted);
        fixture.Control<TextBox>("FindingBox").Text = "Явно сохраняемая редакция";
        Assert.IsTrue(await fixture.RunOperationAsync(_ => fixture.SaveAsync()));
        Assert.AreEqual(initialRevision + 2, fixture.Project.Document.Revision);
        Assert.AreEqual("Явно сохраняемая редакция", fixture.Project.Document.Observations[0].Finding);
        Assert.IsFalse(fixture.Get<bool>("pendingInput"));
        Assert.IsNull(fixture.Get<Task?>("backgroundSave"));
        Assert.IsTrue(fixture.Control<Grid>("Workspace").IsEnabled);
    });

    [TestMethod]
    public Task FailedBackgroundSaveStopsTimerStartedByConcurrentInput() => ApplicationLifetime.RunAsync(async () =>
    {
        await using var fixture = await ComponentFixture.CreateAsync();
        VisitProject original = fixture.Project;
        await File.AppendAllTextAsync(Path.Combine(original.DirectoryPath, VisitProjects.DocumentFileName), "\n");
        fixture.Control<TextBox>("FindingBox").Text = "Снимок перед конфликтом";
        fixture.Call("AutosaveTick", fixture.Window, EventArgs.Empty);
        Task saving = fixture.Get<Task>("backgroundSave");
        Assert.IsFalse(saving.IsCompleted);
        fixture.Control<TextBox>("FindingBox").Text = "Новые клавиши до ошибки";
        Assert.IsTrue(fixture.Autosave.IsEnabled);
        await Assert.ThrowsAsync<IOException>(async () => await saving);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.AreSame(original, fixture.Project);
        Assert.AreEqual("Новые клавиши до ошибки", fixture.Control<TextBox>("FindingBox").Text);
        Assert.IsTrue(fixture.Get<bool>("pendingInput"));
        Assert.IsTrue(fixture.Get<bool>("saveFailed"));
        Assert.IsFalse(fixture.Autosave.IsEnabled, "Ошибка останавливает даже таймер, заведённый новым вводом во время IO.");
        Assert.IsTrue(fixture.Control<Grid>("Workspace").IsEnabled);
        Assert.IsNull(fixture.Get<Task?>("backgroundSave"));
    });

    [TestMethod]
    public Task EditorOperationsKeepBoundedUndoHistoryAcrossSaving() => ApplicationLifetime.RunAsync(async () =>
    {
        await using var fixture = await ComponentFixture.CreateAsync();
        for (int index = 0; index < 40; index++)
        {
            fixture.Control<TextBox>("FindingBox").Text = $"Версия {index}";
            fixture.Call("CommitEditors");
        }
        var undo = fixture.Get<List<VisitDocument>>("undo");
        Assert.HasCount(32, undo);
        Assert.AreEqual("Версия 38", undo[^1].Observations[0].Finding);
        await fixture.SaveAsync();
        Assert.HasCount(32, undo);
        Assert.AreEqual("Версия 39", fixture.Project.Document.Observations[0].Finding);
        fixture.Call("MoveObservation", 1);
        Assert.AreEqual(fixture.Project.Document.Observations[0].Id, fixture.Document.Observations[1].Id);
        Assert.AreEqual(fixture.Project.Document.Observations[0].Id, undo[^1].Observations[0].Id);
    });

    [TestMethod]
    [DataRow("fileFormat")]
    [DataRow("format")]
    [DataRow("overflow")]
    [DataRow("invalidData")]
    [DataRow("io")]
    [DataRow("json")]
    public Task ExpectedFailureIsReportedWithoutDiscardingDocument(string kind) => ApplicationLifetime.RunAsync(async () =>
    {
        await using var fixture = await ComponentFixture.CreateAsync();
        Exception error = kind switch
        {
            "fileFormat" => new FileFormatException("regression"),
            "format" => new FormatException("regression"),
            "overflow" => new OverflowException("regression"),
            "invalidData" => new InvalidDataException("regression"),
            "json" => new JsonException("regression"),
            _ => new IOException("regression"),
        };
        VisitDocument original = fixture.Document;
        Assert.IsFalse(await fixture.RunOperationAsync(_ => Task.FromException(error)));
        Assert.AreSame(original, fixture.Document);
        Assert.IsNull(fixture.Get<object?>("operation"));
        Assert.Contains("regression", fixture.Control<TextBlock>("StatusText").Text);
        Assert.IsTrue(fixture.Control<Grid>("Workspace").IsEnabled);
    });

    [TestMethod]
    public Task BusyCloseCancelsRealOperationAndRetainsPendingWork() => ApplicationLifetime.RunAsync(async () =>
    {
        await using var fixture = await ComponentFixture.CreateAsync();
        fixture.Control<TextBox>("FindingBox").Text = "Не потерять при отмене";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> operation = fixture.RunOperationAsync(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await started.Task;
        var closing = new CancelEventArgs();
        fixture.Call("WindowClosing", fixture.Window, closing);
        Assert.IsTrue(closing.Cancel);
        Assert.IsFalse(await operation);
        Assert.AreEqual("Не потерять при отмене", fixture.Control<TextBox>("FindingBox").Text);
        Assert.IsTrue(fixture.Get<bool>("pendingInput"));
        Assert.IsNull(fixture.Get<object?>("operation"));
        Assert.Contains("отменена", fixture.Control<TextBlock>("StatusText").Text);
    });

    [TestMethod]
    public Task CleanWindowAllowsClosingWithoutPrompt() => ApplicationLifetime.RunAsync(async () =>
    {
        await using var fixture = await ComponentFixture.CreateAsync();
        var closing = new CancelEventArgs();
        fixture.Call("WindowClosing", fixture.Window, closing);
        Assert.IsFalse(closing.Cancel);
        Assert.IsFalse(fixture.Autosave.IsEnabled);
    });
}
