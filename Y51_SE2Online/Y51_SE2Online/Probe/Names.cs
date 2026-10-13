using System;
using System.Text;

namespace Y51_SE2Online.Probe;

public static class Names
{
	public static string Short(string? fullName)
	{
		if (string.IsNullOrEmpty(fullName))
		{
			return "?";
		}
		string name = fullName;
		int genericArgs = name.IndexOf("[[", StringComparison.Ordinal);
		if (genericArgs >= 0)
		{
			name = name.Substring(0, genericArgs);
		}
		int nested = name.IndexOf('+');
		int lastDot = (nested >= 0 ? name.Substring(0, nested) : name).LastIndexOf('.');
		if (lastDot >= 0)
		{
			name = name.Substring(lastDot + 1);
		}
		name = name.Replace('+', '.');
		var sb = new StringBuilder(name.Length);
		for (int i = 0; i < name.Length; i++)
		{
			if (name[i] == '`')
			{
				// Skip the generic arity digits.
				while (i + 1 < name.Length && char.IsDigit(name[i + 1]))
				{
					i++;
				}
			}
			else
			{
				sb.Append(name[i]);
			}
		}
		return sb.ToString();
	}

	public static string Member(string? name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return "?";
		}
		if (name[0] != '<')
		{
			return name;
		}
		int close = name.IndexOf('>');
		if (close < 0)
		{
			return name;
		}
		string owner = name.Substring(1, close - 1);
		int lastDot = owner.LastIndexOf('.');
		if (lastDot >= 0)
		{
			owner = owner.Substring(lastDot + 1);
		}
		if (owner.Length == 0)
		{
			owner = "lambda";
		}
		int localFunction = name.IndexOf(">g__", StringComparison.Ordinal);
		if (localFunction >= 0)
		{
			int bar = name.IndexOf('|', localFunction);
			string local = bar > 0 ? name.Substring(localFunction + 4, bar - localFunction - 4) : name.Substring(localFunction + 4);
			return owner + "." + local;
		}
		return owner;
	}

	public static string GeneratedType(string? fullName)
	{
		if (string.IsNullOrEmpty(fullName))
		{
			return "?";
		}
		string name = fullName;
		int genericArgs = name.IndexOf("[[", StringComparison.Ordinal);
		if (genericArgs >= 0)
		{
			name = name.Substring(0, genericArgs);
		}
		int nested = name.LastIndexOf('+');
		if (nested < 0)
		{
			return Short(name);
		}
		string outer = Short(name.Substring(0, nested));
		string inner = name.Substring(nested + 1);
		if (inner.StartsWith("<>c", StringComparison.Ordinal))
		{
			return outer;
		}
		return outer + "." + Member(inner);
	}
}
