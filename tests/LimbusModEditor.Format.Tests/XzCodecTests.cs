using System.IO.Compression;
using LimbusModEditor.Formats.Carra;

namespace LimbusModEditor.Format.Tests;

public sealed class XzCodecTests
{
    [Fact]
    public void JovelerCodecProducesReadableXz()
    {
        var source = Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray();
        var codec = new JovelerXzCodec();
        var encoded = codec.Encode(source);

        Assert.True(encoded.Length > 6);
        Assert.Equal(new byte[] { 0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0 }, encoded[..6]);

        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
        {
            using var entry = zip.CreateEntry("acct/bundle/1.28").Open();
            entry.Write(encoded);
        }
        var package = CarraArchive.Read(new MemoryStream(archive.ToArray()));
        Assert.Equal(source, package.Entries.Single().ReadData());
    }
}
