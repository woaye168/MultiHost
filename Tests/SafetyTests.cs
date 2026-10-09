using System.Diagnostics;
using System.IO;
using Xunit;

namespace MultiHost.Tests;

public class SafetyTests
{
    [Fact]
    public void SameNameOutsideFolderIsNotOwned()
    {
        using var self = Process.GetCurrentProcess();
        var found = ProcessManager.FindInstanceProcesses(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), self.ProcessName + ".exe");
        try { Assert.Empty(found); }
        finally { foreach (var p in found) p.Dispose(); }
    }

    [Fact]
    public void SiblingPrefixIsNotOwned()
    {
        using var self = Process.GetCurrentProcess();
        var directory = Path.GetDirectoryName(self.MainModule!.FileName)!;
        var found = ProcessManager.FindInstanceProcesses(directory[..^1], "");
        try { Assert.DoesNotContain(found, p => p.Id == self.Id); }
        finally { foreach (var p in found) p.Dispose(); }
    }

    [Fact]
    public void DefaultBatchHasNoDelay() => Assert.Equal(0, ThemeManager.BatchIntervalSeconds);
}
