using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Permissions;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.DCS.Annotations.Signals;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.Annotations;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed class PermissionGate
{
	private readonly ConcurrentDictionary<Type, string?> _storyValidator = new ConcurrentDictionary<Type, string>();

	private readonly ConcurrentDictionary<string, string?> _signalValidator = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

	public long Refused;

	public long Checked;

	private static readonly Type ServerAttr = typeof(Server);

	private volatile Dictionary<Type, string>? _index;

	public string IndexState { get; private set; } = "building";

	public PermissionGate(bool buildIndex = true)
	{
		if (buildIndex)
		{
			Task.Run(() =>
			{
				BuildIndex(typeof(PermissionsSessionComponent).Assembly);
			});
		}
	}

	internal static bool Checks(string? validator)
	{
		if (validator != null)
		{
			if (!(validator == "ValidateEntityOwnership"))
			{
				return validator.StartsWith("ValidateUse", StringComparison.Ordinal);
			}
			return true;
		}
		return false;
	}

	internal void BuildIndex(Assembly asm)
	{
		Dictionary<Type, string> dictionary = new Dictionary<Type, string>();
		int num = 0;
		try
		{
			Type[] types;
			try
			{
				types = asm.GetTypes();
			}
			catch (ReflectionTypeLoadException ex)
			{
				types = ex.Types;
			}
			Type[] array = types;
			foreach (Type type in array)
			{
				if (type == null)
				{
					continue;
				}
				MethodInfo[] methods = type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				foreach (MethodInfo methodInfo in methods)
				{
					num++;
					string text = null;
					foreach (CustomAttributeData customAttribute in methodInfo.CustomAttributes)
					{
						if (customAttribute.AttributeType == ServerAttr && customAttribute.ConstructorArguments.Count == 1 && customAttribute.ConstructorArguments[0].Value is string text2 && Checks(text2))
						{
							text = text2;
						}
					}
					if (text == null)
					{
						continue;
					}
					foreach (CustomAttributeData customAttribute2 in methodInfo.CustomAttributes)
					{
						if (typeof(SignalAttribute).IsAssignableFrom(customAttribute2.AttributeType))
						{
							dictionary[customAttribute2.AttributeType] = text;
						}
					}
				}
			}
			_index = dictionary;
			IndexState = $"{dictionary.Count} guarded signals in {num} methods";
		}
		catch (Exception ex2)
		{
			IndexState = $"index failed ({ex2.GetType().Name}: {ex2.Message}); declaring components only";
		}
	}

	public string? StoryValidator(Type stateMachine)
	{
		return _storyValidator.GetOrAdd(stateMachine, (Type t) =>
		{
			Type declaringType = t.DeclaringType;
			int num = t.Name.IndexOf('<');
			int num2 = t.Name.IndexOf('>');
			if (declaringType == null || num != 0 || num2 < 2)
			{
				return (string?)null;
			}
			string text = t.Name.Substring(1, num2 - 1);
			MethodInfo[] methods = declaringType.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (MethodInfo methodInfo in methods)
			{
				if (!(methodInfo.Name != text))
				{
					AsyncStateMachineAttribute customAttribute = methodInfo.GetCustomAttribute<AsyncStateMachineAttribute>();
					if ((customAttribute == null || !(customAttribute.StateMachineType != t)) && methodInfo.GetCustomAttribute(ServerAttr) is Server server && Checks(server.ValidatorName))
					{
						return server.ValidatorName;
					}
				}
			}
			return (string?)null;
		});
	}

	public string? SignalValidator(string signalFullName)
	{
		Type type = CommandLayouts.ByName(signalFullName)?.SignalType;
		if (type == null)
		{
			return null;
		}
		Dictionary<Type, string> index = _index;
		if (index != null)
		{
			if (!index.TryGetValue(type, out var value))
			{
				return null;
			}
			return value;
		}
		return DeclaredValidator(type);
	}

	private string? DeclaredValidator(Type sig)
	{
		return _signalValidator.GetOrAdd(sig.FullName ?? sig.Name, (string _) =>
		{
			Type declaringType = sig.DeclaringType;
			if (declaringType == null)
			{
				return (string?)null;
			}
			MethodInfo[] methods = declaringType.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (MethodInfo methodInfo in methods)
			{
				if (methodInfo.GetCustomAttributes(sig, inherit: false).Length != 0 && methodInfo.GetCustomAttribute(ServerAttr) is Server server && Checks(server.ValidatorName))
				{
					return server.ValidatorName;
				}
			}
			return (string?)null;
		});
	}

	public string? SignalValidator(Type sig)
	{
		Dictionary<Type, string> index = _index;
		if (index == null)
		{
			return DeclaredValidator(sig);
		}
		if (!index.TryGetValue(sig, out var value))
		{
			return null;
		}
		return value;
	}

	public string? Check(string? validator, Entity? target, long asker, PermissionsSessionComponent? permissions)
	{
		if (WorldSocial.Access(target, asker) == false) return "Grid access denied by its owner.";
		if (validator == null || target == null || permissions == null || asker == 0L)
		{
			return null;
		}
		bool flag = validator == "ValidateEntityOwnership";
		bool flag2 = validator.StartsWith("ValidateUse", StringComparison.Ordinal);
		if (!flag && !flag2)
		{
			return null;
		}
		Entity entity = target;
		if (TeamPermissions.OwnerOverride == null)
		{
			for (int i = 0; i < 32; i++)
			{
				Entity entity2 = entity.TryGet<HierarchyComponent>()?.Parent;
				if (entity2 == null)
				{
					break;
				}
				entity = entity2;
			}
		}
		long num = TeamPermissions.OwnerOf(entity);
		if (!PlayerIdentities.IsHuman(num) || num == asker)
		{
			return null;
		}
		Interlocked.Increment(ref Checked);
		IdentityId identityId = new IdentityId(asker);
		bool flag3;
		if (flag)
		{
			flag3 = permissions.IsOwner(entity, identityId);
		}
		else
		{
			CubeBlockComponent cubeBlockComponent = target.TryGet<CubeBlockComponent>();
			if (cubeBlockComponent == null || target.TryGet<BlockPermissionsComponent>() == null)
			{
				return null;
			}
			PermissionsConfiguration configuration = Singleton<DefinitionManager>.Instance.GetConfiguration<PermissionsConfiguration>();
			StringId stringId;
			if (validator == "ValidateUseAdvanced")
			{
				stringId = configuration.AdvancedUse;
			}
			else
			{
				stringId = ((!(validator == "ValidateUseUnrestricted")) ? configuration.BasicUse : configuration.UnrestrictedUse);
			}
			StringId useType = stringId;
			flag3 = permissions.CanUseBlockAction(target, cubeBlockComponent.Grid.Entity, identityId, useType);
		}
		if (flag3)
		{
			return null;
		}
		Interlocked.Increment(ref Refused);
		return $"{validator}: owned by player {num} (strict grids)";
	}
}
