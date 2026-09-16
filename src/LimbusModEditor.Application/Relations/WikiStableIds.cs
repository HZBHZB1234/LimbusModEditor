using System.Security.Cryptography;
using System.Text;

namespace LimbusModEditor.Application.Relations;

/// <summary>
/// 维基页面树里「稳定 id」的算法：把内容键哈希成定长十六进制串。
///
/// <para><b>为什么不能用 GUID</b>：<see cref="WikiPageArranger"/> 每次编排都
/// <c>Guid.NewGuid()</c>，重复生成会造出全新 id → 旧行永远匹配不上 →
/// 要么堆出重复行（不幂等），要么整棵删掉重建（吃掉用户修订）。
/// 稳定 id 让「同一份内容」在两次生成里落到<b>同一行</b>，
/// 从而既能 UPSERT 幂等，又能在读回 <c>source</c> 时判断「这行被用户改过没」。</para>
///
/// <para><b>不是 id 猜测</b>：输入是内容自身的键（页面 id / 分节标题 / 资源定位键），
/// 不含任何「扫数字窗口」式的推断；键相同才同 id。</para>
/// </summary>
public static class WikiStableIds
{
    // 单元分隔符（0x1F）：避免 "a"+"bc" 与 "ab"+"c" 拼出同一个键。
    private static readonly string Separator = new string((char)31, 1);

    /// <summary>把若干内容段拼成稳定 id（32 个十六进制字符 = SHA-256 前 16 字节）。</summary>
    public static string Of(params string[] parts)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) builder.Append(Separator);
            builder.Append(parts[i] ?? string.Empty);
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash, 0, 16);
    }
}
