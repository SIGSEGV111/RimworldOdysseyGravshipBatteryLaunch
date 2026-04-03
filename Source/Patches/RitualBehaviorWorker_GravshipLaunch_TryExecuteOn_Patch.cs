using HarmonyLib;
using RimWorld;
using Verse;

namespace GravshipRewired
{
	/// <summary>
	/// This patch used to divert the actual Odyssey launch ritual into a delayed warmup sequence.
	///
	/// The design has changed: launch preparation is now a separate precondition on the grav engine,
	/// while the actual Odyssey ritual should run unchanged once the engine is prepared. The patch
	/// therefore stays as an explicit no-op so the intent is obvious in the source tree.
	/// </summary>
	[HarmonyPatch(typeof(RitualBehaviorWorker_GravshipLaunch), "TryExecuteOn")]
	public static class RitualBehaviorWorker_GravshipLaunch_TryExecuteOn_Patch
	{
		private static bool Prefix(
			RitualBehaviorWorker_GravshipLaunch __instance,
			TargetInfo target,
			Pawn organizer,
			Precept_Ritual ritual,
			RitualObligation obligation,
			RitualRoleAssignments assignments,
			bool playerForced = false)
		{
			return true;
		}
	}
}
