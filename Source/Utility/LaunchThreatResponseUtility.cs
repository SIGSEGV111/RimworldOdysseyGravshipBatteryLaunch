using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace OdysseyGravshipBatteryLaunch
{
	/// <summary>
	/// Reacts to gravship spool-up by making nearby large-scale threats stop waiting and engage now.
	///
	/// This implements two user-facing rules:
	/// - hostile raiders that are already present on the map should attack immediately instead of
	///   continuing to loiter, prepare, or siege;
	/// - dormant mech clusters should wake up and become active threats as soon as spool-up begins.
	///
	/// In RimWorld, coordinated hostile behavior is usually controlled by a <see cref="Lord"/>.
	/// A lord owns a set of pawns and a <see cref="LordJob"/> that decides their group behavior
	/// (assault colony, defend a point, flee, kidnap, and so on).
	///
	/// The cleanest way to force immediate aggression is therefore:
	/// 1. identify the pawns/buildings we care about;
	/// 2. wake dormant mechanoid comps where needed;
	/// 3. move the relevant hostile pawns into a fresh <see cref="LordJob_AssaultColony"/>.
	///
	/// This keeps the code aligned with RimWorld's own AI model instead of issuing ad-hoc pawn jobs.
	/// </summary>
	public static class LaunchThreatResponseUtility
	{
		/// <summary>
		/// Entry point called once when spool-up successfully starts.
		///
		/// The method is intentionally fail-soft: if threat escalation throws for any reason, the launch
		/// warmup itself should still continue. A bug in the reaction logic should not brick gravship use.
		/// </summary>
		public static void triggerThreatResponseOnSpoolUp(Map map)
		{
			if (map == null)
			{
				return;
			}

			try
			{
				forceHumanlikeRaidersToAttack(map);
				wakeDormantMechClustersAndAttack(map);
			}
			catch (Exception exception)
			{
				Log.Error($"[OdysseyGravshipBatteryLaunch] Failed to escalate hostile threats on gravship spool-up: {exception}");
			}
		}

		/// <summary>
		/// Forces already-present hostile humanlike raiders into an immediate assault-colony lord.
		///
		/// Why group by faction?
		/// RimWorld expects one hostile faction per lord in almost all normal combat situations.
		/// If two separate raider factions somehow exist on the same map, they should not be merged into
		/// one cross-faction group.
		/// </summary>
		private static void forceHumanlikeRaidersToAttack(Map map)
		{
			Dictionary<Faction, List<Pawn>> pawns_by_faction = new Dictionary<Faction, List<Pawn>>();

			foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
			{
				if (!isImmediateRaiderCandidate(pawn))
				{
					continue;
				}

				Lord current_lord = pawn.GetLord();
				if (current_lord?.LordJob is LordJob_AssaultColony)
				{
					// Already doing exactly what we want.
					continue;
				}

				if (current_lord != null)
				{
					// Pawns cannot belong to two lords at once. We explicitly detach them from their current
					// lord before assigning them to the new assault group.
					current_lord.Notify_PawnLost(pawn, PawnLostCondition.ForcedToJoinOtherLord);
				}

				if (!pawns_by_faction.TryGetValue(pawn.Faction, out List<Pawn> pawns))
				{
					pawns = new List<Pawn>();
					pawns_by_faction.Add(pawn.Faction, pawns);
				}

				pawns.Add(pawn);
			}

			foreach (KeyValuePair<Faction, List<Pawn>> entry in pawns_by_faction)
			{
				Faction faction = entry.Key;
				List<Pawn> pawns = entry.Value;

				if (faction == null || pawns.Count == 0)
				{
					continue;
				}

				// Keep the assault settings fairly "committed" so the response feels immediate and serious.
				// Raiders started by this override should push the colony instead of giving up or deciding
				// to steal/kidnap right away.
				LordJob_AssaultColony assault_job = new LordJob_AssaultColony(
					faction,
					canKidnap: false,
					canTimeoutOrFlee: false,
					sappers: false,
					useAvoidGridSmart: true,
					canSteal: false);

				LordMaker.MakeNewLord(faction, assault_job, map, pawns);
			}
		}

		/// <summary>
		/// Wakes every currently dormant mech-cluster thing on the map and makes awakened mech pawns assault.
		///
		/// There are two different pieces of behavior here:
		/// - buildings / turrets / activators need their dormant comps woken so they become live threats;
		/// - mobile mech pawns need to stop sitting in their dormant-cluster lord and start moving on the
		///   colony immediately.
		///
		/// We therefore wake all dormant mechanoid things first, remember which cluster lords were touched,
		/// then re-home the involved mech pawns into a new assault-colony lord.
		/// </summary>
		private static void wakeDormantMechClustersAndAttack(Map map)
		{
			HashSet<Lord> awakened_cluster_lords = new HashSet<Lord>();
			HashSet<Pawn> attacking_mechs = new HashSet<Pawn>();

			// Buildings first: turrets / assemblers / cluster structures often carry the dormant comp too.
			foreach (Thing thing in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
			{
				if (thing is not ThingWithComps thing_with_comps)
				{
					continue;
				}

				CompCanBeDormant dormant_comp = thing_with_comps.TryGetComp<CompCanBeDormant>();
				if (!isDormantMechClusterMember(dormant_comp, thing_with_comps))
				{
					continue;
				}

				Building building = thing_with_comps as Building;
				Lord current_lord = building?.GetLord();
				if (current_lord != null)
				{
					awakened_cluster_lords.Add(current_lord);
				}

				dormant_comp.WakeUp();
			}

			// Then pawns. Sleepy cluster mechs also use CompCanBeDormant.
			foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
			{
				CompCanBeDormant dormant_comp = pawn.TryGetComp<CompCanBeDormant>();
				if (!isDormantMechClusterMember(dormant_comp, pawn))
				{
					continue;
				}

				Lord current_lord = pawn.GetLord();
				if (current_lord != null)
				{
					awakened_cluster_lords.Add(current_lord);
				}

				dormant_comp.WakeUp();
				if (!pawn.Dead && !pawn.Downed)
				{
					attacking_mechs.Add(pawn);
				}
			}

			// Some cluster pawns may already have been awake but still belonged to the same sleeping/defend
			// lord as the cluster. Pull them into the immediate-assault set as well.
			foreach (Lord lord in awakened_cluster_lords)
			{
				if (lord == null)
				{
					continue;
				}

				foreach (Pawn pawn in lord.ownedPawns.ToList())
				{
					if (pawn == null || pawn.Dead || pawn.Downed || pawn.Faction != Faction.OfMechanoids)
					{
						continue;
					}

					attacking_mechs.Add(pawn);
				}
			}

			if (attacking_mechs.Count == 0)
			{
				return;
			}

			foreach (Pawn pawn in attacking_mechs)
			{
				Lord current_lord = pawn.GetLord();
				if (current_lord != null && current_lord.LordJob is not LordJob_AssaultColony)
				{
					current_lord.Notify_PawnLost(pawn, PawnLostCondition.ForcedToJoinOtherLord);
				}
			}

			LordJob_AssaultColony mech_assault_job = new LordJob_AssaultColony(
				Faction.OfMechanoids,
				canKidnap: false,
				canTimeoutOrFlee: false,
				sappers: false,
				useAvoidGridSmart: true,
				canSteal: false);

			LordMaker.MakeNewLord(Faction.OfMechanoids, mech_assault_job, map, attacking_mechs.ToList());
		}

		/// <summary>
		/// Rough filter for "this hostile humanlike pawn is functionally a raider already on the map".
		///
		/// We intentionally avoid touching:
		/// - player pawns;
		/// - prisoners;
		/// - downed / dead pawns;
		/// - non-humanlike threats such as manhunters or mechanoids.
		/// </summary>
		private static bool isImmediateRaiderCandidate(Pawn pawn)
		{
			if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed)
			{
				return false;
			}

			if (pawn.Faction == null || pawn.Faction.IsPlayer)
			{
				return false;
			}

			if (!pawn.HostileTo(Faction.OfPlayer))
			{
				return false;
			}

			if (pawn.IsPrisonerOfColony)
			{
				return false;
			}

			if (pawn.Faction == Faction.OfMechanoids)
			{
				return false;
			}

			return pawn.RaceProps?.Humanlike == true;
		}

		/// <summary>
		/// Shared filter for dormant mech-cluster members.
		///
		/// We use faction + dormant-comp state as the main test. That is intentionally broader than trying
		/// to detect only one specific cluster implementation class, which keeps the code simpler and more
		/// robust across minor Odyssey/RimWorld internals.
		/// </summary>
		private static bool isDormantMechClusterMember(CompCanBeDormant dormant_comp, Thing parent)
		{
			if (dormant_comp == null || parent == null)
			{
				return false;
			}

			if (parent.Faction != Faction.OfMechanoids)
			{
				return false;
			}

			if (!parent.Spawned)
			{
				return false;
			}

			return !dormant_comp.Awake;
		}
	}
}
