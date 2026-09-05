using System.IO;
using System.Reflection;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MigraDoc;
using PdfSharp.Drawing;
using PdfSharp.Events;
using PdfSharp.Fonts;
using PdfSharp.Logging;
using PdfSharp.Pdf;

namespace Visitplate.Core;

internal sealed class ReportFonts : IFontResolver
{
    internal const string Family = "Noto Sans";
    internal const string DiagnosticFamily = "Visitplate Diagnostic";
    private static bool initialized;
    private static readonly RenderLog Diagnostics = new();

    // The report semaphore serializes initialization and every PDFsharp font operation.
    internal static void Initialize()
    {
        if (initialized) return;
        GlobalFontSettings.DefaultFontEncoding = PdfFontEncoding.Unicode;
        GlobalFontSettings.FontResolver = new ReportFonts();
        PredefinedFontsAndChars.ErrorFontName = DiagnosticFamily;
        LogHost.Factory = Diagnostics;
        // Native Windows PDF preview misrenders this Noto subset at small sizes; full faces are verified.
        var options = new XPdfFontOptions(PdfFontEncoding.Unicode, PdfFontEmbedding.EmbedCompleteFontFile);
        _ = new XFont(Family, 10.5, XFontStyleEx.Regular, options);
        _ = new XFont(Family, 10.5, XFontStyleEx.Bold, options);
        initialized = true;
    }

    internal static void BeginRender() => Diagnostics.Entries.Clear();

    internal static void RequireCleanRender()
    {
        if (Diagnostics.Entries.Count != 0)
            throw new InvalidDataException("Библиотека PDF сообщила об ошибке: " + string.Join("; ", Diagnostics.Entries));
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        if (familyName is not (Family or DiagnosticFamily) || italic)
            throw new InvalidDataException($"Шрифт не входит в комплект отчёта: {familyName}.");
        return new FontResolverInfo(bold ? "NotoSans-Bold" : "NotoSans-Regular");
    }

    public byte[] GetFont(string faceName)
    {
        if (faceName is not ("NotoSans-Regular" or "NotoSans-Bold"))
            throw new InvalidDataException("Недопустимое начертание шрифта.");
        using Stream resource = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"Visitplate.Core.fonts.{faceName}.ttf")
            ?? throw new InvalidDataException($"В комплекте приложения отсутствует шрифт {faceName}.");
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        return bytes.ToArray();
    }

    internal static void RequireSupportedText(object? sender, RenderTextEventArgs args)
    {
        // MigraDoc catches late image IO errors and draws this font without logging a warning.
        if (args.Font.FontFamily.Name == DiagnosticFamily)
            throw new InvalidDataException("Фотография недоступна при рисовании PDF. Черновик не подготовлен.");
        foreach (var pair in args.CodePointGlyphIndexPairs)
            if (pair.GlyphIndex == 0)
                throw new InvalidDataException($"Шрифт отчёта не содержит символ U+{pair.CodePoint:X4}. Измените текст.");
    }

    private sealed class RenderLog : ILoggerFactory, ILogger
    {
        private bool disposed;
        internal HashSet<string> Entries { get; } = new(StringComparer.Ordinal);
        public ILogger CreateLogger(string categoryName) => !disposed ? this : throw new ObjectDisposedException(nameof(RenderLog));
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException("PDF diagnostics use the report-owned logger.");
        public void Dispose() => disposed = true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => !disposed && level >= LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            string message = formatter(state, exception);
            // 6.2.4 reports its documented decision to retain a previously requested full face as Error.
            if (exception is null && message == "Font embedding option was already set to EmbedCompleteFontFile. Setting to TryComputeSubset is ignored.")
                Trace.WriteLine("Visitplate full-font policy: " + message);
            else
                Entries.Add(string.IsNullOrWhiteSpace(message) ? $"PDF diagnostic {level}/{eventId.Id}." : message);
        }
    }
}
