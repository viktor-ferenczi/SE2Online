namespace Y51_SE2Online.Sync;

public readonly record struct TypeKeyDef(ushort Key, ushort Size, string Name)
{
	public string ShortName
	{
		get
		{
			string text = Name;
			int num = text.IndexOf(',');
			if (num >= 0)
			{
				text = text.Substring(0, num);
			}
			int num2 = text.LastIndexOfAny(new char[2] { '.', '+' });
			if (num2 < 0)
			{
				return text;
			}
			return text.Substring(num2 + 1);
		}
	}
}
