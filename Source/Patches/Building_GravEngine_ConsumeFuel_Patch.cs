using HarmonyLib;
using RimWorld;
using RimWorld.Planet;

namespace OdysseyGravshipBatteryLaunch
{
	/// <summary>
	/// The earlier mod versions drew a one-time battery charge when launch actually happened.
	///
	/// This revision no longer does that. Engine preparation now consumes power progressively over
	/// two in-game hours through the normal power network, so takeoff itself should not perform any
	/// additional custom battery draw.
	///
	/// The patch class is intentionally left in place because removing a file entirely makes iterative
	/// patching harder for the user. The prefix/postfix are now deliberate no-ops.
	/// </summary>
	[HarmonyPatch(typeof(Building_GravEngine), nameof(Building_GravEngine.ConsumeFuel))]
	public static class Building_GravEngine_ConsumeFuel_Patch
	{
		private static void Prefix(Building_GravEngine __instance, PlanetTile tile)
		{
		}

		private static void Postfix(Building_GravEngine __instance, PlanetTile tile)
		{
		}
	}
}
