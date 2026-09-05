using System.Globalization;

namespace LimbusModEditor.Formats.Unity;

/// <summary>
/// Shared text parsing rules for editable Unity field value types. The field
/// editor uses this to validate before saving; the format backend applies the
/// same ranges when writing. Enum fields are edited through their underlying
/// integer value.
/// </summary>
public static class UnityFieldValueParser
{
    public static bool TryValidate(string valueType, string text, out string? error)
    {
        error = null;
        var culture = CultureInfo.InvariantCulture;
        try
        {
            switch (valueType)
            {
                case "bool":
                    _ = bool.Parse(text);
                    return true;
                case "int8":
                    _ = sbyte.Parse(text, culture);
                    return true;
                case "uint8":
                    _ = byte.Parse(text, culture);
                    return true;
                case "int16":
                    _ = short.Parse(text, culture);
                    return true;
                case "uint16":
                    _ = ushort.Parse(text, culture);
                    return true;
                case "int32":
                    _ = int.Parse(text, culture);
                    return true;
                case "uint32":
                    _ = uint.Parse(text, culture);
                    return true;
                case "int64":
                    _ = long.Parse(text, culture);
                    return true;
                case "uint64":
                    _ = ulong.Parse(text, culture);
                    return true;
                case "float":
                    _ = float.Parse(text, culture);
                    return true;
                case "double":
                    _ = double.Parse(text, culture);
                    return true;
                case "string":
                    return true;
                default:
                    error = $"类型 {valueType} 不支持文本编辑。";
                    return false;
            }
        }
        catch (FormatException)
        {
            error = $"值“{text}”不是有效的 {valueType}。";
            return false;
        }
        catch (OverflowException)
        {
            error = $"值“{text}”超出 {valueType} 范围。";
            return false;
        }
    }
}
