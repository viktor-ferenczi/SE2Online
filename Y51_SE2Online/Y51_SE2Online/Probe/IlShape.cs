using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;

namespace Y51_SE2Online;

/// <summary>Hashes a method body with every metadata token zeroed, so the same code in a re-emitted assembly hashes alike.</summary>
internal static class IlShape
{
	private static readonly OpCode?[] OneByte = new OpCode?[256];

	private static readonly OpCode?[] TwoByte = new OpCode?[256];

	static IlShape()
	{
		foreach (OpCode op in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (OpCode)f.GetValue(null)!))
		{
			ushort value = (ushort)op.Value;
			if (op.Size == 1) OneByte[value] = op;
			else TwoByte[value & 0xFF] = op;
		}
	}

	/// <summary>Null when the IL cannot be decoded.</summary>
	public static string? Hash(byte[] il)
	{
		byte[] shape = (byte[])il.Clone();
		int i = 0;
		while (i < shape.Length)
		{
			OpCode? found = shape[i] == 0xFE ? (i + 1 < shape.Length ? TwoByte[shape[i + 1]] : null) : OneByte[shape[i]];
			if (found is not OpCode op) return null;
			i += op.Size;
			int size = op.OperandType switch
			{
				OperandType.InlineNone => 0,
				OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
				OperandType.InlineVar => 2,
				OperandType.InlineI8 or OperandType.InlineR => 8,
				OperandType.InlineSwitch when i + 4 <= shape.Length => 4 + 4 * BitConverter.ToInt32(shape, i),
				_ => 4
			};
			if (i + size > shape.Length) return null;
			if (op.OperandType is OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineTok or OperandType.InlineType or OperandType.InlineString or OperandType.InlineSig)
			{
				Array.Clear(shape, i, 4);
			}
			i += size;
		}
		return Convert.ToHexString(SHA256.HashData(shape)).ToLowerInvariant();
	}
}
