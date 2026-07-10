using System.Text;
using ToolGood.Words.Pinyin;

namespace StartupCommon;

public static class PinyinConverter
{
    /// <summary>
    /// 将中文名称转换为拼音目录名（不含声调，只保留字母数字）
    /// 示例： "示例脚本" → "shilijiaoben"
    /// </summary>
    public static string ToPinyinDirName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "unknown";

        // 先尝试用拼音库转换（返回全拼，不带声调，不带空格：如"WoAiZhongGuo"）
        var pinyin = WordsHelper.GetPinyin(name);

        // 如果转换结果与原字符串相同（说明没有中文），则直接清理非文件名字符
        if (pinyin == name)
        {
            return SanitizeDirName(name);
        }

        // 拼音结果形如 "WoAiZhongGuo"，转小写
        var result = SanitizeDirName(pinyin.ToLowerInvariant());
        return string.IsNullOrEmpty(result) ? "unknown" : result;
    }

    /// <summary>
    /// 清理字符串，只保留字母、数字、下划线、中划线
    /// </summary>
    private static string SanitizeDirName(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
                sb.Append(c);
            else if (c == ' ')
                sb.Append('_');
        }
        return sb.ToString();
    }
}