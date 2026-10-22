namespace StartupTool.Core.Models;

/// <summary>
/// 脚本依赖校验（D4）：自依赖 / 环依赖会让"等依赖就绪"变成永久等待（每次启动都等满上限后放弃），
/// 必须在保存前拦下；保存时同时清理指向不存在脚本的悬空引用。
/// </summary>
public static class ScriptDependencyValidator
{
    /// <summary>返回首个环依赖的可读描述（形如 `A → B → A`）；无环返回 null。</summary>
    public static string? FindCycle(IEnumerable<ScriptConfig> scripts)
    {
        var map = scripts
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // 三色标记：0=未访问 1=在栈上（灰） 2=已完成（黑）
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new List<string>();

        foreach (var id in map.Keys)
        {
            var found = Visit(id);
            if (found is not null) return found;
        }
        return null;

        string? Visit(string id)
        {
            var s = state.GetValueOrDefault(id);
            if (s == 1) return Describe(path, id, map); // 回到栈上节点 = 成环
            if (s == 2) return null;

            state[id] = 1;
            path.Add(id);
            foreach (var dep in map[id].DependsOn ?? new List<string>())
            {
                // 悬空引用（依赖不存在的脚本）不算环，保存时会被清理
                if (string.IsNullOrEmpty(dep) || !map.ContainsKey(dep)) continue;
                var child = Visit(dep);
                if (child is not null) return child;
            }
            state[id] = 2;
            path.RemoveAt(path.Count - 1);
            return null;
        }
    }

    /// <summary>清理悬空依赖引用与自依赖（返回是否有改动）。</summary>
    public static bool NormalizeDependencies(IEnumerable<ScriptConfig> scripts)
    {
        var ids = scripts.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var changed = false;
        foreach (var s in scripts)
        {
            s.DependsOn ??= new List<string>();
            var cleaned = s.DependsOn
                .Where(d => !string.IsNullOrEmpty(d) && d != s.Id && ids.Contains(d))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (cleaned.Count != s.DependsOn.Count)
            {
                s.DependsOn = cleaned;
                changed = true;
            }
        }
        return changed;
    }

    private static string Describe(List<string> path, string repeated, IReadOnlyDictionary<string, ScriptConfig> map)
    {
        var start = path.IndexOf(repeated);
        var chain = path.Skip(start < 0 ? 0 : start)
            .Select(p => Display(map, p))
            .ToList();
        chain.Add(Display(map, repeated));
        return string.Join(" → ", chain);
    }

    private static string Display(IReadOnlyDictionary<string, ScriptConfig> map, string id)
        => map.TryGetValue(id, out var s) && !string.IsNullOrWhiteSpace(s.Name) ? s.Name : id;
}
