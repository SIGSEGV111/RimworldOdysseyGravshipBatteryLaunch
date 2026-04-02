using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace OdysseyGravshipBatteryLaunch
{
	/// <summary>
	/// Adds explicit preparation/shutdown gizmos to the pilot console.
	///
	/// We do not replace Odyssey's own launch gizmos. Instead we add the new preparation controls and
	/// let CanLaunch()/StartChoosingDestination enforce that launch is unavailable until the engine is
	/// fully prepared.
	/// </summary>
	[HarmonyPatch(typeof(CompPilotConsole), "CompGetGizmosExtra")]
	public static class CompPilotConsole_CompGetGizmosExtra_Patch
	{
		private static void Postfix(ref IEnumerable<Gizmo> __result, CompPilotConsole __instance)
		{
			__result = appendGizmos(__result, __instance);
		}

		private static IEnumerable<Gizmo> appendGizmos(IEnumerable<Gizmo> original_gizmos, CompPilotConsole console_comp)
		{
			foreach (Gizmo gizmo in original_gizmos)
			{
				yield return gizmo;
			}

			Thing console = console_comp?.parent;
			if (console?.Map == null || console_comp.engine == null)
			{
				yield break;
			}

			GravshipLaunchWarmupManager manager = console.Map.GetComponent<GravshipLaunchWarmupManager>();
			GravshipWarmupState state = manager?.getStateForEngine(console_comp.engine);

			if (state == null)
			{
				Command_Action prepare = new Command_Action();
				prepare.defaultLabel = "OGBL_PrepareForLaunch".Translate().CapitalizeFirst();
				prepare.defaultDesc = "OGBL_PrepareForLaunchDesc".Translate();
				prepare.action = delegate
				{
					Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
						"OGBL_PrepareForLaunchWarning".Translate(),
						delegate
						{
							manager?.tryStartPreparation(console_comp);
						},
						destructive: false));
				};
				yield return prepare;
				yield break;
			}

			Command_Action shutdown = new Command_Action();
			shutdown.defaultLabel = state.phase == GravshipPreparationPhase.Spooled
				? "OGBL_ShutdownPreparedEngine".Translate().CapitalizeFirst()
				: "OGBL_AbortPreparation".Translate().CapitalizeFirst();
			shutdown.defaultDesc = state.phase == GravshipPreparationPhase.Spooled
				? "OGBL_ShutdownPreparedEngineDesc".Translate()
				: "OGBL_AbortPreparationDesc".Translate();
			shutdown.action = delegate
			{
				manager?.cancelPreparationForConsole(console, false);
			};
			yield return shutdown;
		}
	}
}
