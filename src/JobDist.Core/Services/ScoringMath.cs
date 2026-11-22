using System.Numerics;
using System.Runtime.InteropServices;

namespace JobDist.Core.Services;

/// <summary>
/// Numeric kernels used on the scheduling hot path. Every kernel has a SIMD path (System.Numerics.Vector&lt;double&gt;, which maps
/// to SSE/AVX/AVX-512 on x64 and AdvSimd on ARM64) and a scalar path; the caller chooses with <c>useSimd</c> so the behaviour
/// can be switched off by configuration (<c>JobDist:UseSimd</c>) and both paths are unit-tested for equality.
/// </summary>
public static class ScoringMath
{
    /// <summary>
    /// Computes worker scores: <c>wLoad*(1-load) + wSpeed*speed + wRel*reliability + wExp*exploration + bonus</c>.
    /// </summary>
    /// <param name="load">Load ratio per candidate (0 = idle, 1 = full).</param>
    /// <param name="speed">Normalised speed per candidate (1 = fastest candidate).</param>
    /// <param name="reliability">Predicted success probability per candidate.</param>
    /// <param name="exploration">Exploration bonus per candidate.</param>
    /// <param name="bonus">Additive bonus / penalty per candidate (preferred worker, last failed worker...).</param>
    /// <param name="weights">(load, speed, reliability, exploration) weights.</param>
    /// <param name="scores">Output span, same length as inputs.</param>
    /// <param name="useSimd">Use the vectorised path when hardware accelerated.</param>
    public static void ComputeScores(
        ReadOnlySpan<double> load, ReadOnlySpan<double> speed, ReadOnlySpan<double> reliability, ReadOnlySpan<double> exploration,
        ReadOnlySpan<double> bonus, (double Load, double Speed, double Reliability, double Exploration) weights, Span<double> scores, bool useSimd)
    {
        // Step 1: validate that every span has the same length (avoids out-of-range reads in the vector loop).
        var n = scores.Length;
        if (load.Length != n || speed.Length != n || reliability.Length != n || exploration.Length != n || bonus.Length != n)
        {
            throw new ArgumentException("All spans must have the same length.");
        }

        var i = 0;

        // Step 2: SIMD loop processes Vector<double>.Count candidates per instruction.
        if (useSimd && Vector.IsHardwareAccelerated && n >= Vector<double>.Count)
        {
            var wl = new Vector<double>(weights.Load);
            var ws = new Vector<double>(weights.Speed);
            var wr = new Vector<double>(weights.Reliability);
            var we = new Vector<double>(weights.Exploration);
            var one = Vector<double>.One;
            var lv = MemoryMarshal.Cast<double, Vector<double>>(load);
            var sv = MemoryMarshal.Cast<double, Vector<double>>(speed);
            var rv = MemoryMarshal.Cast<double, Vector<double>>(reliability);
            var ev = MemoryMarshal.Cast<double, Vector<double>>(exploration);
            var bv = MemoryMarshal.Cast<double, Vector<double>>(bonus);
            var ov = MemoryMarshal.Cast<double, Vector<double>>(scores);
            for (var v = 0; v < ov.Length; v++)
            {
                ov[v] = wl * (one - lv[v]) + ws * sv[v] + wr * rv[v] + we * ev[v] + bv[v];
            }
            i = ov.Length * Vector<double>.Count;
        }

        // Step 3: scalar tail (or the whole array when SIMD is off).
        for (; i < n; i++)
        {
            scores[i] = weights.Load * (1 - load[i]) + weights.Speed * speed[i] + weights.Reliability * reliability[i]
                        + weights.Exploration * exploration[i] + bonus[i];
        }
    }

    /// <summary>Index of the maximum value among entries whose <paramref name="eligible"/> flag is set.</summary>
    /// <param name="values">Values.</param>
    /// <param name="eligible">Eligibility mask.</param>
    /// <returns>Index or -1 when nothing is eligible.</returns>
    public static int ArgMax(ReadOnlySpan<double> values, ReadOnlySpan<bool> eligible)
    {
        var best = -1;
        var bestValue = double.NegativeInfinity;
        for (var i = 0; i < values.Length; i++)
        {
            if (eligible[i] && values[i] > bestValue)
            {
                best = i;
                bestValue = values[i];
            }
        }
        return best;
    }

    /// <summary>
    /// Mean and population standard deviation.
    /// </summary>
    /// <param name="values">Samples.</param>
    /// <param name="useSimd">Use the vectorised path.</param>
    /// <returns>(mean, standard deviation); (0,0) for an empty span.</returns>
    public static (double Mean, double StdDev) MeanStdDev(ReadOnlySpan<double> values, bool useSimd)
    {
        if (values.IsEmpty)
        {
            return (0, 0);
        }

        // Step 1: sum and sum of squares (vectorised accumulators).
        double sum = 0, sumSq = 0;
        var i = 0;
        if (useSimd && Vector.IsHardwareAccelerated && values.Length >= Vector<double>.Count)
        {
            var acc = Vector<double>.Zero;
            var accSq = Vector<double>.Zero;
            var vectors = MemoryMarshal.Cast<double, Vector<double>>(values);
            foreach (var v in vectors)
            {
                acc += v;
                accSq += v * v;
            }
            sum = Vector.Sum(acc);
            sumSq = Vector.Sum(accSq);
            i = vectors.Length * Vector<double>.Count;
        }
        for (; i < values.Length; i++)
        {
            sum += values[i];
            sumSq += values[i] * values[i];
        }

        // Step 2: population variance, clamped against negative rounding error.
        var mean = sum / values.Length;
        var variance = Math.Max(0, sumSq / values.Length - mean * mean);
        return (mean, Math.Sqrt(variance));
    }

    /// <summary>Z-score with a floor on the standard deviation (avoids infinities on flat baselines).</summary>
    public static double ZScore(double value, double mean, double stdDev, double minStdDev = 1e-9) =>
        (value - mean) / Math.Max(stdDev, Math.Max(minStdDev, Math.Abs(mean) * 0.05));
}
