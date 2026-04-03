using HarmonyLib;
using Verse;

namespace GravshipRewired;

/// <summary>
/// RimWorld loads all classes marked with <see cref="StaticConstructorOnStartup"/> during game startup.
///
/// In a typical C# application you would have an explicit entry point such as Main().
/// RimWorld mods do not have that. Instead, the game discovers assemblies and runs static
/// constructors on specific types. This class is therefore the effective "bootstrap" for the mod.
///
/// The only job here is to create one Harmony instance and ask it to apply every patch class in
/// this assembly. Harmony then scans for methods decorated with [HarmonyPatch] and rewrites the
/// game methods at runtime.
/// </summary>
[StaticConstructorOnStartup]
public static class ModBootstrap
{
	static ModBootstrap()
	{
		// The string is the Harmony id. It should be stable and globally unique so other mods and
		// log output can identify which patch owner made a change.
		var harmony = new Harmony("sigsegv111.realrim.gravshiprewired");

		// PatchAll() scans this assembly for [HarmonyPatch] attributes and installs every patch.
		harmony.PatchAll();
	}
}
