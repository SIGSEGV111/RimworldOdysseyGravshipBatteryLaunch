using RimWorld;
using Verse;

namespace GravshipRewired
{
	/// <summary>
	/// RimWorld's XML defs are usually referenced by string in data files, but from C# you almost
	/// never want to hardcode those strings over and over. A DefOf class is RimWorld's standard way
	/// to bind XML defs to typed static fields.
	///
	/// During startup the engine fills OGBL_GravshipSpoolUp with the JobDef declared in
	/// Defs/JobDefs/OGBL_JobDefs.xml. After that, code can reference the field directly.
	/// </summary>
	[DefOf]
	public static class OGBL_DefOf
	{
		/// <summary>
		/// Custom job used during the 2-hour launch spool-up.
		/// </summary>
		public static JobDef OGBL_GravshipSpoolUp;

		static OGBL_DefOf()
		{
			// RimWorld expects DefOf classes to call this helper so the static fields are populated
			// even when the type is touched very early during startup.
			DefOfHelper.EnsureInitializedInCtor(typeof(OGBL_DefOf));
		}
	}
}
