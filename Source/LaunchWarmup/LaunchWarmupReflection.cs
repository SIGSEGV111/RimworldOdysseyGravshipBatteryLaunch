using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace GravshipRewired
{
	/// <summary>
	/// Helper for reading the ritual's behavior worker in a version-tolerant way.
	///
	/// Odyssey / RimWorld internals are not always a stable public API. Depending on the exact game
	/// build or decompilation, a value might exist as a property in one version and a field in the
	/// next. Harmony's AccessTools gives us a safe way to probe both.
	/// </summary>
	public static class LaunchWarmupReflection
	{
		/// <summary>
		/// Attempts to retrieve the <see cref="RitualBehaviorWorker_GravshipLaunch"/> instance attached
		/// to a ritual definition. We only need this when finishing warmup and resuming Odyssey's
		/// original ritual flow.
		/// </summary>
		public static RitualBehaviorWorker_GravshipLaunch getRitualBehaviorWorker(Precept_Ritual ritual)
		{
			if (ritual == null)
			{
				return null;
			}

			// First try the common property names used by different builds / decompilations.
			PropertyInfo property =
				AccessTools.Property(ritual.GetType(), "behavior") ??
				AccessTools.Property(ritual.GetType(), "behaviorWorker");

			if (property != null)
			{
				try
				{
					return property.GetValue(ritual, null) as RitualBehaviorWorker_GravshipLaunch;
				}
				catch
				{
					// Silent by design: the field fallback below may still succeed.
				}
			}

			// Fall back to fields if the worker is not exposed as a property.
			FieldInfo field =
				AccessTools.Field(ritual.GetType(), "behavior") ??
				AccessTools.Field(ritual.GetType(), "behaviorWorker");

			if (field != null)
			{
				try
				{
					return field.GetValue(ritual) as RitualBehaviorWorker_GravshipLaunch;
				}
				catch
				{
					// Silent again for the same reason.
				}
			}

			return null;
		}

		/// <summary>
		/// Calls Odyssey's original <c>RitualBehaviorWorker_GravshipLaunch.TryExecuteOn</c> method.
		///
		/// We intentionally do this through reflection instead of a Harmony reverse patch because the
		/// reverse patch proved brittle across local game/mod setups. Reflection still enters the real
		/// Harmony-patched method, and our one-shot bypass flag lets that call pass through to Odyssey's
		/// original implementation exactly once.
		/// </summary>
		public static void invokeOriginalTryExecuteOn(
			RitualBehaviorWorker_GravshipLaunch ritual_worker,
			TargetInfo target,
			Pawn organizer,
			Precept_Ritual ritual,
			RitualObligation obligation,
			RitualRoleAssignments assignments,
			bool player_forced)
		{
			MethodInfo method = AccessTools.Method(typeof(RitualBehaviorWorker_GravshipLaunch), "TryExecuteOn");
			if (method == null)
			{
				throw new MissingMethodException(typeof(RitualBehaviorWorker_GravshipLaunch).FullName, "TryExecuteOn");
			}

			try
			{
				method.Invoke(ritual_worker, new object[]
				{
					target,
					organizer,
					ritual,
					obligation,
					assignments,
					player_forced
				});
			}
			catch (TargetInvocationException exception) when (exception.InnerException != null)
			{
				throw exception.InnerException;
			}
		}

	}
}
