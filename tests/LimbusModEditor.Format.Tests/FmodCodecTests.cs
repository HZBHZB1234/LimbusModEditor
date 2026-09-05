using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.Format.Tests;

public class FmodCodecTests
{
    [Fact]
    public void MissingLibrariesProduceDiagnosticStatus()
    {
        using var loader = new FmodCodecLibrary();
        var loaded = loader.TryLoad(Path.GetTempPath(), out var status);
        Assert.False(loaded);
        Assert.False(status.IsLoaded);
        Assert.False(string.IsNullOrWhiteSpace(status.Error));
    }

    [Fact]
    public async Task UnavailableCodecFailsExplicitly()
    {
        var codec = new UnavailableFmodAudioCodec();
        await Assert.ThrowsAsync<NotSupportedException>(() => codec.DecodeFsbToWaveAsync(ReadOnlyMemory<byte>.Empty));
    }
}
