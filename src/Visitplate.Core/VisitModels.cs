using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("Visitplate.Core.Tests")]

namespace Visitplate.Core;

public sealed record VisitDetails(string Title, string Site, DateOnly VisitDate, string Author,
    string? Customer = null, string? Reference = null);

public enum PhotoFormat { Jpeg, Png }

public sealed record PhotoAsset(Guid Id, PhotoFormat Format, string OriginalFileName, long Length,
    string Sha256, int PixelWidth, int PixelHeight, int? ExifOrientation)
{
    [JsonIgnore]
    public string RelativePath => $"originals/{Id:N}.{(Format == PhotoFormat.Jpeg ? "jpg" : "png")}";
}

public enum PhotoRole { Before, After, Overview }

public sealed record PhotoUse(Guid AssetId, PhotoRole Role, string Caption, int ManualQuarterTurns = 0);

public enum ObservationStatus { Recorded, Completed, FollowUp }

public sealed record Observation(Guid Id, string Title, string Finding, string WorkDone, string Remaining,
    ObservationStatus Status, ImmutableArray<PhotoUse> Photos);

public sealed record VisitDocument(int SchemaVersion, Guid Id, long Revision, VisitDetails Details,
    ImmutableArray<PhotoAsset> Assets, ImmutableArray<Observation> Observations);

public sealed class VisitProject
{
    internal VisitProject(string directoryPath, VisitDocument document, string documentFingerprint)
    {
        DirectoryPath = directoryPath;
        Document = document;
        DocumentFingerprint = documentFingerprint;
    }

    public string DirectoryPath { get; }
    public VisitDocument Document { get; }
    public string DocumentFingerprint { get; }
}

public enum VisitIssueSeverity { Warning, Error }

public sealed record VisitIssue(VisitIssueSeverity Severity, string Code, string Message,
    Guid? ObservationId = null, Guid? AssetId = null, string? FileName = null);

public enum VisitPhase { Importing, Normalizing, Paginating, Verifying, Publishing }

public sealed record VisitProgress(VisitPhase Phase, int Completed, int? Total = null, string? CurrentFileName = null);

public sealed record ImportResult(VisitProject Project, ImmutableArray<VisitIssue> Issues);

public sealed class ReportDraft
{
    internal ReportDraft(string path, Guid projectId, long revision, string documentFingerprint,
        string sha256, int pageCount, int photoCount, ImmutableArray<VisitIssue> warnings)
    {
        Path = path;
        ProjectId = projectId;
        Revision = revision;
        DocumentFingerprint = documentFingerprint;
        Sha256 = sha256;
        PageCount = pageCount;
        PhotoCount = photoCount;
        Warnings = warnings;
    }

    public string Path { get; }
    public Guid ProjectId { get; }
    public long Revision { get; }
    public string DocumentFingerprint { get; }
    public string Sha256 { get; }
    public int PageCount { get; }
    public int PhotoCount { get; }
    public ImmutableArray<VisitIssue> Warnings { get; }
}
