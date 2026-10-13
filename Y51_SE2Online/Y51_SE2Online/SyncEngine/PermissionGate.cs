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
	private readonly ConcurrentDictionary<Type, string?> _storyValidator = new();

	private readonly ConcurrentDictionary<string, string?> _signalValidator = new(StringComparer.Ordinal);

	public long Refused;

	public long Checked;

	private static readonly Type ServerAttr = typeof(Server);

	private volatile Dictionary<Type, string>? _index;

	public string IndexState { get; private set; } = "building";

	public PermissionGate(bool buildIndex = true)
	{
		if (buildIndex)
		{
			Task.Run(() => BuildIndex(typeof(PermissionsSessionComponent).Assembly));
		}
	}

	internal static bool Checks(string? validator) => validator != null && (validator == "ValidateEntityOwnership" || validator.StartsWith("ValidateUse", StringComparison.Ordinal));

	internal void BuildIndex(Assembly asm)
	{
		var validators = new Dictionary<Type, string>();
		int methodCount = 0;
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
			// Types that failed to load come back as null entries.
			foreach (Type type in types)
			{
				if (type == null)
				{
					continue;
				}
				foreach (MethodInfo method in type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
				{
					methodCount++;
					string validator = null;
					foreach (CustomAttributeData attr in method.CustomAttributes)
					{
						if (attr.AttributeType == ServerAttr && attr.ConstructorArguments.Count == 1 && attr.ConstructorArguments[0].Value is string name && Checks(name))
						{
							validator = name;
						}
					}
					if (validator == null)
					{
						continue;
					}
					foreach (CustomAttributeData attr in method.CustomAttributes)
					{
						if (typeof(SignalAttribute).IsAssignableFrom(attr.AttributeType))
						{
							validators[attr.AttributeType] = validator;
						}
					}
				}
			}
			_index = validators;
			IndexState = $"{validators.Count} guarded signals in {methodCount} methods";
		}
		catch (Exception ex)
		{
			IndexState = $"index failed ({ex.GetType().Name}: {ex.Message}); declaring components only";
		}
	}

	public string? StoryValidator(Type stateMachine)
	{
		// A story's state machine is the compiler's "<Method>d__N" nested in the component that declares the story method.
		return _storyValidator.GetOrAdd(stateMachine, (Type t) =>
		{
			Type owner = t.DeclaringType;
			int open = t.Name.IndexOf('<');
			int close = t.Name.IndexOf('>');
			if (owner == null || open != 0 || close < 2)
			{
				return null;
			}
			string methodName = t.Name.Substring(1, close - 1);
			foreach (MethodInfo method in owner.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
			{
				if (method.Name != methodName)
				{
					continue;
				}
				// Overloads share the name; the one compiled into this state machine says so.
				AsyncStateMachineAttribute async = method.GetCustomAttribute<AsyncStateMachineAttribute>();
				if (async != null && async.StateMachineType != t)
				{
					continue;
				}
				if (method.GetCustomAttribute(ServerAttr) is Server server && Checks(server.ValidatorName))
				{
					return server.ValidatorName;
				}
			}
			return null;
		});
	}

	public string? SignalValidator(string signalFullName)
	{
		Type signal = CommandLayouts.ByName(signalFullName)?.SignalType;
		if (signal == null)
		{
			return null;
		}
		Dictionary<Type, string> index = _index;
		if (index != null)
		{
			return index.TryGetValue(signal, out string validator) ? validator : null;
		}
		return DeclaredValidator(signal);
	}

	private string? DeclaredValidator(Type sig)
	{
		return _signalValidator.GetOrAdd(sig.FullName ?? sig.Name, (string _) =>
		{
			Type owner = sig.DeclaringType;
			if (owner == null)
			{
				return null;
			}
			foreach (MethodInfo method in owner.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
			{
				if (method.GetCustomAttributes(sig, inherit: false).Length != 0 && method.GetCustomAttribute(ServerAttr) is Server server && Checks(server.ValidatorName))
				{
					return server.ValidatorName;
				}
			}
			return null;
		});
	}

	public string? SignalValidator(Type sig)
	{
		Dictionary<Type, string> index = _index;
		if (index == null)
		{
			return DeclaredValidator(sig);
		}
		return index.TryGetValue(sig, out string validator) ? validator : null;
	}

	public string? Check(string? validator, Entity? target, long asker, PermissionsSessionComponent? permissions)
	{
		if (WorldSocial.Access(target, asker) == false) return "Grid access denied by its owner.";
		if (validator == null || target == null || permissions == null || asker == 0L)
		{
			return null;
		}
		bool ownership = validator == "ValidateEntityOwnership";
		bool use = validator.StartsWith("ValidateUse", StringComparison.Ordinal);
		if (!ownership && !use)
		{
			return null;
		}
		// Ownership is decided by the top of the hierarchy (the grid), unless a team override answers per entity.
		Entity root = target;
		if (TeamPermissions.OwnerOverride == null)
		{
			for (int i = 0; i < 32; i++)
			{
				Entity parent = root.TryGet<HierarchyComponent>()?.Parent;
				if (parent == null)
				{
					break;
				}
				root = parent;
			}
		}
		long owner = TeamPermissions.OwnerOf(root);
		if (!PlayerIdentities.IsHuman(owner) || owner == asker)
		{
			return null;
		}
		Interlocked.Increment(ref Checked);
		var askerId = new IdentityId(asker);
		bool allowed;
		if (ownership)
		{
			allowed = permissions.IsOwner(root, askerId);
		}
		else
		{
			CubeBlockComponent block = target.TryGet<CubeBlockComponent>();
			if (block == null || target.TryGet<BlockPermissionsComponent>() == null)
			{
				return null;
			}
			PermissionsConfiguration configuration = Singleton<DefinitionManager>.Instance.GetConfiguration<PermissionsConfiguration>();
			StringId useType = validator == "ValidateUseAdvanced" ? configuration.AdvancedUse
				: validator == "ValidateUseUnrestricted" ? configuration.UnrestrictedUse
				: configuration.BasicUse;
			allowed = permissions.CanUseBlockAction(target, block.Grid.Entity, askerId, useType);
		}
		if (allowed)
		{
			return null;
		}
		Interlocked.Increment(ref Refused);
		return $"{validator}: owned by player {owner} (strict grids)";
	}
}
