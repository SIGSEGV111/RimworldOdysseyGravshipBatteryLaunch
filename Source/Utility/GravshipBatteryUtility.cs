using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace OdysseyGravshipBatteryLaunch
{
	/// <summary>
	/// Shared helper methods for the mod.
	///
	/// This file is intentionally large because it centralizes the pieces that multiple Harmony patches
	/// and warmup systems need to agree on: how launch energy is calculated, how batteries are found,
	/// how powered parts are detected, and how pilot/copilot pawns are selected.
	///
	/// For someone new to RimWorld modding, the key idea is that most gameplay code works on spawned
	/// Things and their Comps:
	/// - a building is usually a ThingWithComps;
	/// - behavior modules such as power or batteries live in comps like CompPowerTrader/
	///   CompPowerBattery;
	/// - the map exposes collections such as listerThings for fast runtime queries.
	/// </summary>
	public static class GravshipBatteryUtility
	{
		// Custom launch-energy rule requested for this mod: 100 stored energy units per connected
		// gravship footprint tile. The epsilon avoids float-comparison edge cases.
		public const float ENERGY_PER_TILE = 100f;
		public const float ENERGY_EPSILON = 0.0001f;

		public const string GRAV_ENGINE_DEF_NAME = "GravEngine";
		public const string PILOT_CONSOLE_DEF_NAME = "PilotConsole";
		public const string SMALL_THRUSTER_DEF_NAME = "SmallThruster";
		public const string LARGE_THRUSTER_DEF_NAME = "LargeThruster";

		// RimWorld uses 2500 ticks per in-game hour, so a 2-hour spool-up is 5000 ticks.
		public const int SPOOL_UP_TICKS = 5000;

		// Spool-up now charges the grav engine over time instead of draining batteries instantly.
		// Requested energy budget: 15 Wd per connected ship tile over 2 in-game hours.
		// 15 Wd / (1/12 day) = 180 W average draw per tile during spool-up.
		public const float SPOOL_ENERGY_PER_TILE_WD = 15f;
		public const float SPOOL_POWER_WATTS_PER_TILE = 180f;
		public const int SPOOL_STATUS_MESSAGE_INTERVAL_TICKS = 250;

		// Battery internals are not guaranteed to be a stable public API. We therefore cache reflection
		// handles once and reuse them instead of repeatedly scanning metadata every time we touch energy.
		private static readonly PropertyInfo STORED_ENERGY_PROPERTY = AccessTools.Property(typeof(CompPowerBattery), "StoredEnergy");
		private static readonly FieldInfo STORED_ENERGY_FIELD = AccessTools.Field(typeof(CompPowerBattery), "storedEnergyInt") ?? AccessTools.Field(typeof(CompPowerBattery), "storedEnergy");
		private static readonly MethodInfo ADD_ENERGY_METHOD = AccessTools.Method(typeof(CompPowerBattery), "AddEnergy", new[] { typeof(float) });

		// The grav engine power draw is adjusted dynamically while preparing/spooled.
		private static readonly PropertyInfo POWER_OUTPUT_PROPERTY = AccessTools.Property(typeof(CompPowerTrader), "PowerOutput");
		private static readonly FieldInfo POWER_OUTPUT_FIELD = AccessTools.Field(typeof(CompPowerTrader), "powerOutputInt") ?? AccessTools.Field(typeof(CompPowerTrader), "powerOutput");

		// Optional Odyssey stat used when choosing the most suitable pilot/copilot.
		private static readonly StatDef PILOTING_ABILITY_STAT = DefDatabase<StatDef>.GetNamedSilentFail("PilotingAbility");

		/// <summary>
		/// Produces a single snapshot object containing everything launch validation needs.
		///
		/// The point of bundling this into LaunchEnergyState is consistency: CanLaunch(), warmup ticking,
		/// and fuel-consumption hooks all evaluate the same rules from the same data model.
		/// </summary>
		public static LaunchEnergyState buildLaunchEnergyState(Building_GravEngine grav_engine, CompPilotConsole active_console)
		{
			if (grav_engine == null || grav_engine.Map == null)
			{
				return new LaunchEnergyState();
			}

			// Odyssey already knows how to compute the connected gravship footprint. Reusing that helper is
			// much safer than trying to approximate the ship shape ourselves.
			HashSet<IntVec3> ship_cells = collectShipCells(grav_engine);
			int ship_tile_count = ship_cells.Count;
			float required_energy = calculateRequiredEnergy(ship_tile_count);

			// The grav core's power net is the authoritative source of battery energy for takeoff.
			CompPowerTrader grav_engine_power = grav_engine.TryGetComp<CompPowerTrader>();
			PowerNet core_power_net = grav_engine_power?.PowerNet;

			ThingWithComps pilot_console_thing = active_console?.parent as ThingWithComps;
			if (pilot_console_thing == null)
			{
				pilot_console_thing = findActiveConsoleForEngine(grav_engine);
			}

			List<ThingWithComps> thrusters = collectThrusters(grav_engine.Map, ship_cells);
			List<ThingWithComps> powered_thrusters = thrusters.Where(x => isThingPowered(x)).ToList();

			List<BatteryEnergyRecord> batteries = collectBatteriesOnNet(core_power_net);
			float available_energy = 0f;

			foreach (BatteryEnergyRecord battery_record in batteries)
			{
				available_energy += battery_record.stored_energy;
			}

			return new LaunchEnergyState(
				required_energy,
				available_energy,
				batteries,
				ship_tile_count,
				isThingPowered(grav_engine),
				isThingPoweredOnNet(pilot_console_thing, core_power_net),
				thrusters.Count,
				powered_thrusters.Count,
				core_power_net != null,
				core_power_net,
				pilot_console_thing,
				thrusters,
				powered_thrusters);
		}

		/// <summary>
		/// Builds a localized multi-line error string describing why launch is currently illegal.
		///
		/// Returning a string instead of an enum is convenient in RimWorld because the same text can be
		/// plugged directly into AcceptanceReport or a message popup.
		/// </summary>
		public static string buildFailureReason(LaunchEnergyState launch_state, bool require_all_thrusters_powered)
		{
			if (launch_state == null)
			{
				return string.Empty;
			}

			List<string> reasons = new List<string>();

			if (!launch_state.has_core_power_net)
			{
				reasons.Add("OGBL_NoCorePowerNet".Translate().CapitalizeFirst());
			}

			if (!launch_state.grav_core_powered)
			{
				reasons.Add("OGBL_GravCoreNotPowered".Translate().CapitalizeFirst());
			}

			if (!launch_state.pilot_console_powered)
			{
				reasons.Add("OGBL_PilotConsoleNotPowered".Translate().CapitalizeFirst());
			}

			if (require_all_thrusters_powered && launch_state.total_thruster_count > 0 && launch_state.powered_thruster_count < launch_state.total_thruster_count)
			{
				reasons.Add(
					"OGBL_ThrustersNotPowered"
						.Translate(
							launch_state.powered_thruster_count.ToString(),
							launch_state.total_thruster_count.ToString())
						.CapitalizeFirst());
			}

			if (launch_state.required_energy > ENERGY_EPSILON && launch_state.available_energy + ENERGY_EPSILON < launch_state.required_energy)
			{
				reasons.Add(
					"OGBL_NotEnoughStoredPower"
						.Translate(
							launch_state.available_energy.ToString("0"),
							launch_state.required_energy.ToString("0"),
							launch_state.ship_tile_count.ToString())
						.CapitalizeFirst());
			}

			return string.Join("\n", reasons);
		}

		/// <summary>
		/// Smaller validation set used when starting engine preparation.
		///
		/// Preparation only cares whether the grav core and active console are actually powered on the
		/// grav core's net. It deliberately does not require a large stored-energy reserve up front,
		/// because the new design consumes power progressively through the normal power system.
		/// </summary>
		public static string buildPreparationFailureReason(LaunchEnergyState launch_state)
		{
			if (launch_state == null)
			{
				return string.Empty;
			}

			List<string> reasons = new List<string>();

			if (!launch_state.has_core_power_net)
			{
				reasons.Add("OGBL_NoCorePowerNet".Translate().CapitalizeFirst());
			}

			if (!launch_state.grav_core_powered)
			{
				reasons.Add("OGBL_GravCoreNotPowered".Translate().CapitalizeFirst());
			}

			if (!launch_state.pilot_console_powered)
			{
				reasons.Add("OGBL_PilotConsoleNotPowered".Translate().CapitalizeFirst());
			}

			return string.Join("\n", reasons);
		}

		/// <summary>
		/// Converts ship size into the requested additional spool-up power draw.
		/// </summary>
		public static float calculateSpoolPowerWatts(int ship_tile_count)
		{
			if (ship_tile_count <= 0)
			{
				return 0f;
			}

			return ship_tile_count * SPOOL_POWER_WATTS_PER_TILE;
		}

		/// <summary>
		/// Returns the base power consumption defined on the thing's CompProperties_Power.
		/// The XML patch already sets the grav engine to 1200 W. We add the spool draw on top of that.
		/// </summary>
		public static float getBasePowerConsumption(ThingWithComps thing)
		{
			CompPowerTrader power_trader = thing?.TryGetComp<CompPowerTrader>();
			if (power_trader?.Props == null)
			{
				return 0f;
			}

			return power_trader.Props.basePowerConsumption;
		}

		/// <summary>
		/// Applies a desired power draw to a power trader by writing a negative PowerOutput.
		///
		/// In RimWorld, consumers use negative power output and generators use positive output.
		/// </summary>
		public static void setThingPowerDraw(ThingWithComps thing, float power_draw_watts)
		{
			CompPowerTrader power_trader = thing?.TryGetComp<CompPowerTrader>();
			if (power_trader == null)
			{
				return;
			}

			float desired_output = -Math.Abs(power_draw_watts);

			if (POWER_OUTPUT_PROPERTY != null && POWER_OUTPUT_PROPERTY.CanWrite)
			{
				POWER_OUTPUT_PROPERTY.SetValue(power_trader, desired_output, null);
				return;
			}

			if (POWER_OUTPUT_FIELD != null)
			{
				POWER_OUTPUT_FIELD.SetValue(power_trader, desired_output);
				return;
			}

			Log.Warning("[OdysseyGravshipBatteryLaunch] Could not set CompPowerTrader power output.");
		}

		/// <summary>
		/// Restores the thing's normal XML-defined base power consumption.
		/// </summary>
		public static void restoreThingBasePowerDraw(ThingWithComps thing)
		{
			setThingPowerDraw(thing, getBasePowerConsumption(thing));
		}


		/// <summary>
		/// Draws the requested amount of energy from a set of batteries.
		///
		/// The implementation intentionally drains the fullest batteries first. That choice is not required
		/// by the rules, but it tends to minimize how many batteries are partially depleted after launch.
		/// </summary>
		public static void consumeEnergy(List<BatteryEnergyRecord> batteries, float required_energy)
		{
			if (batteries == null || batteries.Count == 0 || required_energy <= ENERGY_EPSILON)
			{
				return;
			}

			batteries.Sort((left, right) => right.stored_energy.CompareTo(left.stored_energy));

			float remaining_energy = required_energy;

			foreach (BatteryEnergyRecord battery_record in batteries)
			{
				if (remaining_energy <= ENERGY_EPSILON)
				{
					break;
				}

				float current_energy = readStoredEnergy(battery_record.battery);
				if (current_energy <= ENERGY_EPSILON)
				{
					continue;
				}

				float energy_to_draw = Math.Min(current_energy, remaining_energy);
				writeStoredEnergyDelta(battery_record.battery, -energy_to_draw);
				remaining_energy -= energy_to_draw;
			}

			if (remaining_energy > 0.1f)
			{
				Log.Warning($"[OdysseyGravshipBatteryLaunch] Launch energy shortfall after draw attempt: {remaining_energy:0.##}.");
			}
		}

		/// <summary>
		/// Returns every substructure cell connected to the grav engine according to Odyssey's own logic.
		/// </summary>
		public static HashSet<IntVec3> collectShipCells(Building_GravEngine grav_engine)
		{
			HashSet<IntVec3> ship_cells = new HashSet<IntVec3>();
			int max_cells = int.MaxValue;

			GravshipUtility.GetConnectedSubstructure(grav_engine, ship_cells, max_cells, true);
			return ship_cells;
		}

		/// <summary>
		/// Scans the map for thrusters that overlap the ship footprint.
		///
		/// RimWorld mods commonly search via listerThings instead of calling expensive all-map helpers.
		/// The ThingRequestGroup limits the scan to artificial buildings only.
		/// </summary>
		public static List<ThingWithComps> collectThrusters(Map map, HashSet<IntVec3> ship_cells)
		{
			List<ThingWithComps> thrusters = new List<ThingWithComps>();
			if (map == null || ship_cells == null || ship_cells.Count == 0)
			{
				return thrusters;
			}

			foreach (Thing thing in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
			{
				if (thing is not ThingWithComps thing_with_comps || !thing_with_comps.Spawned)
				{
					continue;
				}

				string def_name = thing_with_comps.def?.defName;
				if (def_name != SMALL_THRUSTER_DEF_NAME && def_name != LARGE_THRUSTER_DEF_NAME)
				{
					continue;
				}

				if (!isThingInsideShipFootprint(thing_with_comps, ship_cells))
				{
					continue;
				}

				thrusters.Add(thing_with_comps);
			}

			return thrusters;
		}

		/// <summary>
		/// Returns the batteries currently attached to the grav core's power net.
		///
		/// This is the key difference from earlier versions that counted batteries by ship footprint.
		/// Physical position no longer matters; network membership does.
		/// </summary>
		public static List<BatteryEnergyRecord> collectBatteriesOnNet(PowerNet core_power_net)
		{
			List<BatteryEnergyRecord> batteries = new List<BatteryEnergyRecord>();
			if (core_power_net == null)
			{
				return batteries;
			}

			foreach (CompPowerBattery battery in core_power_net.batteryComps)
			{
				if (battery == null || battery.parent == null || !battery.parent.Spawned)
				{
					continue;
				}

				batteries.Add(new BatteryEnergyRecord(battery, readStoredEnergy(battery)));
			}

			return batteries;
		}

		/// <summary>
		/// Best-effort fallback used when the current active console was not passed in explicitly.
		///
		/// The code simply picks the nearest pilot console to the grav engine. That is a heuristic, not a
		/// guaranteed "official" Odyssey concept of an active console, but it is good enough for validation
		/// paths where the actual launch target is not directly available.
		/// </summary>
		public static ThingWithComps findActiveConsoleForEngine(Building_GravEngine grav_engine)
		{
			if (grav_engine == null || grav_engine.Map == null)
			{
				return null;
			}

			ThingWithComps closest_console = null;
			float closest_distance = float.MaxValue;

			foreach (Thing thing in grav_engine.Map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
			{
				if (thing is not ThingWithComps thing_with_comps || thing_with_comps.def?.defName != PILOT_CONSOLE_DEF_NAME)
				{
					continue;
				}

				float distance = thing.Position.DistanceToSquared(grav_engine.Position);
				if (distance < closest_distance)
				{
					closest_console = thing_with_comps;
					closest_distance = distance;
				}
			}

			return closest_console;
		}

		/// <summary>
		/// Returns true if any occupied cell of the given thing overlaps the ship footprint.
		/// This matters because buildings can occupy more than one map cell.
		/// </summary>
		public static bool isThingInsideShipFootprint(Thing thing, HashSet<IntVec3> ship_cells)
		{
			if (thing == null || ship_cells == null || ship_cells.Count == 0)
			{
				return false;
			}

			foreach (IntVec3 cell in thing.OccupiedRect())
			{
				if (ship_cells.Contains(cell))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// Generic powered check for any building-like thing.
		///
		/// If a thing has no CompPowerTrader, we treat it as inherently powered for the purpose of this mod.
		/// </summary>
		public static bool isThingPowered(ThingWithComps thing)
		{
			if (thing == null)
			{
				return false;
			}

			CompPowerTrader power_trader = thing.TryGetComp<CompPowerTrader>();
			if (power_trader == null)
			{
				return true;
			}

			return power_trader.PowerOn;
		}

		/// <summary>
		/// Stronger powered check that also requires the thing to be attached to the same power net as the
		/// grav core. This is used for the pilot console because the request explicitly tied flight systems
		/// to the grav core's power network.
		/// </summary>
		public static bool isThingPoweredOnNet(ThingWithComps thing, PowerNet expected_power_net)
		{
			if (thing == null || expected_power_net == null)
			{
				return false;
			}

			CompPowerTrader power_trader = thing.TryGetComp<CompPowerTrader>();
			if (power_trader == null)
			{
				return false;
			}

			return power_trader.PowerOn && power_trader.PowerNet == expected_power_net;
		}

		/// <summary>
		/// Reads battery energy in a version-tolerant way.
		///
		/// Some game builds expose stored energy as a property, others keep it in a backing field. We try
		/// both so the mod is more resilient to small internal API changes.
		/// </summary>
		public static float readStoredEnergy(CompPowerBattery battery)
		{
			if (battery == null)
			{
				return 0f;
			}

			if (STORED_ENERGY_PROPERTY != null)
			{
				object value = STORED_ENERGY_PROPERTY.GetValue(battery, null);
				if (value is float stored_energy)
				{
					return stored_energy;
				}
			}

			if (STORED_ENERGY_FIELD != null)
			{
				object value = STORED_ENERGY_FIELD.GetValue(battery);
				if (value is float stored_energy)
				{
					return stored_energy;
				}
			}

			Log.Warning("[OdysseyGravshipBatteryLaunch] Could not read CompPowerBattery stored energy. Assuming 0.");
			return 0f;
		}

		/// <summary>
		/// Applies an energy delta to a battery.
		///
		/// Prefer the game's own AddEnergy method when available so side effects remain consistent with
		/// vanilla expectations. If that method is unavailable, fall back to directly writing the field.
		/// </summary>
		public static void writeStoredEnergyDelta(CompPowerBattery battery, float energy_delta)
		{
			if (battery == null || Math.Abs(energy_delta) <= ENERGY_EPSILON)
			{
				return;
			}

			if (energy_delta > 0f && ADD_ENERGY_METHOD != null)
			{
				ADD_ENERGY_METHOD.Invoke(battery, new object[] { energy_delta });
				return;
			}

			float updated_energy = Math.Max(0f, readStoredEnergy(battery) + energy_delta);

			if (STORED_ENERGY_PROPERTY != null && STORED_ENERGY_PROPERTY.CanWrite)
			{
				STORED_ENERGY_PROPERTY.SetValue(battery, updated_energy, null);
				return;
			}

			if (STORED_ENERGY_FIELD != null)
			{
				STORED_ENERGY_FIELD.SetValue(battery, updated_energy);
				return;
			}

			Log.Warning("[OdysseyGravshipBatteryLaunch] Could not modify CompPowerBattery stored energy.");
		}

		/// <summary>
		/// Converts ship size into launch-energy demand.
		/// </summary>
		public static float calculateRequiredEnergy(int ship_tile_count)
		{
			if (ship_tile_count <= 0)
			{
				return 0f;
			}

			return ship_tile_count * ENERGY_PER_TILE;
		}

		/// <summary>
		/// Extracts all pawns participating in the launch ritual.
		///
		/// The RitualRoleAssignments object is not especially pleasant to consume from mods because its
		/// exact structure is not a small stable public API. We therefore try a few likely members first,
		/// then fall back to a reflection-based recursive scan.
		/// </summary>
		public static List<Pawn> collectAssignedPawns(RitualRoleAssignments assignments, Pawn organizer, Map map)
		{
			HashSet<Pawn> pawns = new HashSet<Pawn>();

			addPawnsFromObject(readMemberValue(assignments, "allAssignedPawns"), pawns);
			addPawnsFromObject(readMemberValue(assignments, "allPawns"), pawns);
			addPawnsFromObject(readMemberValue(assignments, "assignedPawns"), pawns);

			if (pawns.Count == 0)
			{
				int budget = 256;
				scanForPawns(assignments, pawns, 0, new HashSet<object>(ReferenceEqualityComparer.Instance), ref budget);
			}

			if (organizer != null)
			{
				pawns.Add(organizer);
			}

			return pawns.Where(x => x != null && x.Spawned && x.Map == map).Distinct().ToList();
		}

		/// <summary>
		/// Finds pawns assigned to ritual roles whose name/label contains the requested fragments.
		/// Example: "pilot" or "copilot".
		/// </summary>
		public static List<Pawn> collectRolePawns(RitualRoleAssignments assignments, Map map, params string[] role_name_fragments)
		{
			HashSet<Pawn> pawns = new HashSet<Pawn>();
			int budget = 256;
			scanForRoleAssignments(assignments, pawns, 0, new HashSet<object>(ReferenceEqualityComparer.Instance), ref budget, role_name_fragments);

			return pawns.Where(x => x != null && x.Spawned && x.Map == map).Distinct().ToList();
		}

		/// <summary>
		/// Reads Odyssey's piloting stat, or 0 if the stat is unavailable for any reason.
		/// </summary>
		public static float getPilotingScore(Pawn pawn)
		{
			if (pawn == null || PILOTING_ABILITY_STAT == null)
			{
				return 0f;
			}

			try
			{
				return pawn.GetStatValue(PILOTING_ABILITY_STAT, true);
			}
			catch
			{
				return 0f;
			}
		}

		/// <summary>
		/// Picks the pilot and copilot that should occupy the console during spool-up.
		///
		/// Preference order:
		/// 1. pawns explicitly assigned to a pilot role;
		/// 2. pawns explicitly assigned to a copilot role;
		/// 3. highest piloting-score fallbacks from all assigned pawns.
		/// </summary>
		public static List<Pawn> chooseSpoolCrew(RitualRoleAssignments assignments, Pawn organizer, Map map)
		{
			List<Pawn> all_assigned_pawns = collectAssignedPawns(assignments, organizer, map);
			List<Pawn> pilot_candidates = collectRolePawns(assignments, map, "pilot");
			List<Pawn> copilot_candidates = collectRolePawns(assignments, map, "copilot", "co-pilot", "co pilot");

			HashSet<Pawn> selected = new HashSet<Pawn>();

			Pawn pilot = pilot_candidates
				.Where(x => !copilot_candidates.Contains(x))
				.OrderByDescending(getPilotingScore)
				.FirstOrDefault();

			if (pilot != null)
			{
				selected.Add(pilot);
			}

			Pawn copilot = copilot_candidates
				.Where(x => x != pilot)
				.OrderByDescending(getPilotingScore)
				.FirstOrDefault();

			if (copilot != null)
			{
				selected.Add(copilot);
			}

			if (selected.Count < 2)
			{
				foreach (Pawn pawn in all_assigned_pawns.OrderByDescending(getPilotingScore))
				{
					if (selected.Add(pawn) && selected.Count >= 2)
					{
						break;
					}
				}
			}

			return selected.ToList();
		}

		/// <summary>
		/// Chooses the primary standing cell for the pilot.
		///
		/// InteractionCell is the first choice because that is the standard RimWorld concept for a place
		/// where a pawn uses a building. If that is invalid we degrade gracefully to nearby walkable cells.
		/// </summary>
		public static IntVec3 findPilotCell(Thing console)
		{
			if (console == null)
			{
				return IntVec3.Invalid;
			}

			IntVec3 interaction_cell = console.InteractionCell;
			if (interaction_cell.IsValid && interaction_cell.InBounds(console.Map) && interaction_cell.Walkable(console.Map))
			{
				return interaction_cell;
			}

			foreach (IntVec3 cell in console.OccupiedRect().ExpandedBy(1))
			{
				if (cell.InBounds(console.Map) && cell.Walkable(console.Map))
				{
					return cell;
				}
			}

			return console.Position;
		}

		/// <summary>
		/// Chooses a nearby but distinct standing cell for the copilot.
		/// </summary>
		public static IntVec3 findCopilotCell(Thing console, IntVec3 pilot_cell)
		{
			if (console == null || console.Map == null)
			{
				return IntVec3.Invalid;
			}

			List<IntVec3> candidates = new List<IntVec3>();
			foreach (IntVec3 cell in console.OccupiedRect().ExpandedBy(1))
			{
				if (!cell.InBounds(console.Map))
				{
					continue;
				}

				if (!cell.Walkable(console.Map))
				{
					continue;
				}

				if (cell == pilot_cell)
				{
					continue;
				}

				candidates.Add(cell);
			}

			IntVec3 result = candidates
				.OrderBy(x => x.DistanceToSquared(pilot_cell))
				.ThenBy(x => x.DistanceToSquared(console.Position))
				.FirstOrDefault();

			return result.IsValid ? result : IntVec3.Invalid;
		}

		/// <summary>
		/// Reflection helper that reads either a property or a field with the same logical name.
		/// </summary>
		private static object readMemberValue(object source, string member_name)
		{
			if (source == null)
			{
				return null;
			}

			PropertyInfo property = AccessTools.Property(source.GetType(), member_name);
			if (property != null)
			{
				try
				{
					return property.GetValue(source, null);
				}
				catch
				{
				}
			}

			FieldInfo field = AccessTools.Field(source.GetType(), member_name);
			if (field != null)
			{
				try
				{
					return field.GetValue(source);
				}
				catch
				{
				}
			}

			return null;
		}

		/// <summary>
		/// Normalizes a value that might be a single pawn or a nested enumerable of pawns into a set.
		/// </summary>
		private static void addPawnsFromObject(object value, HashSet<Pawn> pawns)
		{
			if (value == null)
			{
				return;
			}

			if (value is Pawn pawn)
			{
				pawns.Add(pawn);
				return;
			}

			if (value is IEnumerable enumerable && value is not string)
			{
				foreach (object item in enumerable)
				{
					addPawnsFromObject(item, pawns);
				}
			}
		}

		/// <summary>
		/// Recursive "last resort" pawn discovery.
		///
		/// This kind of reflection walk is not pretty, but it is often practical in RimWorld modding when
		/// you need to integrate with internal objects that were not designed as a public API surface.
		/// The depth cap prevents runaway recursion on large object graphs.
		/// </summary>
		private static void scanForPawns(object value, HashSet<Pawn> pawns, int depth, HashSet<object> visited, ref int budget)
		{
			if (value == null || depth > 4 || budget <= 0)
			{
				return;
			}

			if (value is Pawn pawn)
			{
				pawns.Add(pawn);
				return;
			}

			Type type = value.GetType();
			if (type.IsPrimitive || type.IsEnum || value is string)
			{
				return;
			}

			if (!type.IsValueType && !visited.Add(value))
			{
				return;
			}

			budget--;

			if (value is Thing || value is ThingComp || value is Map || value is Faction || value is Job || value is Lord)
			{
				return;
			}

			if (value is IDictionary dictionary)
			{
				foreach (DictionaryEntry entry in dictionary)
				{
					scanForPawns(entry.Key, pawns, depth + 1, visited, ref budget);
					scanForPawns(entry.Value, pawns, depth + 1, visited, ref budget);
					if (budget <= 0)
					{
						break;
					}
				}

				return;
			}

			if (value is IEnumerable enumerable && value is not string)
			{
				foreach (object item in enumerable)
				{
					scanForPawns(item, pawns, depth + 1, visited, ref budget);
					if (budget <= 0)
					{
						break;
					}
				}

				return;
			}

			foreach (FieldInfo field in AccessTools.GetDeclaredFields(type))
			{
				scanForPawns(safeGetValue(field, value), pawns, depth + 1, visited, ref budget);
				if (budget <= 0)
				{
					break;
				}
			}

			if (budget <= 0)
			{
				return;
			}

			foreach (PropertyInfo property in AccessTools.GetDeclaredProperties(type))
			{
				if (property.GetIndexParameters().Length > 0 || property.GetMethod == null)
				{
					continue;
				}

				scanForPawns(safeGetValue(property, value), pawns, depth + 1, visited, ref budget);
				if (budget <= 0)
				{
					break;
				}
			}
		}

		/// <summary>
		/// Recursive scan that tries to infer role assignments by matching member names/labels such as
		/// "pilot" and "copilot".
		/// </summary>
		private static void scanForRoleAssignments(object value, HashSet<Pawn> pawns, int depth, HashSet<object> visited, ref int budget, params string[] role_name_fragments)
		{
			if (value == null || depth > 4 || budget <= 0)
			{
				return;
			}

			Type type = value.GetType();
			if (type.IsPrimitive || type.IsEnum || value is string)
			{
				return;
			}

			if (!type.IsValueType && !visited.Add(value))
			{
				return;
			}

			budget--;

			if (value is IDictionary dictionary)
			{
				foreach (DictionaryEntry entry in dictionary)
				{
					bool key_matches = memberMatchesRole(entry.Key, role_name_fragments);
					bool value_matches = memberMatchesRole(entry.Value, role_name_fragments);

					if (key_matches)
					{
						addPawnsFromObject(entry.Value, pawns);
					}

					if (value_matches)
					{
						addPawnsFromObject(entry.Key, pawns);
					}

					scanForRoleAssignments(entry.Key, pawns, depth + 1, visited, ref budget, role_name_fragments);
					scanForRoleAssignments(entry.Value, pawns, depth + 1, visited, ref budget, role_name_fragments);
					if (budget <= 0)
					{
						break;
					}
				}

				return;
			}

			if (value is IEnumerable enumerable && value is not string)
			{
				foreach (object item in enumerable)
				{
					scanForRoleAssignments(item, pawns, depth + 1, visited, ref budget, role_name_fragments);
					if (budget <= 0)
					{
						break;
					}
				}

				return;
			}

			List<object> matching_members = new List<object>();
			List<object> other_members = new List<object>();

			foreach (FieldInfo field in AccessTools.GetDeclaredFields(type))
			{
				object member_value = safeGetValue(field, value);
				if (memberMatchesRole(field.Name, role_name_fragments) || memberMatchesRole(member_value, role_name_fragments))
				{
					matching_members.Add(member_value);
				}
				else
				{
					other_members.Add(member_value);
				}
			}

			foreach (PropertyInfo property in AccessTools.GetDeclaredProperties(type))
			{
				if (property.GetIndexParameters().Length > 0 || property.GetMethod == null)
				{
					continue;
				}

				object member_value = safeGetValue(property, value);
				if (memberMatchesRole(property.Name, role_name_fragments) || memberMatchesRole(member_value, role_name_fragments))
				{
					matching_members.Add(member_value);
				}
				else
				{
					other_members.Add(member_value);
				}
			}

			if (matching_members.Count > 0)
			{
				foreach (object member in other_members)
				{
					addPawnsFromObject(member, pawns);
				}
			}

			foreach (object member in matching_members)
			{
				scanForRoleAssignments(member, pawns, depth + 1, visited, ref budget, role_name_fragments);
				if (budget <= 0)
				{
					break;
				}
			}

			if (budget <= 0)
			{
				return;
			}

			foreach (object member in other_members)
			{
				scanForRoleAssignments(member, pawns, depth + 1, visited, ref budget, role_name_fragments);
				if (budget <= 0)
				{
					break;
				}
			}
		}

		/// <summary>
		/// Reflection helper that swallows exceptions and returns null when a member cannot be read.
		/// Silent failure is intentional here because these scans are heuristic fallbacks, not core logic.
		/// </summary>
		private static object safeGetValue(MemberInfo member, object source)
		{
			try
			{
				if (member is FieldInfo field)
				{
					return field.GetValue(source);
				}

				if (member is PropertyInfo property)
				{
					return property.GetValue(source, null);
				}
			}
			catch
			{
			}

			return null;
		}

		/// <summary>
		/// Best-effort string matching for role names/labels/ids.
		/// </summary>
		private static bool memberMatchesRole(object value, params string[] role_name_fragments)
		{
			if (value == null)
			{
				return false;
			}

			if (value is string text)
			{
				string lowered = text.ToLowerInvariant();
				return role_name_fragments.Any(x => lowered.Contains(x.ToLowerInvariant()));
			}

			string label = safeReadStringProperty(value, "label");
			string def_name = safeReadStringProperty(value, "defName");
			string id = safeReadStringProperty(value, "id");
			string short_label = safeReadStringProperty(value, "LabelShort");

			List<string> parts = new List<string>()
			{
				label,
				def_name,
				id,
				short_label,
				value.GetType().Name
			};

			foreach (string part in parts)
			{
				if (part.NullOrEmpty())
				{
					continue;
				}

				string lowered = part.ToLowerInvariant();
				if (role_name_fragments.Any(x => lowered.Contains(x.ToLowerInvariant())))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// Reads a string property without throwing if the property is missing or inaccessible.
		/// </summary>
		private static string safeReadStringProperty(object value, string property_name)
		{
			PropertyInfo property = AccessTools.Property(value.GetType(), property_name);
			if (property == null)
			{
				return null;
			}

			try
			{
				return property.GetValue(value, null) as string;
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
	{
		public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

		public new bool Equals(object left, object right)
		{
			return ReferenceEquals(left, right);
		}

		public int GetHashCode(object value)
		{
			return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
		}
	}

	/// <summary>
	/// Immutable snapshot of all launch-relevant electrical state.
	///
	/// Using a data carrier like this makes Harmony patches much easier to test and reason about than
	/// recomputing half a dozen related values independently in multiple places.
	/// </summary>
	public sealed class LaunchEnergyState
	{
		public readonly float required_energy;
		public readonly float available_energy;
		public readonly List<BatteryEnergyRecord> batteries;
		public readonly int ship_tile_count;
		public readonly bool grav_core_powered;
		public readonly bool pilot_console_powered;
		public readonly int total_thruster_count;
		public readonly int powered_thruster_count;
		public readonly bool has_core_power_net;
		public readonly PowerNet core_power_net;
		public readonly ThingWithComps active_console;
		public readonly List<ThingWithComps> thrusters;
		public readonly List<ThingWithComps> powered_thrusters;

		public LaunchEnergyState()
			: this(0f, 0f, new List<BatteryEnergyRecord>(), 0, false, false, 0, 0, false, null, null, new List<ThingWithComps>(), new List<ThingWithComps>())
		{
		}

		public LaunchEnergyState(
			float required_energy,
			float available_energy,
			List<BatteryEnergyRecord> batteries,
			int ship_tile_count,
			bool grav_core_powered,
			bool pilot_console_powered,
			int total_thruster_count,
			int powered_thruster_count,
			bool has_core_power_net,
			PowerNet core_power_net,
			ThingWithComps active_console,
			List<ThingWithComps> thrusters,
			List<ThingWithComps> powered_thrusters)
		{
			this.required_energy = required_energy;
			this.available_energy = available_energy;
			this.batteries = batteries ?? new List<BatteryEnergyRecord>();
			this.ship_tile_count = ship_tile_count;
			this.grav_core_powered = grav_core_powered;
			this.pilot_console_powered = pilot_console_powered;
			this.total_thruster_count = total_thruster_count;
			this.powered_thruster_count = powered_thruster_count;
			this.has_core_power_net = has_core_power_net;
			this.core_power_net = core_power_net;
			this.active_console = active_console;
			this.thrusters = thrusters ?? new List<ThingWithComps>();
			this.powered_thrusters = powered_thrusters ?? new List<ThingWithComps>();
		}
	}

	/// <summary>
	/// Pair of battery component reference + stored energy value captured at a moment in time.
	/// </summary>
	public sealed class BatteryEnergyRecord
	{
		public readonly CompPowerBattery battery;
		public readonly float stored_energy;

		public BatteryEnergyRecord(CompPowerBattery battery, float stored_energy)
		{
			this.battery = battery;
			this.stored_energy = stored_energy;
		}
	}
}
