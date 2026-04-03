using HarmonyLib;
using RimWorld;
using Verse;

namespace GravshipRewired
{
	/// <summary>
	/// Adds preparation status to the grav engine itself.
	///
	/// Patching CompPowerTrader.CompInspectStringExtra is an easy way to append engine-specific text
	/// without replacing the grav engine's entire inspect string implementation.
	/// </summary>
	[HarmonyPatch(typeof(CompPowerTrader), nameof(CompPowerTrader.CompInspectStringExtra))]
	public static class CompPowerTrader_CompInspectStringExtra_Patch
	{
		private static void Postfix(CompPowerTrader __instance, ref string __result)
		{
			ThingWithComps parent = __instance?.parent;
			Building_GravEngine grav_engine = parent as Building_GravEngine;
			if (grav_engine == null)
			{
				return;
			}

			GravshipLaunchWarmupManager manager = grav_engine.Map?.GetComponent<GravshipLaunchWarmupManager>();
			GravshipWarmupState state = manager?.getStateForEngine(grav_engine);
			if (state == null)
			{
				return;
			}

			if (!__result.NullOrEmpty())
			{
				__result += "\n";
			}

			float extra_draw = state.extra_spool_power_watts;
			if (state.phase == GravshipPreparationPhase.Spooled)
			{
				__result += "OGBL_EnginePreparedInspect".Translate(extra_draw.ToString("0")).CapitalizeFirst();
				return;
			}

			__result += "OGBL_EnginePreparingInspect".Translate(
				state.getPercentComplete().ToString("0"),
				extra_draw.ToString("0")).CapitalizeFirst();
		}
	}
}
