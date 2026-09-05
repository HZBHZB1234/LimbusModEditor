using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Formats.Unity;

/// <summary>
/// Maps Unity serialized class IDs to the editor's neutral asset types. The
/// table follows Unity's canonical class-ID reference (verified against
/// AssetsTools.NET's AssetClassID enum): 115 is MonoScript, 213 is Sprite and
/// there is no dedicated ScriptableObject class ID.
/// </summary>
public static class UnityClassId
{
    public const int GameObject = 1;
    public const int Texture2D = 28;
    public const int AudioClip = 83;
    public const int MonoBehaviour = 114;
    public const int MonoScript = 115;
    public const int Font = 128;
    public const int AssetBundle = 142;
    public const int Sprite = 213;

    public static bool TryMap(int typeId, out AssetType type)
    {
        type = Map(typeId);
        return type != AssetType.Unknown;
    }

    public static AssetType Map(int typeId) => typeId switch
    {
        GameObject => AssetType.GameObject,
        Texture2D => AssetType.Texture,
        AudioClip => AssetType.Audio,
        MonoBehaviour => AssetType.MonoBehaviour,
        MonoScript => AssetType.MonoScript,
        Font => AssetType.Font,
        AssetBundle => AssetType.Binary,
        Sprite => AssetType.Sprite,
        _ => AssetType.Unknown
    };
}
