using HarmonyLib;
using RimWorld;
using Verse;

namespace OdysseyGravshipBatteryLaunch
{
	/// <summary>
	/// Requires the grav engine to be fully prepared before Odyssey launch can start.
	///
	/// The old version validated a stored battery reserve. The new version instead enforces a separate
	/// preparation phase. Once prepared, the actual launch is meant to proceed with Odyssey's normal
	/// ritual / gathering / takeoff flow.
	/// </summary>
	[HarmonyPatch(typeof(Building_GravEngine), nameof(Building_GravEngine.CanLaunch))]
	public static class Building_GravEngine_CanLaunch_Patch
	{
		private static void Postfix(Building_GravEngine __instance, CompPilotConsole console, ref AcceptanceReport __result)
		{
			if (__instance == null || !__result.Accepted)
			{
				return;
			}

			GravshipLaunchWarmupManager manager = __instance.Map?.GetComponent<GravshipLaunchWarmupManager>();
			GravshipWarmupState state = manager?.getStateForEngine(__instance);

			if (state == null)
			{
				__result = new AcceptanceReport("OGBL_EngineNotPrepared".Translate().CapitalizeFirst());
				return;
			}

			if (state.phase != GravshipPreparationPhase.Spooled)
			{
				__result = new AcceptanceReport(
					"OGBL_EngineSpooling".Translate(state.getPercentComplete().ToString("0")).CapitalizeFirst());
			}
		}
	}
}
