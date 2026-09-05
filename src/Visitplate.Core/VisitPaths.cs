using System.IO;

namespace Visitplate.Core;

internal static class VisitPaths
{
    private const FileAttributes UnavailableAttributes = FileAttributes.ReparsePoint | FileAttributes.Offline
        | (FileAttributes)0x40000 | (FileAttributes)0x400000;

    internal static string RequireLocalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // Reject network/device grammar before asking the filesystem about any ancestor.
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':'
            || path[2] is not ('\\' or '/') || path.AsSpan(2).Contains(':')
            || path.Any(char.IsControl))
            throw new ArgumentException("Требуется абсолютный путь на локальном диске.", nameof(path));

        foreach (string component in path[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or ".." || component.EndsWith(' ') || component.EndsWith('.')
                || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(component))
                throw new ArgumentException("Путь содержит неоднозначное или недопустимое имя.", nameof(path));
        }

        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string root = Path.GetPathRoot(fullPath)!;
        DriveType driveType = new DriveInfo(root).DriveType;
        if (driveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram or DriveType.CDRom))
            throw new IOException("Сетевой или недоступный диск не поддерживается.");

        var ancestors = new Stack<string>();
        string? current = fullPath;
        while (current is not null)
        {
            ancestors.Push(current);
            current = Path.GetDirectoryName(current);
        }

        while (ancestors.TryPop(out string? ancestor))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(ancestor); }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
            if ((attributes & UnavailableAttributes) != 0)
                throw new IOException($"Путь содержит ссылку или недоступный локально объект: {ancestor}");
            if (ancestors.Count > 0 && (attributes & FileAttributes.Directory) == 0)
                throw new IOException($"Родитель пути не является папкой: {ancestor}");
        }

        return fullPath;
    }

    internal static string AssetPath(VisitProject project, PhotoAsset asset)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Id == Guid.Empty || !Enum.IsDefined(asset.Format))
            throw new ArgumentException("Недопустимый идентификатор или формат снимка.", nameof(asset));
        return RequireLocalPath(Path.Combine(project.DirectoryPath, asset.RelativePath));
    }

    internal static bool IsWithin(string directory, string path)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string candidate = Path.GetFullPath(path);
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeviceName(string name)
    {
        string stem = name.Split('.')[0].TrimEnd(' ');
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³'));
    }
}
