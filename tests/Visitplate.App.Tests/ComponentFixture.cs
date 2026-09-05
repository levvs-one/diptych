using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Visitplate.Core;

[assembly: DoNotParallelize]

namespace Visitplate.App.Tests;

[TestClass]
public sealed class ApplicationLifetime
{
    private static readonly TaskCompletionSource<Dispatcher> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Thread? applicationThread;
    private static string baselineRoot = "";
    internal static MainWindow Window { get; private set; } = null!;
    internal static VisitProject EmptyProject { get; private set; } = null!;

    [AssemblyInitialize]
    public static async Task Initialize(TestContext _)
    {
        var thread = new Thread(() =>
        {
            try
            {
                var application = new Visitplate.App.App();
                application.InitializeComponent();
                // Load production resources without the XAML StartupUri opening a desktop window.
                application.Navigating += (_, navigation) => navigation.Cancel = true;
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                Started.SetResult(Dispatcher.CurrentDispatcher);
                application.Run();
                Stopped.SetResult();
            }
            catch (Exception exception)
            {
                Started.TrySetException(exception);
                Stopped.TrySetException(exception);
                throw;
            }
        }) { IsBackground = true, Name = "Visitplate component tests" };
        thread.SetApartmentState(ApartmentState.STA);
        applicationThread = thread;
        thread.Start();
        Dispatcher dispatcher = await Started.Task;
        await dispatcher.InvokeAsync(async () =>
        {
            string fixtureBase = ComponentFixture.GetFixtureBase();
            Directory.CreateDirectory(fixtureBase);
            baselineRoot = Path.Combine(fixtureBase, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(baselineRoot);
            EmptyProject = await VisitProjects.CreateAsync(Path.Combine(baselineRoot, "empty"),
                new VisitDetails("", "", new DateOnly(2026, 9, 5), ""));
            Window = new MainWindow();
        }).Task.Unwrap();
    }

    internal static async Task RunAsync(Func<Task> test)
    {
        Dispatcher dispatcher = await Started.Task;
        await dispatcher.InvokeAsync(async () =>
        {
            Assert.IsFalse(Application.Current.Windows.Cast<Window>().Any(window => window.IsVisible));
            await test();
            Assert.IsFalse(Application.Current.Windows.Cast<Window>().Any(window => window.IsVisible));
        }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(90));
    }

    [AssemblyCleanup]
    public static async Task Cleanup()
    {
        Dispatcher dispatcher = await Started.Task;
        await dispatcher.InvokeAsync(() =>
        {
            bool closed = false;
            Window.Closed += (_, _) => closed = true;
            Window.Close();
            Assert.IsTrue(closed, "Единственное окно закрывается через настоящий production Closing.");
            var imageWork = (CancellationTokenSource)typeof(MainWindow)
                .GetField("imageWork", ComponentFixture.Instance)!.GetValue(Window)!;
            var autosave = (DispatcherTimer)typeof(MainWindow)
                .GetField("autosave", ComponentFixture.Instance)!.GetValue(Window)!;
            Assert.IsTrue(imageWork.IsCancellationRequested);
            Assert.IsFalse(autosave.IsEnabled);
            Application.Current.Shutdown();
        });
        await Stopped.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.IsTrue(await Task.Run(() => applicationThread!.Join(TimeSpan.FromSeconds(15))),
            "STA должен полностью завершиться, включая освобождение нативной COM apartment.");
        Assert.IsTrue(dispatcher.HasShutdownFinished);
        ComponentFixture.DeleteOwnedRoot(baselineRoot, ComponentFixture.GetFixtureBase());
    }
}

internal sealed class ComponentFixture : IAsyncDisposable
{
    internal const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly string fixtureBase;
    private readonly TimeSpan originalAutosaveInterval;

    private ComponentFixture(string fixtureBase, string root, MainWindow window)
    {
        this.fixtureBase = fixtureBase;
        Root = root;
        Window = window;
        originalAutosaveInterval = Autosave.Interval;
    }

    internal string Root { get; }
    internal MainWindow Window { get; }
    internal VisitProject Project => Get<VisitProject>("project");
    internal VisitDocument Document => Get<VisitDocument>("document");
    internal DispatcherTimer Autosave => Get<DispatcherTimer>("autosave");

