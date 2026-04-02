using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace OdysseyGravshipBatteryLaunch
{
	/// <summary>
	/// Despite the historic type name, this class now manages the grav-engine preparation state,
	/// not a pawn-held ritual warmup.
	///
	/// New flow:
	/// 1. Player clicks "Prepare for launch" on the pilot console.
	/// 2. The grav engine starts drawing a very large amount of power from its power net.
	/// 3. Over 2 in-game hours the engine charges up. No pawns are reserved for this.
	/// 4. Once fully prepared, the normal Odyssey launch flow can be used unchanged.
	///
	/// The old implementation delayed the launch ritual itself. This revision is intentionally simpler:
	/// preparation is a separate engine state machine, while the actual launch remains Odyssey's job.
	/// </summary>
	public sealed class GravshipLaunchWarmupManager : MapComponent
	{
		private List<GravshipWarmupState> states = new List<GravshipWarmupState>();
		private bool loaded_unsupported_state;

		public GravshipLaunchWarmupManager(Map map)
			: base(map)
		{
		}

		public override void ExposeData()
		{
			Scribe_Collections.Look(ref states, "states", LookMode.Deep);

			if (Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				loaded_unsupported_state = states.Count > 0;
			}
		}

		public override void FinalizeInit()
		{
			base.FinalizeInit();

			if (!loaded_unsupported_state)
			{
				return;
			}

			foreach (GravshipWarmupState state in states.ToList())
			{
				cancelState(state, "OGBL_PreparationCanceledAfterLoad".Translate().CapitalizeFirst(), true);
			}

			states.Clear();
			loaded_unsupported_state = false;
		}

		public override void MapComponentTick()
		{
			if (states.Count == 0)
			{
				return;
			}

			for (int index = states.Count - 1; index >= 0; index--)
			{
				GravshipWarmupState state = states[index];
				if (state == null)
				{
					states.RemoveAt(index);
					continue;
				}

				if (!tickState(state))
				{
					states.RemoveAt(index);
				}
			}
		}

		public GravshipWarmupState getStateForConsole(Thing console)
		{
			if (console == null)
			{
				return null;
			}

			return states.FirstOrDefault(x => x.console == console);
		}

		public GravshipWarmupState getStateForEngine(Building_GravEngine engine)
		{
			if (engine == null)
			{
				return null;
			}

			return states.FirstOrDefault(x => x.engine == engine);
		}

		public bool isEngineReady(Building_GravEngine engine)
		{
			GravshipWarmupState state = getStateForEngine(engine);
			return state != null && state.phase == GravshipPreparationPhase.Spooled;
		}

		public bool tryStartPreparation(CompPilotConsole console_comp)
		{
			Thing console_thing = console_comp?.parent;
			Building_GravEngine grav_engine = console_comp?.engine;

			if (console_thing == null || grav_engine == null || console_thing.Map != map)
			{
				return false;
			}

			GravshipWarmupState existing_state = getStateForEngine(grav_engine);
			if (existing_state != null)
			{
				string message = existing_state.phase == GravshipPreparationPhase.Spooled
					? "OGBL_AlreadyPrepared".Translate().CapitalizeFirst()
					: "OGBL_PreparationAlreadyInProgress".Translate().CapitalizeFirst();
				Messages.Message(message, console_thing, MessageTypeDefOf.RejectInput, false);
				return false;
			}

			LaunchEnergyState launch_state = GravshipBatteryUtility.buildLaunchEnergyState(grav_engine, console_comp);
			string failure_reason = GravshipBatteryUtility.buildPreparationFailureReason(launch_state);
			if (!failure_reason.NullOrEmpty())
			{
				Messages.Message(failure_reason, console_thing, MessageTypeDefOf.RejectInput, false);
				return false;
			}

			GravshipWarmupState state = new GravshipWarmupState();
			state.engine = grav_engine;
			state.console = console_thing;
			state.phase = GravshipPreparationPhase.Spooling;
			state.start_tick = Find.TickManager.TicksGame;
			state.progress_ticks = 0;
			state.last_status_tick = state.start_tick;
			state.ship_tile_count = launch_state.ship_tile_count;
			state.extra_spool_power_watts = GravshipBatteryUtility.calculateSpoolPowerWatts(launch_state.ship_tile_count);

			states.Add(state);
			applyEnginePowerDraw(state);
			LaunchThreatResponseUtility.triggerThreatResponseOnSpoolUp(map);

			Messages.Message(
				"OGBL_PreparationStarted".Translate(GenDate.ToStringTicksToPeriod(GravshipBatteryUtility.SPOOL_UP_TICKS)).CapitalizeFirst(),
				console_thing,
				MessageTypeDefOf.TaskCompletion,
				false);

			return true;
		}

		public void cancelPreparationForConsole(Thing console, bool silent = false)
		{
			GravshipWarmupState state = getStateForConsole(console);
			if (state == null)
			{
				return;
			}

			cancelState(state, "OGBL_PreparationCanceledManual".Translate().CapitalizeFirst(), silent);
			states.Remove(state);
		}

		private bool tickState(GravshipWarmupState state)
		{
			if (state.engine == null || state.console == null)
			{
				return false;
			}

			if (!state.engine.Spawned || !state.console.Spawned || state.engine.Map != map || state.console.Map != map)
			{
				restoreEnginePowerDraw(state.engine);
				return false;
			}

			applyEnginePowerDraw(state);

			CompPilotConsole console_comp = (state.console as ThingWithComps)?.TryGetComp<CompPilotConsole>();
			LaunchEnergyState launch_state = GravshipBatteryUtility.buildLaunchEnergyState(state.engine, console_comp);

			if (!launch_state.grav_core_powered)
			{
				cancelState(state, "OGBL_PreparationCanceledCorePower".Translate().CapitalizeFirst(), false);
				return false;
			}

			if (!launch_state.pilot_console_powered)
			{
				cancelState(state, "OGBL_PreparationCanceledConsolePower".Translate().CapitalizeFirst(), false);
				return false;
			}

			int current_tick = Find.TickManager.TicksGame;
			if (state.phase == GravshipPreparationPhase.Spooling)
			{
				state.progress_ticks++;
				LaunchWarmupVisuals.tickWarmupVisuals(state);

				if (current_tick - state.last_status_tick >= GravshipBatteryUtility.SPOOL_STATUS_MESSAGE_INTERVAL_TICKS)
				{
					state.last_status_tick = current_tick;
					Messages.Message(
						"OGBL_PreparationProgress".Translate(state.getPercentComplete().ToString("0")).CapitalizeFirst(),
						state.console,
						MessageTypeDefOf.SilentInput,
						false);
				}

				if (state.progress_ticks >= GravshipBatteryUtility.SPOOL_UP_TICKS)
				{
					state.phase = GravshipPreparationPhase.Spooled;
					state.completed_tick = current_tick;
					Messages.Message("OGBL_PreparationComplete".Translate().CapitalizeFirst(), state.console, MessageTypeDefOf.TaskCompletion, false);
				}

				return true;
			}

			LaunchWarmupVisuals.tickPreparedVisuals(state);
			return true;
		}

		private void cancelState(GravshipWarmupState state, string message, bool silent)
		{
			restoreEnginePowerDraw(state.engine);

			if (!silent && state.console != null && !message.NullOrEmpty())
			{
				Messages.Message(message, state.console, MessageTypeDefOf.RejectInput, false);
			}
		}

		private void applyEnginePowerDraw(GravshipWarmupState state)
		{
			if (state == null || state.engine == null)
			{
				return;
			}

			float total_draw_watts = GravshipBatteryUtility.getBasePowerConsumption(state.engine) + state.extra_spool_power_watts;
			GravshipBatteryUtility.setThingPowerDraw(state.engine, total_draw_watts);
		}

		private void restoreEnginePowerDraw(Building_GravEngine engine)
		{
			if (engine == null)
			{
				return;
			}

			GravshipBatteryUtility.restoreThingBasePowerDraw(engine);
		}
	}

	public enum GravshipPreparationPhase
	{
		Spooling = 0,
		Spooled = 1,
	}

	/// <summary>
	/// Serializable preparation state for one grav engine.
	/// </summary>
	public sealed class GravshipWarmupState : IExposable
	{
		public Building_GravEngine engine;
		public Thing console;
		public GravshipPreparationPhase phase;
		public int start_tick;
		public int completed_tick;
		public int progress_ticks;
		public int last_status_tick;
		public int ship_tile_count;
		public float extra_spool_power_watts;

		public float getHoursRemaining()
		{
			int ticks_left = Math.Max(0, GravshipBatteryUtility.SPOOL_UP_TICKS - progress_ticks);
			return ticks_left / 2500f;
		}

		public float getPercentComplete()
		{
			if (phase == GravshipPreparationPhase.Spooled)
			{
				return 100f;
			}

			return Math.Min(100f, (progress_ticks / (float)GravshipBatteryUtility.SPOOL_UP_TICKS) * 100f);
		}

		public void ExposeData()
		{
			Scribe_References.Look(ref engine, "engine");
			Scribe_References.Look(ref console, "console");
			Scribe_Values.Look(ref phase, "phase", GravshipPreparationPhase.Spooling);
			Scribe_Values.Look(ref start_tick, "start_tick", 0);
			Scribe_Values.Look(ref completed_tick, "completed_tick", 0);
			Scribe_Values.Look(ref progress_ticks, "progress_ticks", 0);
			Scribe_Values.Look(ref last_status_tick, "last_status_tick", 0);
			Scribe_Values.Look(ref ship_tile_count, "ship_tile_count", 0);
			Scribe_Values.Look(ref extra_spool_power_watts, "extra_spool_power_watts", 0f);
		}
	}
}
