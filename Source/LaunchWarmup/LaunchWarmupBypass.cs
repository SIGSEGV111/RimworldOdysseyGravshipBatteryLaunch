using System.Collections.Generic;
using Verse;

namespace OdysseyGravshipBatteryLaunch
{
	/// <summary>
	/// The warmup flow works by intercepting the normal Odyssey launch ritual, delaying it for
	/// 2 in-game hours, and then re-invoking the original ritual method.
	///
	/// That creates one subtle problem: when we call the original method ourselves, our Harmony
	/// prefix would normally intercept it again and start a second warmup. This tiny helper is the
	/// escape hatch. We mark a console id right before re-entry, then the prefix consumes that mark
	/// and allows the original method to run unmodified exactly once.
	/// </summary>
	public static class LaunchWarmupBypass
	{
		private static readonly HashSet<int> console_ids = new HashSet<int>();

		/// <summary>
		/// Marks a console as "the next TryExecuteOn call for this console should bypass warmup".
		/// </summary>
		public static void pushConsole(Thing console)
		{
			if (console != null)
			{
				console_ids.Add(console.thingIDNumber);
			}
		}

		/// <summary>
		/// Returns true once for a marked console and removes the mark immediately.
		/// This gives us single-use bypass semantics.
		/// </summary>
		public static bool consumeConsole(Thing console)
		{
			if (console == null)
			{
				return false;
			}

			return console_ids.Remove(console.thingIDNumber);
		}
	}
}
