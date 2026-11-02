using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>The arithmetic of a stair that has no geometry in it (so it can be tested without a scan).</summary>
public static class StairMath
{
    /// <summary>Riser counts for a stair whose flights have the given horizontal lengths: each flight gets as many steps as fit its
    /// leg at 'tread' (a little rounding up), and the total is brought to 'total' - extra steps go to the longest flights, surplus is
    /// taken from the longest flights first. Never negative, and the sum is exactly 'total' (a lone flight just takes all of it).</summary>
    public static List<int> DistributeRisers(IList<float> lens, int total, float tread)
    {
        var n = lens.Select(l => Mathf.Max(0, Mathf.FloorToInt(l / tread + 0.25f))).ToList();
        if (n.Count == 0 || total < 0) return n;
        int guard = 10000;
        while (n.Sum() < total && guard-- > 0)
            n[LongestFlight(lens)]++;                          // missing steps: the longest leg takes them
        while (n.Sum() > total && guard-- > 0)
        {
            int widest = 0;                                    // too many: take one from the flight with the most steps
            for (int i = 1; i < n.Count; i++) if (n[i] > n[widest]) widest = i;
            if (n[widest] == 0) break;
            n[widest]--;
        }
        return n;
    }

    static int LongestFlight(IList<float> lens)
    {
        int best = 0;
        for (int i = 1; i < lens.Count; i++) if (lens[i] > lens[best]) best = i;
        return best;
    }
}
