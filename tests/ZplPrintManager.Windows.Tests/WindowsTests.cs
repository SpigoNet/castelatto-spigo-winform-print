using ZplPrintManager.Infrastructure.Windows;
namespace ZplPrintManager.Windows.Tests;

public sealed class WindowsTests
{
    [Fact]
    public void Dpapi_RoundTripsWithoutStoringPlaintext()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try { var secrets = new DpapiSecrets(directory); secrets.SelfTest(); Guid id = Guid.NewGuid(); secrets.Write(id, "segredo-de-teste"); Assert.Equal("segredo-de-teste", secrets.Read(id)); Assert.DoesNotContain("segredo-de-teste", File.ReadAllText(Directory.GetFiles(directory).Single())); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
