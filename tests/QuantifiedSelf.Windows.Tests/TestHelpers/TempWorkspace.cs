using System.IO;

namespace QuantifiedSelf.Windows.Tests.TestHelpers;

/// <summary>
/// Disposable unique temp workspace for tests that need isolated files or directories.
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    public TempWorkspace(string prefix = "qsw")
    {
        Root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Path => Root;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
