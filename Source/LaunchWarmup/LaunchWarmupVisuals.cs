using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace GravshipRewired
{
	/// <summary>
	/// Visual helper for the grav-engine preparation state.
	///
	/// During active spool-up we want the ship to visibly look like it is waking up:
	/// - smoke across the ship footprint;
	/// - a brightened grav-core glow.
	///
	/// Once the engine is fully prepared we keep only the core-glow pulse so the ship still looks
	/// energized, but the more dramatic smoke effect stops.
	/// </summary>
	public static class LaunchWarmupVisuals
	{
		private const int SMOKE_INTERVAL_TICKS = 18;
		private const int GLOW_INTERVAL_TICKS = 30;
		private const int SMOKE_SAMPLES_PER_BURST = 3;
		private const float MIN_CORE_GLOW_SIZE = 0.35f;
		private const float MAX_CORE_GLOW_SIZE = 1.05f;

		public static void tickWarmupVisuals(GravshipWarmupState state)
		{
			if (state?.engine == null || state.engine.Map == null || !state.engine.Spawned)
			{
				return;
			}

			int current_tick = Find.TickManager.TicksGame;
			if (current_tick % SMOKE_INTERVAL_TICKS == 0)
			{
				emitShipSmoke(state.engine);
			}

			if (current_tick % GLOW_INTERVAL_TICKS == 0)
			{
				emitCoreGlow(state.engine, state.getPercentComplete() / 100f);
			}
		}

		public static void tickPreparedVisuals(GravshipWarmupState state)
		{
			if (state?.engine == null || state.engine.Map == null || !state.engine.Spawned)
			{
				return;
			}

			if (Find.TickManager.TicksGame % GLOW_INTERVAL_TICKS == 0)
			{
				emitCoreGlow(state.engine, 1f);
			}
		}

		private static void emitShipSmoke(Building_GravEngine grav_engine)
		{
			HashSet<IntVec3> ship_cells = GravshipBatteryUtility.collectShipCells(grav_engine);
			if (ship_cells.Count == 0)
			{
				return;
			}

			List<IntVec3> sampled_cells = ship_cells.InRandomOrder().Take(SMOKE_SAMPLES_PER_BURST).ToList();
			for (int index = 0; index < sampled_cells.Count; index++)
			{
				IntVec3 cell = sampled_cells[index];
				float size = Rand.Range(0.8f, 1.8f);
				FleckMaker.ThrowSmoke(cell.ToVector3Shifted(), grav_engine.Map, size);
			}
		}

		private static void emitCoreGlow(Building_GravEngine grav_engine, float charge_fraction)
		{
			float clamped_fraction = Mathf.Clamp01(charge_fraction);
			float glow_size = Mathf.Lerp(MIN_CORE_GLOW_SIZE, MAX_CORE_GLOW_SIZE, clamped_fraction);

			foreach (IntVec3 cell in grav_engine.OccupiedRect())
			{
				FleckMaker.ThrowHeatGlow(cell, grav_engine.Map, glow_size);
			}
		}
	}
}
