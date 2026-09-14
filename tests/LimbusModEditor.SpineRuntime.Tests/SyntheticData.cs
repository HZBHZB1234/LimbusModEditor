using SkiaSharp;

namespace LimbusModEditor.SpineRuntime.Tests;

/// <summary>
/// 合成最小 Spine 4.0 数据，避免依赖真实美术资源即可驱动离线渲染测试。
/// 所有纹理页都用 <see cref="TestTextureSource.MakeSolidPng"/> 现场画一张纯色 PNG。
/// </summary>
internal static class SyntheticData
{
    public const string PageName = "page1";
    public const string RegionName = "region1";
    public const string MeshName = "mesh1";
    public const string FadeAnim = "fade";

    /// <summary>
    /// 一个含 region + mesh 两个插槽、且带“root 平移”动画的骨架。
    /// region 与 mesh 都以 root 骨为锚，居中于原点（本地坐标 -50..50）。
    /// </summary>
    public static (string json, string atlas, Dictionary<string, byte[]> pages) RegionMesh()
    {
        var pages = new Dictionary<string, byte[]>
        {
            [PageName] = TestTextureSource.MakeSolidPng(200, 200, new SKColor(0xFFFF0000)) // 不透明红
        };

        const string atlas = """
page1
size: 200,200
format: RGBA8888
filter: Linear,Linear
pma: true
region1
  bounds: 0,0,100,100
mesh1
  bounds: 0,0,100,100
""";

        const string json = """
{
  "skeleton": { "hash": "synthetic", "spine": "4.0.64", "width": 200, "height": 200 },
  "bones": [ { "name": "root" } ],
  "slots": [
    { "name": "regionSlot", "bone": "root", "attachment": "region1", "color": "ffffffff" },
    { "name": "meshSlot",   "bone": "root", "attachment": "mesh1",   "color": "ffffffff" }
  ],
  "skins": [
    {
      "name": "default",
      "attachments": {
        "regionSlot": {
          "region1": { "type": "region", "x": -70, "y": 0, "scaleX": 1, "scaleY": 1, "rotation": 0, "width": 100, "height": 100, "color": "ffffffff" }
        },
        "meshSlot": {
          "mesh1": { "type": "mesh", "uvs": [0,0, 1,0, 1,1, 0,1], "vertices": [90,-50, 190,-50, 190,50, 90,50], "triangles": [0,1,2, 0,2,3], "color": "ffffffff" }
        }
      }
    }
  ],
  "animations": {
    "fade": {
      "slots": {
        "regionSlot": {
          "alpha": [
            { "time": 0, "value": 1 },
            { "time": 1, "value": 0.3 }
          ]
        }
      }
    }
  }
}
""";
        return (json, atlas, pages);
    }

    /// <summary>
    /// 一个使用 <c>rotate: 90</c> 区域、且图集标 <c>pma: true</c> 的骨架，用于验证旋转与预乘不越界。
    /// </summary>
    public static (string json, string atlas, Dictionary<string, byte[]> pages) Rotate90Pma()
    {
        var pages = new Dictionary<string, byte[]>
        {
            [PageName] = TestTextureSource.MakeSolidPng(200, 200, new SKColor(0xFF00FF00)) // 不透明绿
        };

        const string atlas = """
page1
size: 200,200
format: RGBA8888
filter: Linear,Linear
pma: true
region1
  rotate: 90
  bounds: 50,50,100,100
""";

        const string json = """
{
  "skeleton": { "hash": "rot90", "spine": "4.0.64", "width": 200, "height": 200 },
  "bones": [ { "name": "root" } ],
  "slots": [
    { "name": "regionSlot", "bone": "root", "attachment": "region1", "color": "ffffffff" }
  ],
  "skins": [
    {
      "name": "default",
      "attachments": {
        "regionSlot": {
          "region1": { "type": "region", "x": 0, "y": 0, "scaleX": 1, "scaleY": 1, "rotation": 0, "width": 100, "height": 100, "color": "ffffffff" }
        }
      }
    }
  ]
}
""";
        return (json, atlas, pages);
    }

    /// <summary>明显非法的骨架 JSON（缺根对象），用于验证解析失败返回中文结果而非抛异常。</summary>
    public static string InvalidJson() => "this is not valid spine json [[[";
}
