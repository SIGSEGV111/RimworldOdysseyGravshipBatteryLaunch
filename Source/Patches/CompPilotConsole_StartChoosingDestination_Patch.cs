using HarmonyLib;
using RimWorld;
using Verse;

namespace GravshipRewired
{
	internal static class GravshipLaunchInteractionGuard
	{
		public static bool shouldAllowLaunchInteraction(CompPilotConsole console_comp)
		{
			Thing console = console_comp?.parent;
			Building_GravEngine grav_engine = console_comp?.engine;
			if (console == null || grav_engine == null)
			{
				return true;
			}

			GravshipLaunchWarmupManager manager = console.Map?.GetComponent<GravshipLaunchWarmupManager>();
			GravshipWarmupState state = manager?.getStateForEngine(grav_engine);
			if (state?.phase == GravshipPreparationPhase.Spooled)
			{
				return true;
			}

			string message = state == null
				? "OGBL_EngineNotPrepared".Translate().CapitalizeFirst()
				: "OGBL_EngineSpooling".Translate(state.getPercentComplete().ToString("0")).CapitalizeFirst();
			Messages.Message(message, console, MessageTypeDefOf.RejectInput, false);
			return false;
		}
	}

	/// <summary>
	/// Blocks the pilot console's launch flow until the engine has been fully prepared.
	/// </summary>
	[HarmonyPatch(typeof(CompPilotConsole), "StartChoosingDestination_NewTemp")]
	public static class CompPilotConsole_StartChoosingDestination_NewTemp_Patch
	{
		private static bool Prefix(CompPilotConsole __instance)
		{
			return GravshipLaunchInteractionGuard.shouldAllowLaunchInteraction(__instance);
		}
	}

	[HarmonyPatch(typeof(CompPilotConsole), "StartChoosingDestination")]
	public static class CompPilotConsole_StartChoosingDestination_Patch
	{
		private static bool Prefix(CompPilotConsole __instance)
		{
			return GravshipLaunchInteractionGuard.shouldAllowLaunchInteraction(__instance);
		}
	}
}