    internal static string GetFixtureBase() => Path.GetFullPath(Environment.GetEnvironmentVariable("VISITPLATE_APP_TEST_ROOT")
        ?? Path.Combine(Environment.GetEnvironmentVariable("VISITPLATE_TEST_ROOT") ?? Path.GetTempPath(), "Visitplate.App.Tests"));

    internal static async Task<ComponentFixture> CreateAsync(bool photos = false, int observationCount = 2)
    {
        string fixtureBase = GetFixtureBase();
        Directory.CreateDirectory(fixtureBase);
        string root = Path.Combine(fixtureBase, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        VisitProject project = await VisitProjects.CreateAsync(Path.Combine(root, "project"),
            new VisitDetails("Проверка выезда", "Насосная № 2", new DateOnly(2026, 9, 5), "Тестовая программа"));
        if (photos)
        {
            string first = WritePng(root, "before.png", 20);
            string second = WritePng(root, "after.png", 220);
            ImportResult imported = await VisitPhotos.ImportAsync(project, [first, second]);
            Assert.IsFalse(imported.Issues.Any(issue => issue.Severity == VisitIssueSeverity.Error));
            project = imported.Project;
        }
        ImmutableArray<PhotoUse> uses = photos
            ? [new PhotoUse(project.Document.Assets[0].Id, PhotoRole.Before, "До работ"),
                new PhotoUse(project.Document.Assets[1].Id, PhotoRole.After, "После работ")]
            : [];
        var observations = Enumerable.Range(0, observationCount).Select(index => new Observation(Guid.NewGuid(),
            $"Узел {index + 1}", $"Наблюдение {index + 1}", "Проверено", "", ObservationStatus.Recorded,
            index == 0 ? uses : [])).ToImmutableArray();
        project = await VisitProjects.SaveAsync(project, project.Document with { Observations = observations });
        MainWindow window = ApplicationLifetime.Window;
        var fixture = new ComponentFixture(fixtureBase, root, window);
        fixture.Call("LoadProject", project);
        Assert.IsFalse(window.IsVisible);
        return fixture;
    }

    private static string WritePng(string directory, string name, byte red)
    {
        var pixels = new byte[12 * 8 * 3];
        for (int index = 0; index < pixels.Length; index += 3)
        {
            pixels[index] = 40;
            pixels[index + 1] = 100;
            pixels[index + 2] = red;
        }
        BitmapSource bitmap = BitmapSource.Create(12, 8, 96, 96, PixelFormats.Bgr24, null, pixels, 36);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string path = Path.Combine(directory, name);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(output);
        return path;
    }

    internal T Control<T>(string name) where T : FrameworkElement => (T)Window.FindName(name);
    internal T Get<T>(string name) => (T)typeof(MainWindow).GetField(name, Instance)!.GetValue(Window)!;
    internal void Set(string name, object? value) => typeof(MainWindow).GetField(name, Instance)!.SetValue(Window, value);
    internal object? Call(string name, params object?[] arguments)
    {
        try { return typeof(MainWindow).GetMethod(name, Instance)!.Invoke(Window, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        { ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
    }
    internal Task SaveAsync() => (Task)Call("SaveCurrentAsync", CancellationToken.None)!;
    internal Task<bool> RunOperationAsync(Func<CancellationToken, Task> action) => (Task<bool>)Call("RunOperation", action, false)!;

    public async ValueTask DisposeAsync()
    {
        Autosave.Stop();
        if (Get<Task?>("backgroundSave") is Task saving) await saving;
        Assert.IsNull(Get<object?>("operation"), "Незавершённая операция не должна пережить свой fixture.");
        Autosave.Stop();
        Autosave.Interval = originalAutosaveInterval;
        // The real app switches projects inside one MainWindow; use the production reset path.
        Call("LoadProject", ApplicationLifetime.EmptyProject);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        DeleteOwnedRoot(Root, fixtureBase);
    }

    internal static void DeleteOwnedRoot(string root, string fixtureBase)
    {
        string resolved = Path.GetFullPath(root);
        if (!string.Equals(Path.GetDirectoryName(resolved), fixtureBase, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Отказ от удаления чужой тестовой папки.");
        Directory.Delete(resolved, recursive: true);
    }
}
