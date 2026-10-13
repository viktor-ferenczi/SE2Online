using System;
using System.Text.RegularExpressions;

namespace Y51_SE2Online.Core;

// Vyrex Engine layout inside SE2's own line prefix: "[Y51] [WARN ] [client sync   ] message".
// The subsystem column is the "tag: " (or "tag <id>") the message already starts with.
public static class LogFormat
{
	public const string Brand = "[Y51]";

	// Periodic status blocks (net, host per joiner, client sync); stalls and errors log as they happen.
	public const int StatusIntervalMs = 30000;

	// Steam's ~45-line connection report: once after connecting, then this often.
	public const int DetailedStatusIntervalMs = 600000;

	private const int TagWidth = 14;

	// Up to three words, so a sentence that happens to contain ": " is not mistaken for a tag.
	private static readonly Regex Prefix = new(@"^(?<tag>[A-Za-z][A-Za-z()/_-]*(?: [A-Za-z][A-Za-z()/_-]*){0,2})(?:: | (?=\d{6,}\b))", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	public static string Level(string severity) => severity switch
	{
		"Warning" => "WARN",
		"Error" => "ERROR",
		"Info" => "INFO",
		_ => severity.ToUpperInvariant()
	};

	public static string Line(string level, string message)
	{
		message ??= "";
		string tag = "general";
		string text = message;
		if (message.StartsWith("Harmony ", StringComparison.Ordinal))
		{
			tag = "harmony";
		}
		else if (message.StartsWith("Y51_SE2Online ", StringComparison.Ordinal))
		{
			tag = "plugin";
		}
		else
		{
			Match m = Prefix.Match(message);
			if (m.Success)
			{
				tag = m.Groups["tag"].Value;
				text = message[m.Length..];
			}
		}
		string head = $"{Brand} [{level,-5}] [{tag.PadRight(TagWidth)}] ";
		string indent = Brand + new string(' ', head.Length - Brand.Length);
		return head + text.Replace("\r\n", "\n").Replace("\n", "\n" + indent);
	}

	public static void SelfTest()
	{
		static void Check(bool ok, string what)
		{
			if (!ok)
			{
				throw new InvalidOperationException("LogFormat self-test: " + what);
			}
		}
		Check(Line("INFO", "client: game clock moved +3.0 s") == "[Y51] [INFO ] [client        ] game clock moved +3.0 s", "plain tag");
		Check(Line("WARN", "sync(host) 76561199053255964 'The_Swack': state 1 KiB/s").EndsWith("[sync(host)    ] 76561199053255964 'The_Swack': state 1 KiB/s", StringComparison.Ordinal), "tag before a Steam id");
		Check(Line("INFO", "host world sync: STORY x").Contains("[host world sync] STORY x", StringComparison.Ordinal), "long tag widens its column");
		Check(Line("INFO", "Harmony patch client:Session.Update: OK").Contains("[harmony       ] Harmony patch", StringComparison.Ordinal), "harmony lines");
		Check(Line("INFO", "0.7.6: audio routing").Contains("[general       ] 0.7.6: audio", StringComparison.Ordinal), "no tag");
		Check(Line("INFO", "the host could not load the world because of this: x").Contains("[general       ]", StringComparison.Ordinal), "sentences are not tags");
		string[] lines = Line("ERROR", "net detailed status 76561199053255964:\r\n  Ping: 5").Split('\n');
		Check(lines[0].Contains("[net detailed status] 7656", StringComparison.Ordinal), "three-word tag");
		Check(lines[1] == Brand + new string(' ', lines[0].IndexOf("7656", StringComparison.Ordinal) - Brand.Length) + "  Ping: 5", "continuation lines align under the message");
	}
}
