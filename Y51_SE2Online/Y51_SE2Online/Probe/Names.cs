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
		string text = fullName;
		int num = text.IndexOf("[[", StringComparison.Ordinal);
		if (num >= 0)
		{
			text = text.Substring(0, num);
		}
		int num2 = text.IndexOf('+');
		int num3 = ((num2 >= 0) ? text.Substring(0, num2) : text).LastIndexOf('.');
		if (num3 >= 0)
		{
			text = text.Substring(num3 + 1);
		}
		text = text.Replace('+', '.');
		StringBuilder stringBuilder = new StringBuilder(text.Length);
		for (int i = 0; i < text.Length; i++)
		{
			if (text[i] == '`')
			{
				for (; i + 1 < text.Length && char.IsDigit(text[i + 1]); i++)
				{
				}
			}
			else
			{
				stringBuilder.Append(text[i]);
			}
		}
		return stringBuilder.ToString();
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
		int num = name.IndexOf('>');
		if (num < 0)
		{
			return name;
		}
		string text = name.Substring(1, num - 1);
		int num2 = text.LastIndexOf('.');
		if (num2 >= 0)
		{
			text = text.Substring(num2 + 1);
		}
		if (text.Length == 0)
		{
			text = "lambda";
		}
		int num3 = name.IndexOf(">g__", StringComparison.Ordinal);
		if (num3 >= 0)
		{
			int num4 = name.IndexOf('|', num3);
			string text2 = ((num4 > 0) ? name.Substring(num3 + 4, num4 - num3 - 4) : name.Substring(num3 + 4));
			return text + "." + text2;
		}
		return text;
	}

	public static string GeneratedType(string? fullName)
	{
		if (string.IsNullOrEmpty(fullName))
		{
			return "?";
		}
		string text = fullName;
		int num = text.IndexOf("[[", StringComparison.Ordinal);
		if (num >= 0)
		{
			text = text.Substring(0, num);
		}
		int num2 = text.LastIndexOf('+');
		if (num2 < 0)
		{
			return Short(text);
		}
		string text2 = Short(text.Substring(0, num2));
		string text3 = text.Substring(num2 + 1);
		if (text3.StartsWith("<>c", StringComparison.Ordinal))
		{
			return text2;
		}
		return text2 + "." + Member(text3);
	}
}
