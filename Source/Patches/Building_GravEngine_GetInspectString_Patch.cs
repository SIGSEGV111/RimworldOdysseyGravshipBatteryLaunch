using HarmonyLib;
using RimWorld;

namespace GravshipRewired
{
	/// <summary>
	/// Hides any remaining vanilla cooldown line and proactively clears cooldown state when the engine is inspected.
	/// </summary>
	[HarmonyPatch(typeof(Building_GravEngine), nameof(Building_GravEngine.GetInspectString))]
	public static class Building_GravEngine_GetInspectString_Patch
	{
		private static void Prefix(Building_GravEngine __instance)
		{
			GravshipCooldownUtility.clearLaunchCooldown(__instance);
		}

		private static void Postfix(Building_GravEngine __instance, ref string __result)
		{
			GravshipCooldownUtility.clearLaunchCooldown(__instance);
			__result = GravshipCooldownUtility.stripCooldownLines(__result);
		}
	}
}
