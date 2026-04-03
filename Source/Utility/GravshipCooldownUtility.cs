using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace GravshipRewired
{
	/// <summary>
	/// Removes Odyssey's built-in post-launch grav-engine cooldown.
	///
	/// We do this defensively through reflection instead of hard-coding a single field/property name,
	/// because the exact internal cooldown member can change between game builds. Any numeric or bool
	/// instance member on Building_GravEngine whose name contains "cooldown" or "coolDown" is treated as
	/// cooldown state and reset to zero/false.
	/// </summary>
	public static class GravshipCooldownUtility
	{
		private static readonly Lazy<List<MemberInfo>> COOLDOWN_MEMBERS = new Lazy<List<MemberInfo>>(findCooldownMembers);

		public static void clearLaunchCooldown(Building_GravEngine grav_engine)
		{
			if (grav_engine == null)
			{
				return;
			}

			foreach (MemberInfo member in COOLDOWN_MEMBERS.Value)
			{
				try
				{
					switch (member)
					{
						case FieldInfo field:
							writeZero(field, grav_engine);
							break;
						case PropertyInfo property:
							writeZero(property, grav_engine);
							break;
					}
				}
				catch
				{
					// Best effort only. If one reflected member is unavailable on a given build we keep trying
					// the rest instead of failing launch entirely.
				}
			}
		}

		public static bool isCooldownRejection(AcceptanceReport report)
		{
			string reason = report.Reason;
			if (reason.NullOrEmpty())
			{
				return false;
			}

			return containsCooldownText(reason);
		}

		public static string stripCooldownLines(string inspect_text)
		{
			if (inspect_text.NullOrEmpty())
			{
				return inspect_text;
			}

			IEnumerable<string> kept_lines = inspect_text
				.Split(new[] { '\n' }, StringSplitOptions.None)
				.Where(line => !containsCooldownText(line));

			return string.Join("\n", kept_lines).Trim();
		}

		private static List<MemberInfo> findCooldownMembers()
		{
			List<MemberInfo> result = new List<MemberInfo>();
			HashSet<string> seen_names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			for (Type current_type = typeof(Building_GravEngine); current_type != null && current_type != typeof(object); current_type = current_type.BaseType)
			{
				BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

				foreach (FieldInfo field in current_type.GetFields(flags))
				{
					if (!looksLikeCooldownMember(field.Name) || !canZero(field.FieldType) || !seen_names.Add(field.Name))
					{
						continue;
					}

					result.Add(field);
				}

				foreach (PropertyInfo property in current_type.GetProperties(flags))
				{
					if (!property.CanWrite || !looksLikeCooldownMember(property.Name) || !canZero(property.PropertyType) || !seen_names.Add(property.Name))
					{
						continue;
					}

					result.Add(property);
				}
			}

			return result;
		}

		private static bool looksLikeCooldownMember(string name)
		{
			if (name.NullOrEmpty())
			{
				return false;
			}

			return name.IndexOf("cooldown", StringComparison.OrdinalIgnoreCase) >= 0 ||
				name.IndexOf("cool down", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static bool containsCooldownText(string text)
		{
			return text.IndexOf("cooldown", StringComparison.OrdinalIgnoreCase) >= 0 ||
				text.IndexOf("cool down", StringComparison.OrdinalIgnoreCase) >= 0 ||
				text.IndexOf("abkling", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static bool canZero(Type type)
		{
			Type underlying_type = Nullable.GetUnderlyingType(type) ?? type;
			return underlying_type == typeof(bool) ||
				underlying_type == typeof(int) ||
				underlying_type == typeof(long) ||
				underlying_type == typeof(float) ||
				underlying_type == typeof(double);
		}

		private static void writeZero(FieldInfo field, object instance)
		{
			Type type = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
			if (type == typeof(bool))
			{
				field.SetValue(instance, false);
				return;
			}

			field.SetValue(instance, Convert.ChangeType(0, type));
		}

		private static void writeZero(PropertyInfo property, object instance)
		{
			Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
			if (type == typeof(bool))
			{
				property.SetValue(instance, false, null);
				return;
			}

			property.SetValue(instance, Convert.ChangeType(0, type), null);
		}
	}
}
