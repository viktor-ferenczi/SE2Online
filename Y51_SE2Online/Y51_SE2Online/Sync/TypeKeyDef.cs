namespace Y51_SE2Online.Sync;

public readonly record struct TypeKeyDef(ushort Key, ushort Size, string Name)
{
	public string ShortName
	{
		get
		{
			string name = Name;
			int comma = name.IndexOf(',');
			if (comma >= 0)
			{
				name = name.Substring(0, comma);
			}
			int separator = name.LastIndexOfAny(new[] { '.', '+' });
			return separator >= 0 ? name.Substring(separator + 1) : name;
		}
	}
}
