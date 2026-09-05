using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Visitplate.Core;

namespace Visitplate.Core.Tests;

internal sealed class FileFixture : IDisposable
{
    private readonly string fixtureBase;
    private readonly List<string> junctions = [];

    internal FileFixture(bool ntfs = false)
    {
        fixtureBase = Path.GetFullPath(ntfs
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "visitplate-reparse-tests")
            : Environment.GetEnvironmentVariable("VISITPLATE_TEST_ROOT") ?? Path.Combine(Path.GetTempPath(), "visitplate-tests"));
        Directory.CreateDirectory(fixtureBase);
        Root = Path.Combine(fixtureBase, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    internal string Root { get; }
    internal string PathFor(string name) => Path.Combine(Root, name);

    internal string Write(string name, byte[] bytes)
    {
        string path = PathFor(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        return path;
    }

    internal static PhotoAsset WriteAsset(VisitProject project, byte[]? bytes = null, Guid? id = null)
    {
        bytes ??= [0xff, 0xd8, 0xff, 0xd9];
        var asset = new PhotoAsset(id ?? Guid.NewGuid(), PhotoFormat.Jpeg, "снимок.jpg", bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), 1, 1, null);
        using var stream = new FileStream(Path.Combine(project.DirectoryPath, asset.RelativePath),
            FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        return asset;
    }

    internal async Task<string> CreateJunctionAsync(string name, string target)
    {
        string link = PathFor(name);
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new IOException("Не удалось запустить создание тестовой junction.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, $"{await output}\n{await error}");
        Assert.IsTrue((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        junctions.Add(link);
        return link;
    }

    internal static (string Hash, long Length, DateTime Modified, FileAttributes Attributes) Snapshot(string path)
    {
        using var stream = File.OpenRead(path);
        return (Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length,
            File.GetLastWriteTimeUtc(path), File.GetAttributes(path));
    }

    public void Dispose()
    {
        string resolved = Path.GetFullPath(Root);
        if (!string.Equals(Path.GetDirectoryName(resolved), fixtureBase, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
            throw new InvalidOperationException("Отказ от удаления папки вне принадлежащей тесту области.");
        foreach (string link in junctions)
        {
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(link)), resolved, StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0)
                throw new InvalidOperationException("Тестовая junction больше не является ожидаемой ссылкой.");
            Directory.Delete(link, recursive: false);
        }
        if ((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Корень тестовой папки заменён ссылкой.");
        Directory.Delete(resolved, recursive: true);
    }
}
