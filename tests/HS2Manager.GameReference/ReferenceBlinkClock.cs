using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace HS2Manager.GameReference
{
    // The fixture fixes the game's native blink clock, without replacing its
    // state machine, random choices or face blending. Absolute process time
    // otherwise changes float comparisons at exact 30 Hz deadline boundaries.
    internal static class ReferenceBlinkClock
    {
        internal static bool Enabled;
        internal static float Time;
        internal static void Initialize(Harmony harmony)
        {
            foreach(var name in new[]{"CalcBlink","SetForceOpen","SetForceClose"})
                harmony.Patch(typeof(FBSBlinkControl).GetMethod(name),transpiler:new HarmonyMethod(typeof(ReferenceBlinkClock).GetMethod("ReplaceTime",BindingFlags.Static|BindingFlags.NonPublic)));
        }
        private static float ReadTime(){return Enabled?Time:UnityEngine.Time.time;}
        private static IEnumerable<CodeInstruction> ReplaceTime(IEnumerable<CodeInstruction> instructions)
        {
            var original=typeof(UnityEngine.Time).GetProperty("time").GetGetMethod();
            var replacement=typeof(ReferenceBlinkClock).GetMethod("ReadTime",BindingFlags.Static|BindingFlags.NonPublic);
            foreach(var instruction in instructions)
            {
                if(instruction.opcode==OpCodes.Call&&Equals(instruction.operand,original))instruction.operand=replacement;
                yield return instruction;
            }
        }
    }
}
