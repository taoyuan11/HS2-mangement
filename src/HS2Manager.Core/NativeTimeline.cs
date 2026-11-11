using System.Runtime.CompilerServices;

namespace HS2Manager.Core;

public static class NativeTimeline
{
    public const int FixtureRate = 30;
    public static float FixedTime(int frame) => Divide(frame, FixtureRate);
    // Mono used scalar division. Constant reciprocal multiplication changes some event-boundary frames.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float Divide(float numerator, float denominator) => numerator / denominator;
}
