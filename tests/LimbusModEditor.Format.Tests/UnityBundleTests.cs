using System.Buffers.Binary;
using System.Text;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Format.Tests;

public class UnityBundleTests
{
    [Fact]
    public void InspectReadsUnityFsHeader()
    {
        var data = new byte[80];
        Encoding.UTF8.GetBytes("UnityFS\0").CopyTo(data, 0);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), 6);
        Encoding.UTF8.GetBytes("2021.3.0f1\0").CopyTo(data, 12);
        Encoding.UTF8.GetBytes("UnityEngine\0").CopyTo(data, 23);
        var info = UnityBundleInspector.Inspect(data);
        Assert.NotNull(info);
        Assert.Equal("UnityFS", info.Signature);
        Assert.Equal(6, info.FormatVersion);
        Assert.Equal("2021.3.0f1", info.UnityVersion);
    }
}
