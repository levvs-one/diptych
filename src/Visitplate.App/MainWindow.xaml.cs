using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Visitplate.Core;

namespace Visitplate.App;

public partial class MainWindow : Window
{
    private readonly List<VisitDocument> undo = [];
    private readonly PdfPreview preview = new();
    private readonly SemaphoreSlim thumbnailSlots = new(2, 2);
    private readonly DispatcherTimer autosave = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly CultureInfo culture = CultureInfo.GetCultureInfo("ru-RU");
    private VisitProject? project;
    private VisitDocument? document;
    private ReportDraft? draft;
    private CancellationTokenSource? operation;
    private Task? backgroundSave;
    private CancellationTokenSource imageWork = new();
    private ImmutableArray<PhotoAsset> shownAssets;
    private Guid? selectedObservation;
    private int selectedUse = -1;
    private int pageIndex;
    private int pairGeneration;
    private bool loading = true;
    private bool ready;
    private bool pendingInput;
    private bool saveFailed;
    private bool allowClose;

    private bool HasChanges => pendingInput || (document is not null && document != project?.Document);
    private bool Busy => operation is not null;

    public MainWindow()
    {
        InitializeComponent();
        StatusBox.ItemsSource = new Dictionary<ObservationStatus, string>
        {
            [ObservationStatus.Recorded] = "Зафиксировано",
            [ObservationStatus.Completed] = "Выполнено",
            [ObservationStatus.FollowUp] = "Нужен повторный выезд"
        };
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(InputChanged));
        autosave.Tick += AutosaveTick;
        SizeChanged += (_, _) => UpdatePdfScale();
        PdfScroll.SizeChanged += (_, _) => UpdatePdfScale();
        ready = true;
        loading = false;
        RefreshButtons();
    }

    private async void NewProjectClick(object sender, RoutedEventArgs e)
    {
        if (!await CanSwitchAsync()) return;
        var dialog = new NewProjectWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ProjectDirectory is null) { ScheduleSave(); return; }
        await RunOperation(async token =>
        {
            var details = new VisitDetails("", "", DateOnly.FromDateTime(DateTime.Today), "");
            LoadProject(await VisitProjects.CreateAsync(dialog.ProjectDirectory, details, token));
            StatusText.Text = "Папка выезда создана. Заполните данные и добавьте фотографии.";
        });
        ScheduleSave();
    }

    private async void OpenProjectClick(object sender, RoutedEventArgs e)
    {
        if (!await CanSwitchAsync()) return;
        var dialog = new OpenFolderDialog { Title = "Открыть папку с visitplate.json" };
        if (dialog.ShowDialog(this) != true) { ScheduleSave(); return; }
        await RunOperation(async token =>
        {
            var opened = await VisitProjects.OpenAsync(dialog.FolderName, token);
            LoadProject(opened);
            StatusText.Text = "Проект открыт. Состав и исходные снимки проверены.";
        });
        ScheduleSave();
    }

    private async Task<bool> CanSwitchAsync()
    {
        if (Busy) return false;
        autosave.Stop();
        if (backgroundSave is Task saving)
        {
            try { await saving; }
            catch (Exception error) when (Expected(error)) { StatusText.Text = error.Message; return false; }
        }
        autosave.Stop();
        if (!HasChanges) return true;
        var answer = MessageBox.Show(this, "Сохранить изменения текущего выезда?", "Несохранённые изменения",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Cancel) { ScheduleSave(); return false; }
        return answer == MessageBoxResult.No || await RunOperation(SaveCurrentAsync);
    }

    private void LoadProject(VisitProject opened)
    {
        imageWork.Cancel();
        imageWork.Dispose();
        imageWork = new CancellationTokenSource();
        project = opened;
        document = opened.Document;
        DetailsExpander.IsExpanded = string.IsNullOrWhiteSpace(document.Details.Title)
            || string.IsNullOrWhiteSpace(document.Details.Site) || string.IsNullOrWhiteSpace(document.Details.Author);
        undo.Clear();
        pendingInput = false;
        saveFailed = false;
        selectedObservation = document.Observations.FirstOrDefault()?.Id;
        selectedUse = -1;
        shownAssets = default;
        InvalidateDraft();
        RefreshView();
    }

    private async void SaveClick(object sender, RoutedEventArgs e) => await RunOperation(SaveCurrentAsync);

    private async Task SaveCurrentAsync(CancellationToken token)
    {
        CommitEditors();
        if (project is null || document is null) return;
        if (!HasChanges) { StatusText.Text = "Изменений для сохранения нет."; return; }
        VisitDocument snapshot = document;
        var saved = await VisitProjects.SaveAsync(project, snapshot, token);
        project = saved;
        saveFailed = false;
        // New keystrokes and structural edits may arrive while the immutable snapshot is written.
        document = ReferenceEquals(document, snapshot) ? saved.Document : document with { Revision = saved.Document.Revision };
        StatusText.Text = HasChanges ? "Предыдущие изменения сохранены. Есть новые правки." : "Изменения сохранены.";
        RefreshObservationRows();
        int index = ObservationIndex();
        if (index >= 0)
        {
            BeforeCaption.Text = document.Observations[index].Photos.FirstOrDefault(use => use.Role == PhotoRole.Before)?.Caption ?? "";
            AfterCaption.Text = document.Observations[index].Photos.FirstOrDefault(use => use.Role == PhotoRole.After)?.Caption ?? "";
        }
        RefreshSaveState();
    }

    private async void ImportClick(object sender, RoutedEventArgs e)
    {
        if (project is null || Busy) return;
        var dialog = new OpenFileDialog
        {
            Title = "Добавить JPEG или PNG - оригиналы не меняются",
            Filter = "Фотографии JPEG и PNG|*.jpg;*.jpeg;*.png",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        await RunOperation(async token =>
        {
            await SaveCurrentAsync(token);
            var imported = await VisitPhotos.ImportAsync(project!, dialog.FileNames.ToImmutableArray(), Progress(), token);
            var selected = selectedObservation;
            project = imported.Project;
            document = project.Document;
            undo.Clear();
            selectedObservation = selected;
            InvalidateDraft();
            RefreshView();
            StatusText.Text = $"В проекте {document.Assets.Length} снимков. Оригиналы сохранены отдельно от PDF.";
            ShowIssues(imported.Issues, "Результат добавления фотографий");
        });
    }

    private async void PrepareClick(object sender, RoutedEventArgs e)
    {
        if (project is null || Busy) return;
        await RunOperation(async token =>
        {
            await SaveCurrentAsync(token);
            var issues = VisitReports.Validate(document!);
            if (issues.Any(issue => issue.Severity == VisitIssueSeverity.Error))
            {
                ShowIssues(issues, "Что нужно перед подготовкой PDF");
                return;
            }
            InvalidateDraft();
            ReportDraft prepared = await VisitReports.PrepareAsync(project!, Progress(), token);
            BitmapSource firstPage;
            try
            {
                await preview.OpenAsync(prepared, token);
                firstPage = await preview.RenderAsync(0, token);
            }
            catch
            {
                preview.Dispose();
                throw;
            }
            draft = prepared;
            pageIndex = 0;
            PdfImage.Source = firstPage;
            EditorPane.Visibility = Visibility.Collapsed;
            PreviewPane.Visibility = Visibility.Visible;
            DraftInfoText.Text = $"Редакция {prepared.Revision} - {prepared.PageCount} стр., {prepared.PhotoCount} снимков";
            UpdatePageControls();
            UpdatePdfScale();
            StatusText.Text = "Проверьте страницы. Сохранение передаст именно этот PDF без повторной генерации.";
            ShowIssues(prepared.Warnings, "Замечания к отчёту");
        });
    }

    private async void PublishClick(object sender, RoutedEventArgs e)
    {
        if (project is null || draft is null || Busy || HasChanges) return;
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить просмотренный PDF под новым именем",
            FileName = "Фотоотчёт.pdf", Filter = "PDF|*.pdf", DefaultExt = ".pdf", AddExtension = true,
            OverwritePrompt = false, CheckPathExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        await RunOperation(async token =>
        {
            string path = await VisitReports.PublishAsync(project!, draft!, dialog.FileName, token);
            StatusText.Text = $"PDF сохранён и проверен: {path}";
        });
    }

    private void BackToEditorClick(object sender, RoutedEventArgs e)
    {
        if (Busy) return;
        PreviewPane.Visibility = Visibility.Collapsed;
        EditorPane.Visibility = Visibility.Visible;
        RefreshButtons();
    }

    private async void PreviousPageClick(object sender, RoutedEventArgs e) => await ShowPageAsync(pageIndex - 1);
    private async void NextPageClick(object sender, RoutedEventArgs e) => await ShowPageAsync(pageIndex + 1);

    private async Task ShowPageAsync(int target)
    {
        if (draft is null || Busy || target < 0 || target >= draft.PageCount) return;
        var currentDraft = draft;
        await RunOperation(async token =>
        {
            BitmapSource page = await preview.RenderAsync(target, token);
            if (!ReferenceEquals(draft, currentDraft)) return;
            PdfImage.Source = page;
            pageIndex = target;
            UpdatePageControls();
            UpdatePdfScale();
            PdfScroll.ScrollToTop();
            StatusText.Text = $"Страница {pageIndex + 1} из {draft.PageCount}.";
        });
    }

    private void UpdatePageControls()
    {
        PageLabel.Text = draft is null ? "" : $"{pageIndex + 1} / {draft.PageCount}";
        PreviousPageButton.IsEnabled = draft is not null && pageIndex > 0;
        NextPageButton.IsEnabled = draft is not null && pageIndex + 1 < draft.PageCount;
    }

    private void ZoomChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ready) UpdatePdfScale();
    }

    private void UpdatePdfScale()
    {
        if (!ready || PdfImage.Source is not BitmapSource image) return;
        double width = Math.Max(200, PdfScroll.ActualWidth - 24);
        if (ZoomBox.SelectedIndex == 0)
            width = Math.Min(width, Math.Max(200, PdfScroll.ActualHeight - 24) * image.PixelWidth / image.PixelHeight);
        PdfImage.Width = width;
    }

    private void InputChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready || loading || Busy || document is null || PreviewPane.Visibility == Visibility.Visible) return;
        pendingInput = true;
        InvalidateDraft();
        RefreshSaveState();
        autosave.Stop();
        autosave.Start();
    }

    private async void AutosaveTick(object? sender, EventArgs e)
    {
        autosave.Stop();
        if (Busy || !HasChanges) return;
        if (backgroundSave is not null) { ScheduleSave(); return; }
        bool saved = false;
        try
        {
            backgroundSave = SaveCurrentAsync(CancellationToken.None);
            await backgroundSave;
            saved = true;
        }
        catch (Exception error) when (Expected(error)) { saveFailed = true; StatusText.Text = error.Message; }
        finally
        {
            backgroundSave = null;
            RefreshSaveState();
            if (saved) ScheduleSave();
            else autosave.Stop();
        }
    }

    private void EditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!loading && !Busy && ready) TryCommitEditors();
    }

    private void DateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loading || Busy || !ready || document is null) return;
        pendingInput = true;
        TryCommitEditors();
        autosave.Stop();
        autosave.Start();
    }

    private void DateValidationError(object? sender, DatePickerDateValidationErrorEventArgs e)
    {
        e.ThrowException = false;
        pendingInput = true;
        StatusText.Text = "Дата не распознана. Используйте день, месяц и год.";
        RefreshSaveState();
    }

    private void StatusChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loading && !Busy && ready) { pendingInput = true; TryCommitEditors(); ScheduleSave(); }
    }

    private void CaptionLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!loading && !Busy && ready) TryCommitEditors();
    }

    private bool TryCommitEditors()
    {
        try { CommitEditors(); return true; }
        catch (ArgumentException error) { StatusText.Text = error.Message; return false; }
    }

    private void CommitEditors()
    {
        if (loading || document is null) return;
        if (!DateOnly.TryParse(DateBox.Text, culture, DateTimeStyles.AllowWhiteSpaces, out DateOnly date))
            throw new ArgumentException("Укажите настоящую дату выезда в формате день.месяц.год.");
        VisitDocument next = document with
        {
            Details = new VisitDetails(TitleBox.Text, SiteBox.Text, date, AuthorBox.Text,
                EmptyToNull(CustomerBox.Text), EmptyToNull(ReferenceBox.Text))
        };
        int index = ObservationIndex();
        if (index >= 0)
        {
            var current = document.Observations[index];
            var photos = current.Photos;
            if (selectedUse >= 0 && selectedUse < photos.Length)
            {
                var use = photos[selectedUse];
                if (use.Caption != CaptionBox.Text)
                    photos = photos.SetItem(selectedUse, use with { Caption = CaptionBox.Text });
            }
            var edited = current with
            {
                Title = ObservationTitleBox.Text,
                Finding = FindingBox.Text,
                WorkDone = WorkDoneBox.Text,
                Remaining = RemainingBox.Text,
                Status = StatusBox.SelectedValue is ObservationStatus status ? status : current.Status,
                Photos = photos
            };
            if (edited != current) next = next with { Observations = next.Observations.SetItem(index, edited) };
        }
        pendingInput = false;
        if (next != document) SetDocument(next);
        RefreshSaveState();
    }

    private static string? EmptyToNull(string value) => string.IsNullOrEmpty(value) ? null : value;

    private void SetDocument(VisitDocument next)
    {
        if (document is null || next == document) return;
        undo.Add(document);
        if (undo.Count > 32) undo.RemoveAt(0);
        document = next;
        InvalidateDraft();
        RefreshSaveState();
    }

    private void ScheduleSave()
    {
        if (!HasChanges) return;
        autosave.Stop();
        autosave.Start();
    }

    private void InvalidateDraft()
    {
        draft = null;
        preview.Dispose();
        if (!ready) return;
        PdfImage.Source = null;
        PublishButton.IsEnabled = false;
    }

    private int ObservationIndex()
    {
        if (document is null || selectedObservation is null) return -1;
        for (int index = 0; index < document.Observations.Length; index++)
            if (document.Observations[index].Id == selectedObservation) return index;
        return -1;
    }

    private void AddObservationClick(object sender, RoutedEventArgs e)
    {
        if (Busy || document is null || !TryCommitEditors()) return;
        if (document.Observations.Length >= 100) { StatusText.Text = "В одном выезде допускается до 100 узлов."; return; }
        var observation = new Observation(Guid.NewGuid(), "", "", "", "", ObservationStatus.Recorded, []);
        SetDocument(document with { Observations = document.Observations.Add(observation) });
        selectedObservation = observation.Id;
        selectedUse = -1;
        RefreshView();
        ObservationTitleBox.Focus();
        ScheduleSave();
    }

    private void RemoveObservationClick(object sender, RoutedEventArgs e)
    {
        if (Busy || document is null || !TryCommitEditors()) return;
        int index = ObservationIndex();
        if (index < 0) return;
        if (MessageBox.Show(this, "Убрать выбранный узел из отчёта? Фотографии останутся в проекте. Действие можно отменить Ctrl+Z вне текстового поля.",
            "Убрать узел", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        SetDocument(document with { Observations = document.Observations.RemoveAt(index) });
        selectedObservation = document.Observations.ElementAtOrDefault(Math.Min(index, document.Observations.Length - 1))?.Id;
        selectedUse = -1;
        RefreshView();
        ScheduleSave();
    }

    private void MoveObservationUpClick(object sender, RoutedEventArgs e) => MoveObservation(-1);
    private void MoveObservationDownClick(object sender, RoutedEventArgs e) => MoveObservation(1);

    private void MoveObservation(int offset)
    {
        if (Busy || document is null || !TryCommitEditors()) return;
        int index = ObservationIndex();
        int target = index + offset;
        if (index < 0 || target < 0 || target >= document.Observations.Length) return;
        var item = document.Observations[index];
        SetDocument(document with { Observations = document.Observations.RemoveAt(index).Insert(target, item) });
        RefreshView();
        ScheduleSave();
    }

    private void ObservationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || Busy || !ready || ObservationsList.SelectedItem is not ObservationRow row) return;
        if (!TryCommitEditors())
        {
            loading = true;
            try { ObservationsList.SelectedItem = ObservationsList.Items.OfType<ObservationRow>().FirstOrDefault(item => item.Id == selectedObservation); }
            finally { loading = false; }
            return;
        }
        selectedObservation = row.Id;
        selectedUse = -1;
        RefreshView();
        ScheduleSave();
    }

    private void PhotoUseSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || Busy || !ready) return;
        if (!TryCommitEditors())
        {
            loading = true;
            try { UsesList.SelectedItem = UsesList.Items.OfType<UseRow>().FirstOrDefault(item => item.Index == selectedUse); }
            finally { loading = false; }
            return;
        }
        selectedUse = UsesList.SelectedItem is UseRow row ? row.Index : -1;
        RefreshCaption();
        ScheduleSave();
    }

    private void AssignBeforeClick(object sender, RoutedEventArgs e) => AssignPhoto(PhotoRole.Before);
    private void AssignAfterClick(object sender, RoutedEventArgs e) => AssignPhoto(PhotoRole.After);
    private void AssignOverviewClick(object sender, RoutedEventArgs e) => AssignPhoto(PhotoRole.Overview);

    private void AssignPhoto(PhotoRole role)
    {
        if (Busy || document is null || !TryCommitEditors() || AssetsList.SelectedItem is not PhotoAsset asset) return;
        int index = ObservationIndex();
        if (index < 0) { StatusText.Text = "Сначала выберите или добавьте узел слева."; return; }
        var observation = document.Observations[index];
        var existing = observation.Photos.FirstOrDefault(use => use.AssetId == asset.Id);
        if (existing is not null && existing.Role != role && existing.Role != PhotoRole.Overview && role != PhotoRole.Overview)
        { StatusText.Text = "Один и тот же снимок не может быть одновременно «До» и «После»."; return; }
        if (existing?.Role == role) { StatusText.Text = "Этот снимок уже назначен в выбранной роли."; return; }
        if (role != PhotoRole.Overview && observation.Photos.Any(use => use.Role == role)
            && MessageBox.Show(this, $"Заменить снимок «{RoleLabel(role)}» в выбранном узле? Оригинал останется в проекте.",
                "Замена снимка в паре", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var photos = observation.Photos.Where(use => use.AssetId != asset.Id && (role == PhotoRole.Overview || use.Role != role)).ToImmutableArray();
        if (document.Observations.Sum(item => item.Photos.Length) - observation.Photos.Length + photos.Length + 1 > 200)
        { StatusText.Text = "В отчёте допускается до 200 использований фотографий."; return; }
        photos = photos.Add(new PhotoUse(asset.Id, role, existing?.Caption ?? "", existing?.ManualQuarterTurns ?? 0));
        SetDocument(document with { Observations = document.Observations.SetItem(index, observation with { Photos = photos }) });
        selectedUse = photos.Length - 1;
        RefreshView();
        ScheduleSave();
    }

    private void RotatePhotoClick(object sender, RoutedEventArgs e)
    {
        if (Busy || document is null || !TryCommitEditors()) return;
        int index = ObservationIndex();
        if (index < 0 || selectedUse < 0) return;
        var observation = document.Observations[index];
        if (selectedUse >= observation.Photos.Length) return;
        var use = observation.Photos[selectedUse];
        SetDocument(document with { Observations = document.Observations.SetItem(index, observation with
        { Photos = observation.Photos.SetItem(selectedUse, use with { ManualQuarterTurns = (use.ManualQuarterTurns + 1) % 4 }) }) });
        RefreshView();
        ScheduleSave();
    }

    private void RemovePhotoUseClick(object sender, RoutedEventArgs e)
    {
        if (Busy || document is null || !TryCommitEditors()) return;
        int index = ObservationIndex();
        if (index < 0 || selectedUse < 0 || selectedUse >= document.Observations[index].Photos.Length) return;
        var observation = document.Observations[index];
        SetDocument(document with { Observations = document.Observations.SetItem(index,
            observation with { Photos = observation.Photos.RemoveAt(selectedUse) }) });
        selectedUse = -1;
        RefreshView();
        ScheduleSave();
    }

    private void MovePhotoUseUpClick(object sender, RoutedEventArgs e) => MovePhotoUse(-1);
    private void MovePhotoUseDownClick(object sender, RoutedEventArgs e) => MovePhotoUse(1);

    private void MovePhotoUse(int offset)
    {
        if (Busy || document is null || !TryCommitEditors()) return;
        int index = ObservationIndex();
        if (index < 0) return;
        var observation = document.Observations[index];
        int target = selectedUse + offset;
        if (selectedUse < 0 || target < 0 || target >= observation.Photos.Length) return;
        var photo = observation.Photos[selectedUse];
        SetDocument(document with { Observations = document.Observations.SetItem(index,
            observation with { Photos = observation.Photos.RemoveAt(selectedUse).Insert(target, photo) }) });
        selectedUse = target;
        RefreshView();
        ScheduleSave();
    }

    private void RefreshView()
    {
        if (document is null) return;
        loading = true;
        try
        {
            EmptyPane.Visibility = Visibility.Collapsed;
            PreviewPane.Visibility = Visibility.Collapsed;
            EditorPane.Visibility = Visibility.Visible;
            TitleBox.Text = document.Details.Title;
            SiteBox.Text = document.Details.Site;
            AuthorBox.Text = document.Details.Author;
            CustomerBox.Text = document.Details.Customer ?? "";
            ReferenceBox.Text = document.Details.Reference ?? "";
            DateBox.SelectedDate = document.Details.VisitDate.ToDateTime(TimeOnly.MinValue);
            RefreshObservationRows();
            int observationIndex = ObservationIndex();
            bool hasObservation = observationIndex >= 0;
            ObservationEditor.Visibility = hasObservation ? Visibility.Visible : Visibility.Collapsed;
            NoObservationText.Visibility = hasObservation ? Visibility.Collapsed : Visibility.Visible;
            if (hasObservation)
            {
                var observation = document.Observations[observationIndex];
                ObservationTitleBox.Text = observation.Title;
                FindingBox.Text = observation.Finding;
                WorkDoneBox.Text = observation.WorkDone;
                RemainingBox.Text = observation.Remaining;
                StatusBox.SelectedValue = observation.Status;
                var names = document.Assets.ToDictionary(asset => asset.Id, asset => asset.OriginalFileName);
                var uses = observation.Photos.Select((use, index) => new UseRow(index,
                    $"{RoleLabel(use.Role)} - {names.GetValueOrDefault(use.AssetId, "Файл отсутствует в реестре")}")).ToArray();
                UsesList.ItemsSource = uses;
                UsesList.SelectedItem = uses.FirstOrDefault(row => row.Index == selectedUse);
            }
            else { UsesList.ItemsSource = null; selectedUse = -1; }
            if (!shownAssets.Equals(document.Assets))
            {
                shownAssets = document.Assets;
                AssetsList.ItemsSource = shownAssets.ToArray();
            }
            AssetSummary.Text = $"Снимков в проекте: {document.Assets.Length}";
            RefreshCaption();
        }
        finally { loading = false; }
        RefreshButtons();
        _ = RefreshPairAsync();
    }

    private void RefreshCaption()
    {
        bool wasLoading = loading;
        loading = true;
        try
        {
            int index = ObservationIndex();
            var photos = index >= 0 ? document!.Observations[index].Photos : [];
            CaptionBox.IsEnabled = selectedUse >= 0 && selectedUse < photos.Length;
            CaptionBox.Text = CaptionBox.IsEnabled ? photos[selectedUse].Caption : "";
        }
        finally { loading = wasLoading; }
    }

    private void RefreshObservationRows()
    {
        if (document is null) return;
        bool wasLoading = loading;
        loading = true;
        try
        {
            var rows = document.Observations.Select((item, index) => new ObservationRow(item.Id, $"{index + 1:00}",
                string.IsNullOrWhiteSpace(item.Title) ? "Узел без названия" : item.Title, StatusLabel(item.Status))).ToArray();
            ObservationsList.ItemsSource = rows;
            ObservationsList.SelectedItem = rows.FirstOrDefault(row => row.Id == selectedObservation);
        }
        finally { loading = wasLoading; }
    }

    private async Task RefreshPairAsync()
    {
        int generation = ++pairGeneration;
        BeforeImage.Source = null;
        AfterImage.Source = null;
        int index = ObservationIndex();
        if (project is null || document is null || index < 0) return;
        var snapshot = project;
        var photos = document.Observations[index].Photos;
        await LoadPairPhoto(photos.FirstOrDefault(use => use.Role == PhotoRole.Before), BeforeImage, BeforeEmpty, BeforeCaption, "до");
        await LoadPairPhoto(photos.FirstOrDefault(use => use.Role == PhotoRole.After), AfterImage, AfterEmpty, AfterCaption, "после");

        async Task LoadPairPhoto(PhotoUse? use, Image image, TextBlock empty, TextBlock caption, string label)
        {
            if (generation != pairGeneration) return;
            empty.Text = use is null ? $"Снимок {label} не добавлен" : "Загрузка снимка...";
            empty.Visibility = Visibility.Visible;
            caption.Text = use?.Caption ?? "";
            if (use is null) return;
            try
            {
                var bitmap = await VisitPhotos.LoadPreviewAsync(snapshot, use.AssetId, use.ManualQuarterTurns, 800, imageWork.Token);
                if (generation != pairGeneration) return;
                image.Source = bitmap;
                empty.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (Expected(error))
            {
                if (generation == pairGeneration) { empty.Text = "Снимок не удалось прочитать"; StatusText.Text = error.Message; }
            }
        }
    }

    private async void ThumbnailLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image || image.DataContext is not PhotoAsset asset || project is null) return;
        var snapshot = project;
        var token = imageWork.Token;
        bool entered = false;
        try
        {
            await thumbnailSlots.WaitAsync(token);
            entered = true;
            if (!image.IsLoaded || !ReferenceEquals(image.DataContext, asset)) return;
            var thumbnail = await VisitPhotos.LoadPreviewAsync(snapshot, asset.Id, 0, 320, token);
            if (image.IsLoaded && ReferenceEquals(image.DataContext, asset) && !token.IsCancellationRequested)
                image.Source = thumbnail;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (Expected(error))
        {
            if (image.IsLoaded && ReferenceEquals(image.DataContext, asset)) image.ToolTip = error.Message;
        }
        finally { if (entered) thumbnailSlots.Release(); }
    }

    private void ThumbnailUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image) { image.Source = null; image.ToolTip = null; }
    }

    private void RefreshButtons()
    {
        if (!ready) return;
        bool editing = project is not null && PreviewPane.Visibility != Visibility.Visible;
        SaveButton.IsEnabled = editing && HasChanges;
        ImportButton.IsEnabled = editing;
        PrepareButton.IsEnabled = editing && document!.Observations.Length > 0;
        PublishButton.IsEnabled = draft is not null && !HasChanges;
        RefreshSaveState();
    }

    private void RefreshSaveState()
    {
        if (!ready) return;
        SaveStateText.Text = project is null ? "" : HasChanges ? saveFailed
            ? "Изменения не сохранены. Исправьте причину и нажмите «Сохранить»."
            : "Есть несохранённые изменения. Автосохранение после паузы."
            : $"Сохранено, редакция {project.Document.Revision}. Папка: {project.DirectoryPath}";
        SaveButton.IsEnabled = project is not null && HasChanges && PreviewPane.Visibility != Visibility.Visible;
    }

    private IProgress<VisitProgress> Progress()
    {
        var current = operation;
        return new Progress<VisitProgress>(value =>
        {
            if (current is null || !ReferenceEquals(operation, current)) return;
            string phase = value.Phase switch
            {
                VisitPhase.Importing => "Копирование оригиналов",
                VisitPhase.Normalizing => "Подготовка фотографий",
                VisitPhase.Paginating => "Верстка PDF",
                VisitPhase.Verifying => "Проверка результата",
                VisitPhase.Publishing => "Сохранение PDF",
                _ => "Обработка"
            };
            StatusText.Text = value.Total is int total
                ? $"{phase}: {value.Completed} / {total}. {value.CurrentFileName}" : phase;
        });
    }

    private async Task<bool> RunOperation(Func<CancellationToken, Task> action, bool showErrorDialog = true)
    {
        if (Busy) return false;
        autosave.Stop();
        using var current = new CancellationTokenSource();
        operation = current;
        CommandsPanel.IsEnabled = false;
        Workspace.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        StatusText.Text = "Обработка...";
        try
        {
            if (backgroundSave is Task saving) await saving;
            current.Token.ThrowIfCancellationRequested();
            await action(current.Token);
            return true;
        }
        catch (OperationCanceledException) { StatusText.Text = "Операция отменена. Готовый результат не опубликован; промежуточные файлы могут остаться в рабочей папке."; return false; }
        catch (Exception error) when (Expected(error))
        {
            if (HasChanges) saveFailed = true;
            string recovery = string.Join(Environment.NewLine, new[] { "PartialPath", "DraftDirectory" }
                .Where(error.Data.Contains).Select(key => $"Материалы для восстановления: {error.Data[key]}"));
            string message = string.IsNullOrEmpty(recovery) ? error.Message : error.Message + Environment.NewLine + recovery;
            StatusText.Text = message;
            if (showErrorDialog) ShowText(message, "Операция не завершена");
            return false;
        }
        finally
        {
            operation = null;
            CommandsPanel.IsEnabled = true;
            Workspace.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            RefreshButtons();
        }
    }

    private static bool Expected(Exception error) => error is IOException or InvalidDataException
        or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Text.Json.JsonException
        or COMException or FormatException or OverflowException or System.Security.SecurityException;

    private void ShowIssues(ImmutableArray<VisitIssue> issues, string title)
    {
        if (issues.IsDefaultOrEmpty) return;
        string text = string.Join(Environment.NewLine, issues.Select(issue =>
        {
            string context = string.IsNullOrEmpty(issue.FileName) ? "" : issue.FileName + ": ";
            if (issue.ObservationId is Guid id && document is not null)
            {
                for (int index = 0; index < document.Observations.Length; index++)
                    if (document.Observations[index].Id == id) { context = $"Узел {index + 1:00}: " + context; break; }
            }
            return context + issue.Message;
        }));
        StatusText.Text = $"Замечаний: {issues.Length}. {issues[0].Message}";
        ShowText(text, title);
    }

    private void ShowText(string text, string title)
    {
        var content = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 16)
        };
        var close = new Button { Content = "Закрыть", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new DockPanel { Margin = new Thickness(24) };
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(close);
        panel.Children.Add(content);
        var dialog = new Window
        {
            Owner = this, Title = title, Width = 700, Height = 460, MinWidth = 420, MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel,
            Style = (Style)FindResource(typeof(Window))
        };
        close.Click += (_, _) => dialog.Close();
        dialog.ShowDialog();
    }

    private static string RoleLabel(PhotoRole role) => role switch
    { PhotoRole.Before => "До", PhotoRole.After => "После", PhotoRole.Overview => "Общий вид", _ => "Неизвестная роль" };

    private static string StatusLabel(ObservationStatus status) => status switch
    { ObservationStatus.Recorded => "Зафиксировано", ObservationStatus.Completed => "Выполнено", ObservationStatus.FollowUp => "Нужен повторный выезд", _ => "Неизвестный статус" };

    private async void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (Busy) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        { e.Handled = true; await RunOperation(SaveCurrentAsync); }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z && Keyboard.FocusedElement is not TextBoxBase)
        {
            e.Handled = true;
            if (document is null || project is null || undo.Count == 0) return;
            document = undo[^1] with { Revision = project.Document.Revision, Assets = project.Document.Assets };
            undo.RemoveAt(undo.Count - 1);
            pendingInput = false;
            selectedUse = -1;
            InvalidateDraft();
            RefreshView();
            ScheduleSave();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && PreviewPane.Visibility == Visibility.Visible && e.Key is Key.Left or Key.Right)
        { e.Handled = true; await ShowPageAsync(pageIndex + (e.Key == Key.Left ? -1 : 1)); }
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        operation?.Cancel();
        StatusText.Text = "Отмена запрошена. Текущая библиотечная стадия может завершаться некоторое время.";
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        if (Busy) { e.Cancel = true; CancelClick(this, new RoutedEventArgs()); return; }
        if (!HasChanges && backgroundSave is null) return;
        e.Cancel = true;
        if (await CanSwitchAsync())
        {
            allowClose = true;
            // A synchronous discard decision still runs inside Closing; defer the second close.
            await Dispatcher.InvokeAsync(Close);
        }
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        autosave.Stop();
        autosave.Tick -= AutosaveTick;
        imageWork.Cancel();
        preview.Dispose();
    }

    private sealed record ObservationRow(Guid Id, string NumberLabel, string Title, string StatusLabel);
    private sealed record UseRow(int Index, string Label);
}
