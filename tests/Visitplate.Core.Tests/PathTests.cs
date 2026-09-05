using System.IO;
using Visitplate.Core;

namespace Visitplate.Core.Tests;

[TestClass]
public sealed class PathTests
{
    [TestMethod]
    [DataRow(@"\\server\share\project")]
    [DataRow(@"\\?\C:\project")]
    [DataRow(@"\\.\C:\project")]
    [DataRow("file:///C:/project")]
    [DataRow("https://example.invalid/project")]
    [DataRow(@"C:project")]
    [DataRow(@"\project")]
    [DataRow("project")]
    [DataRow(@"C:\a\..\b")]
    [DataRow(@"C:\a\.\b")]
    [DataRow(@"C:\file:stream")]
    [DataRow(@"C:\CON.txt")]
    [DataRow(@"C:\COM1 .txt")]
    [DataRow(@"C:\LPT1 .txt")]
    [DataRow(@"C:\AUX")]
    [DataRow(@"C:\CONIN$")]
    [DataRow(@"C:\CONOUT$")]
    [DataRow(@"C:\LPT².txt")]
    [DataRow(@"C:\name.")]
    [DataRow("C:\\name ")]
    [DataRow("C:\\bad\u0001name")]
    public void UnsafeGrammarIsRejectedBeforeFileAccess(string path)
    {
        Assert.ThrowsExactly<ArgumentException>(() => VisitPaths.RequireLocalPath(path));
    }

    [TestMethod]
    public void ExistingAndNewLocalPathsAreAccepted()
    {
        using var fixture = new FileFixture();
        string file = fixture.Write("local.txt", [1, 2, 3]);
        Assert.AreEqual(file, VisitPaths.RequireLocalPath(file));
        string newPath = fixture.PathFor("new\\project");
        Assert.AreEqual(newPath, VisitPaths.RequireLocalPath(newPath));
    }

    [TestMethod]
    public void ContainmentChecksDirectoryBoundariesAndVolumeRoot()
    {
        Assert.IsTrue(VisitPaths.IsWithin(@"F:\project", @"F:\project\originals\one.jpg"));
        Assert.IsTrue(VisitPaths.IsWithin(@"F:\project", @"f:\PROJECT"));
        Assert.IsFalse(VisitPaths.IsWithin(@"F:\project", @"F:\project-other\one.jpg"));
        Assert.IsFalse(VisitPaths.IsWithin(@"F:\project", @"F:\elsewhere\one.jpg"));
        Assert.IsTrue(VisitPaths.IsWithin(@"F:\", @"F:\project"));
    }

    [TestMethod]
    public async Task JunctionAndDirectDescendantAreRejectedWithoutFollowingTarget()
    {
        using var fixture = new FileFixture(ntfs: true);
        string target = fixture.PathFor("target");
        Directory.CreateDirectory(target);
        string source = fixture.Write("target\\original.txt", [3, 2, 1]);
        var before = FileFixture.Snapshot(source);
        string link = await fixture.CreateJunctionAsync("link", target);
        Assert.ThrowsExactly<IOException>(() => VisitPaths.RequireLocalPath(link));
        Assert.ThrowsExactly<IOException>(() => VisitPaths.RequireLocalPath(Path.Combine(link, "original.txt")));
        Assert.ThrowsExactly<IOException>(() => VisitPaths.RequireLocalPath(Path.Combine(link, "missing", "new.txt")));
        Assert.AreEqual(before, FileFixture.Snapshot(source));
    }

    [TestMethod]
    public async Task ProjectThroughJunctionIsRejected()
    {
        using var fixture = new FileFixture(ntfs: true);
        VisitProject project = await VisitProjects.CreateAsync(fixture.PathFor("project"), ProjectTests.Details);
        string link = await fixture.CreateJunctionAsync("link", project.DirectoryPath);
        await Assert.ThrowsExactlyAsync<IOException>(() => VisitProjects.OpenAsync(link));
        await Assert.ThrowsExactlyAsync<IOException>(() => VisitProjects.CreateAsync(Path.Combine(link, "child"), ProjectTests.Details));
    }
}
