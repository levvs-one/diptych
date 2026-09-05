using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFiumCore;
using Visitplate.Core;

namespace Visitplate.App;

internal sealed class PdfPreview : IDisposable
{
    private static readonly object PdfiumGate = new();
    private FileStream? input;
    private byte[]? pdfBytes;

    public int PageCount { get; private set; }

    public async Task OpenAsync(ReportDraft draft, CancellationToken cancellationToken)
    {
        Dispose();
        try
        {
            input = new FileStream(draft.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous);
            if (input.Length is <= 0 or > 128L * 1024 * 1024)
                throw new InvalidDataException("PDF пуст или превышает предел просмотра 128 МиБ.");
            byte[] bytes = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(bytes, cancellationToken);
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!hash.Equals(draft.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Подготовленный PDF изменился. Подготовьте его заново.");
            // Render only the verified bytes; retain the read lock until this preview is closed.
            int pageCount = await Task.Run(() => WithDocument(bytes, cancellationToken,
                fpdfview.FPDF_GetPageCount), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (pageCount != draft.PageCount || pageCount is < 1 or > 150)
                throw new InvalidDataException("Число страниц PDF не совпало с подготовленным документом.");
            pdfBytes = bytes;
            PageCount = pageCount;
        }
        catch (Exception error) when (RendererFailure(error))
        {
            Dispose();
            throw new InvalidDataException("Не удалось открыть подготовленный PDF в движке просмотра. " + error.Message, error);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public async Task<BitmapSource> RenderAsync(int pageIndex, CancellationToken cancellationToken)
    {
        if (pdfBytes is not { } bytes || pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            BitmapSource image = await Task.Run(() => WithDocument(bytes, cancellationToken,
                document => RenderPage(document, pageIndex, cancellationToken)), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return image;
        }
        catch (Exception error) when (RendererFailure(error))
        {
            throw new InvalidDataException("Не удалось отобразить страницу PDF. " + error.Message, error);
        }
    }

    private static T WithDocument<T>(byte[] bytes, CancellationToken cancellationToken, Func<FpdfDocumentT, T> action)
    {
        // PDFium owns process-wide state. No native handle survives this lock or crosses an await.
        lock (PdfiumGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fpdfview.FPDF_InitLibrary();
            try
            {
                GCHandle pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                try
                {
                    FpdfDocumentT document = fpdfview.FPDF_LoadMemDocument64(pin.AddrOfPinnedObject(), (ulong)bytes.Length, null)
                        ?? throw new InvalidDataException($"Не удалось открыть PDF (код {fpdfview.FPDF_GetLastError()}).");
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return action(document);
                    }
                    finally { fpdfview.FPDF_CloseDocument(document); }
                }
                finally { pin.Free(); }
            }
            finally { fpdfview.FPDF_DestroyLibrary(); }
        }
    }

    private static BitmapSource RenderPage(FpdfDocumentT document, int index, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FpdfPageT page = fpdfview.FPDF_LoadPage(document, index)
            ?? throw new InvalidDataException($"Не удалось открыть страницу {index + 1} (код {fpdfview.FPDF_GetLastError()}).");
        try
        {
            float pointsWidth = fpdfview.FPDF_GetPageWidthF(page);
            float pointsHeight = fpdfview.FPDF_GetPageHeightF(page);
            if (!float.IsFinite(pointsWidth) || !float.IsFinite(pointsHeight) || pointsWidth <= 0 || pointsHeight <= 0)
                throw new InvalidDataException("У страницы PDF некорректные размеры.");
            double scale = 2048d / Math.Max(pointsWidth, pointsHeight);
            int width = Math.Clamp((int)Math.Round(pointsWidth * scale), 1, 2048);
            int height = Math.Clamp((int)Math.Round(pointsHeight * scale), 1, 2048);
            cancellationToken.ThrowIfCancellationRequested();
            FpdfBitmapT bitmap = fpdfview.FPDFBitmapCreateEx(width, height, (int)FPDFBitmapFormat.BGRA,
                IntPtr.Zero, checked(width * 4)) ?? throw new InvalidDataException("Не удалось выделить изображение страницы PDF.");
            try
            {
                if (fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, width, height, 0xffffffff) == 0)
                    throw new InvalidDataException("Не удалось подготовить фон страницы PDF.");
                fpdfview.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0,
                    (int)(RenderFlags.NoNativeText | RenderFlags.LimitImageCacheSize));
                // The native call is not interruptible. Discard late pixels before they reach the window.
                cancellationToken.ThrowIfCancellationRequested();
                int stride = fpdfview.FPDFBitmapGetStride(bitmap);
                IntPtr pointer = fpdfview.FPDFBitmapGetBuffer(bitmap);
                if (stride != checked(width * 4) || pointer == IntPtr.Zero)
                    throw new InvalidDataException("Движок просмотра вернул некорректное изображение страницы.");
                byte[] pixels = new byte[checked(stride * height)];
                Marshal.Copy(pointer, pixels, 0, pixels.Length);
                BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
                image.Freeze();
                return image;
            }
            finally { fpdfview.FPDFBitmapDestroy(bitmap); }
        }
        finally { fpdfview.FPDF_ClosePage(page); }
    }

    private static bool RendererFailure(Exception error) => error is DllNotFoundException or EntryPointNotFoundException
        or BadImageFormatException || error is TypeInitializationException { InnerException: { } inner } && RendererFailure(inner);

    public void Dispose()
    {
        PageCount = 0;
        input?.Dispose();
        input = null;
        pdfBytes = null;
    }
}
