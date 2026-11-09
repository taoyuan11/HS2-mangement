using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using AIChara;
using System.Collections.Generic;
using UnityEngine;

namespace HS2Manager.GameReference
{
    // Optional observer only: records native reset input without changing it.
    internal static class PhysicsResetTrace
    {
        internal static string Output,Phase="load";
        private static readonly List<ColliderEntry> created=new List<ColliderEntry>();
        private sealed class ColliderEntry {internal DynamicBoneCollider Collider;internal string Path;}
        internal static void Initialize(string output)
        {
            Output=output;
            var harmony=new Harmony("local.hs2manager.reference.physics.trace");
            ReferenceBlinkClock.Initialize(harmony);
            harmony.Patch(typeof(CmpBase).GetProperty("isVisible").GetGetMethod(),prefix:new HarmonyMethod(typeof(PhysicsResetTrace).GetMethod("RepairVisibility",BindingFlags.Static|BindingFlags.NonPublic)));
            var controller=Type.GetType("KK_Plugins.Colliders+ColliderController, HS2_Colliders",false);
            var add=controller==null?null:controller.GetMethods(BindingFlags.Instance|BindingFlags.NonPublic).FirstOrDefault(m=>m.Name=="AddCollider"&&m.GetParameters().Length==6);
            if(add!=null)harmony.Patch(add,postfix:new HarmonyMethod(typeof(PhysicsResetTrace).GetMethod("ObserveCreation",BindingFlags.Static|BindingFlags.NonPublic)));
            if(Environment.GetEnvironmentVariable("HS2_PHYSICS_TRACE")=="1")
            {
                harmony.Patch(typeof(DynamicBone).GetMethod("ResetParticlesPosition"),postfix:new HarmonyMethod(typeof(PhysicsResetTrace).GetMethod("Observe",BindingFlags.Static|BindingFlags.NonPublic)));
                harmony.Patch(typeof(DynamicBone).GetMethod("SetupParticles",BindingFlags.Instance|BindingFlags.NonPublic),postfix:new HarmonyMethod(typeof(PhysicsResetTrace).GetMethod("ObserveSetup",BindingFlags.Static|BindingFlags.NonPublic)));
                harmony.Patch(typeof(DynamicBone).GetMethod("LateUpdate",BindingFlags.Instance|BindingFlags.NonPublic),postfix:new HarmonyMethod(typeof(PhysicsResetTrace).GetMethod("ObserveLate",BindingFlags.Static|BindingFlags.NonPublic)));
            }
        }
        private static void RepairVisibility(CmpBase __instance)
        {
            var values=__instance.rendCheckVisible;
            if(values==null||!values.Any(r=>r==null))return;
            __instance.rendCheckVisible=values.Where(r=>r!=null).ToArray();
            File.AppendAllText(Path.Combine(Output,"visibility-reference-repairs.txt"),__instance.name+": removed "+(values.Length-__instance.rendCheckVisible.Length)+" destroyed renderer references before visibility evaluation\n");
        }
        private static void ObserveCreation(DynamicBoneCollider __result)
        {
            if(__result==null)return;
            string path=__result.name;
            for(var p=__result.transform.parent;p!=null;p=p.parent)path=p.name+"/"+path;
            created.Add(new ColliderEntry{Collider=__result,Path=path});
        }
        internal static void WriteCreationState()
        {
            File.WriteAllLines(Path.Combine(Output,"collider-creation-state.txt"),created.Select(c=>(c.Collider==null?"destroyed ":"alive ")+c.Path).ToArray());
        }
        [Serializable]private sealed class Row {public int Frame;public string Phase,Root;public Vector3 Host;public Vector3[] Positions;}
        private static void Observe(DynamicBone __instance)
        {
            if(!Selected(__instance))return;
            var flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
            var particles=(IEnumerable)typeof(DynamicBone).GetField("m_Particles",flags).GetValue(__instance);
            var row=new Row{Frame=Time.frameCount,Phase=Phase,Root=__instance.m_Root.name,Host=__instance.transform.position,Positions=particles.Cast<object>().Select(p=>(Vector3)p.GetType().GetField("m_Position",flags).GetValue(p)).ToArray()};
            File.AppendAllText(Path.Combine(Output,"physics-resets.jsonl"),JsonUtility.ToJson(row)+"\n");
        }
        private static bool Selected(DynamicBone instance)
        {
            var names=Environment.GetEnvironmentVariable("HS2_PHYSICS_TRACE_NAMES");
            return instance.m_Root!=null&&(names==null?new[]{"joint25","AddArmLow_L_sv_1","cf_J_sk_00_00"}:names.Split(',')).Contains(instance.m_Root.name);
        }
        private static void ObserveSetup(DynamicBone __instance)
        {
            if(!Selected(__instance))return;
            File.AppendAllText(Path.Combine(Output,"physics-setup.txt"),Time.frameCount+" "+Phase+" "+__instance.m_Root.name+"\n"+Environment.StackTrace+"\n"+string.Join("\n",__instance.m_Root.GetComponentsInChildren<Transform>(true).Select(t=>t.name+" "+t.localPosition.ToString("R")+" "+t.localRotation.ToString("R")))+"\n");
        }
        private static void ObserveLate(DynamicBone __instance)
        {
            if(!Selected(__instance)||Phase!="front-warm")return;
            string path=Path.Combine(Output,"physics-late.txt");
            if(File.Exists(path)&&new FileInfo(path).Length>50000)return;
            File.AppendAllText(path,Time.frameCount+" "+__instance.m_Root.name+" "+__instance.m_Root.position.ToString("R")+"\n");
        }
    }
}
