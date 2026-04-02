using HarmonyLib;
using RimWorld;
using Verse;

namespace OdysseyGravshipBatteryLaunch
{
	/// <summary>
	/// Adds preparation status text to the pilot console's inspect pane.
	/// </summary>
	[HarmonyPatch(typeof(CompPilotConsole), nameof(CompPilotConsole.CompInspectStringExtra))]
	public static class CompPilotConsole_CompInspectStringExtra_Patch
	{
		private static void Postfix(CompPilotConsole __instance, ref string __result)
		{
			Thing console = __instance?.parent;
			GravshipLaunchWarmupManager manager = console?.Map?.GetComponent<GravshipLaunchWarmupManager>();
			GravshipWarmupState state = manager?.getStateForConsole(console);

			if (state == null)
			{
				return;
			}

			if (!__result.NullOrEmpty())
			{
				__result += "\n";
			}

			if (state.phase == GravshipPreparationPhase.Spooled)
			{
				__result += "OGBL_PreparedInspect".Translate().CapitalizeFirst();
				return;
			}

			__result += "OGBL_PreparationInspect".Translate(
				state.getPercentComplete().ToString("0"),
				state.getHoursRemaining().ToString("0.0")).CapitalizeFirst();
		}
	}
}
