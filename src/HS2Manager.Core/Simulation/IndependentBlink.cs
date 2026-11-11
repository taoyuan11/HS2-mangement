namespace HS2Manager.Core.Simulation;

/// <summary>Instance-owned blink timeline. No process-global random state or wall-clock dependency.</summary>
internal sealed class IndependentBlink
{
    private readonly uint[] state = new uint[4];
    private int phase, closedFrames;
    private float deadline, duration, open = 1;
    internal string Diagnostic => $"{phase} {deadline:R} {duration:R} {open:R} {closedFrames}";
    internal IndependentBlink(int seed)
    {
        state[0] = unchecked((uint)seed);
        for (int i = 1; i < state.Length; i++) state[i] = unchecked(1812433253u * state[i - 1] + 1);
    }
    private uint Next()
    {
        uint mixed = state[0] ^ (state[0] << 11), last = state[3];
        Array.Copy(state, 1, state, 0, 3);
        return state[3] = last ^ (last >> 19) ^ mixed ^ (mixed >> 8);
    }
    private float Duration() => .15f + .05f * (1 - (Next() & 0x7fffff) / 8388607f);
    internal float Evaluate(float time)
    {
        float remaining = MathF.Max(0, deadline - time);
        open = phase == 0 ? 1 : Math.Clamp(phase == 1 ? remaining / duration : 1 - remaining / duration, 0, 1);
        if (time <= deadline) return open;
        if (phase == 0) { duration = Duration(); deadline = time + duration; closedFrames = (int)(Next() % 3) + 1; phase = 1; }
        else if (phase == 1) { if (--closedFrames <= 0) { duration = Duration(); deadline = time + duration; phase = -1; } }
        else
        {
            float frequency = Mathf.InverseLerp(0, 30, Next() % 30);
            // The old Mono evaluation retains precision across this multiply-add until the field store.
            // Rounding the product first changes equality at fixed-timestep boundaries (e.g. seed 1979, frame 316).
            deadline = (float)((double)time + (double).2f * Mathf.Lerp(0, 30, frequency)); phase = 0;
        }
        return open;
    }
}
