using System;
using System.Collections.Generic;
using System.Globalization;

namespace AvaibeExam.Util;

/// <summary>
/// Minimal semantic version comparison ("1.2.3" style; missing components are 0;
/// any pre-release suffix after "-" or "+" is ignored). Named SemVer to avoid clashing
/// with System.Version.
/// </summary>
public static class SemVer
{
    public static int[] Parse(string version)
    {
        var core = version ?? string.Empty;
        var cut = core.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) core = core.Substring(0, cut);
        var parts = new List<int>();
        foreach (var piece in core.Split('.'))
        {
            parts.Add(int.TryParse(piece.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0);
        }
        while (parts.Count < 3) parts.Add(0);
        return parts.ToArray();
    }

    /// <summary>Returns negative when a &lt; b, 0 when equal, positive when a &gt; b.</summary>
    public static int Compare(string a, string b)
    {
        var x = Parse(a);
        var y = Parse(b);
        var n = Math.Max(x.Length, y.Length);
        for (var i = 0; i < n; i++)
        {
            var xi = i < x.Length ? x[i] : 0;
            var yi = i < y.Length ? y[i] : 0;
            if (xi != yi) return xi.CompareTo(yi);
        }
        return 0;
    }

    /// <summary>true when <paramref name="current"/> satisfies <paramref name="minimum"/>.</summary>
    public static bool IsAtLeast(string current, string minimum) => Compare(current, minimum) >= 0;
}
