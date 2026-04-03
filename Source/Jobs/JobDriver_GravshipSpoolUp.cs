using System.Collections.Generic;
using Verse;
using Verse.AI;
using RimWorld;

namespace GravshipRewired
{
	/// <summary>
	/// A JobDriver is RimWorld's state machine for a pawn job.
	///
	/// The job itself is defined in XML (JobDef). The driver implements the runtime behavior as a
	/// sequence of "toils". A toil is a small step such as walking to a cell, waiting, or using a
	/// workstation. For the spool-up phase we keep the behavior intentionally simple:
	///
	/// 1. Walk to the assigned cell near the pilot console.
	/// 2. Stay there indefinitely.
	/// 3. Face the console every tick so the animation looks intentional.
	///
	/// The warmup manager owns the actual timer and decides when this job should end.
	/// </summary>
	public sealed class JobDriver_GravshipSpoolUp : JobDriver
	{
		// RimWorld jobs can store multiple targets. By convention TargetIndex.A is usually the primary
		// target. Here A is the stand cell and B is the console the pawn should face.
		private const TargetIndex CELL_INDEX = TargetIndex.A;
		private const TargetIndex CONSOLE_INDEX = TargetIndex.B;

		public override bool TryMakePreToilReservations(bool errorOnFailed)
		{
			// Only reserve the actual standing cell.
			//
			// Why not reserve the pilot console too?
		// Because both the pilot and copilot need to reference the same console during spool-up.
			// Reserving the console Thing itself makes the second pawn fail immediately even though the
			// two pawns are meant to stand at different cells around the same workstation.
			//
			// In vanilla RimWorld many workstation-style jobs reserve the worktable because only one pawn
			// uses it at a time. Our spool-up job is different: the console is a shared focal point, while
			// the actual conflict worth preventing is two pawns trying to stand on the same tile.
			return pawn.Reserve(job.GetTarget(CELL_INDEX), job, 1, -1, null, errorOnFailed);
		}

		public override IEnumerable<Toil> MakeNewToils()
		{
			// Fail the job immediately if the console disappears, is despawned, or becomes forbidden.
			this.FailOnDestroyedNullOrForbidden(CONSOLE_INDEX);

			// First toil: physically move the pawn to the assigned console-adjacent cell.
			yield return Toils_Goto.GotoCell(CELL_INDEX, PathEndMode.OnCell);

			// Second toil: an infinite wait-style toil that the warmup manager interrupts later.
			Toil spool = ToilMaker.MakeToil("OGBL_GravshipSpoolUp");
			spool.defaultCompleteMode = ToilCompleteMode.Never;
			spool.handlingFacing = true;
			spool.socialMode = RandomSocialMode.Off;
			spool.tickAction = delegate
			{
				// Facing is updated every tick so the pilot/copilot keep looking at the console while
				// the warmup timer is running.
				Thing console = job.GetTarget(CONSOLE_INDEX).Thing;
				if (console != null)
				{
					pawn.rotationTracker.FaceCell(console.Position);
				}
			};

			yield return spool;
		}
	}
}
