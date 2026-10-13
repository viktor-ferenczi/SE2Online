using System;
using System.Collections.Generic;
using System.Globalization;

namespace Y51_SE2Online.Sync;

public sealed class RateCapPolicy
{
	public const double DefaultHz = 60.0;

	public static readonly IReadOnlyDictionary<string, double> Defaults = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
	{
		["WindGeneratorData"] = 2.0,
		["GravityEffectData"] = 2.0,
		["CustomGravity"] = 2.0,
		["MaxThrustData"] = 10.0,
		["AirData"] = 30.0,
		["WorldTransform"] = 60.0,
		["RigidBodyData"] = 60.0,
		["CharacterPhysicsState"] = 60.0,
		["AdvancedCharacterControlData"] = 60.0,
		["VoluntaryThrustData"] = 60.0
	};

	private readonly Dictionary<string, double> _hz = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

	public double FallbackHz { get; }

	public RateCapPolicy(IReadOnlyDictionary<string, double>? overrides = null, double fallbackHz = DefaultHz)
	{
		FallbackHz = fallbackHz;
		foreach (KeyValuePair<string, double> @default in Defaults)
		{
			_hz[@default.Key] = @default.Value;
		}
		if (overrides == null)
		{
			return;
		}
		foreach (KeyValuePair<string, double> @override in overrides)
		{
			_hz[@override.Key] = @override.Value;
		}
	}

	public double HzFor(string shortName)
	{
		if (!_hz.TryGetValue(shortName, out var value))
		{
			return FallbackHz;
		}
		return value;
	}

	public int IntervalMsFor(string shortName)
	{
		double num = HzFor(shortName);
		if (num <= 0.0)
		{
			return -1;
		}
		return (int)Math.Floor(900.0 / num);
	}

	public string Describe()
	{
		List<string> list = new List<string>();
		foreach (KeyValuePair<string, double> item in _hz)
		{
			list.Add(item.Key + "=" + item.Value.ToString("0.##", CultureInfo.InvariantCulture));
		}
		list.Add("other=" + FallbackHz.ToString("0.##", CultureInfo.InvariantCulture));
		return string.Join(" ", list) + " Hz";
	}
}
