using HarmonyLib;
using RimWorld;

namespace GravshipRewired
{
	/// <summary>
	/// Makes powered state part of a thruster's "can be active" decision.
	///
	/// Odyssey already has internal logic that decides whether a thruster participates in flight.
	/// By patching the getter instead of replacing flight range code directly, we let the existing
	/// Odyssey systems treat an unpowered thruster similarly to a broken or otherwise unusable one.
	/// </summary>
	[HarmonyPatch(typeof(CompGravshipThruster), nameof(CompGravshipThruster.CanBeActive), MethodType.Getter)]
	public static class CompGravshipThruster_CanBeActive_Patch
	{
		private static void Postfix(CompGravshipThruster __instance, ref bool __result)
		{
			// Respect any failure Odyssey already decided on. We only narrow the condition further.
			if (!__result || __instance?.parent == null)
			{
				return;
			}

			__result = GravshipBatteryUtility.isThingPowered(__instance.parent);
		}
	}
}
