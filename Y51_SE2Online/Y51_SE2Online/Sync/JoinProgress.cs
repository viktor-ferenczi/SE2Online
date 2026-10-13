using System;
using System.Globalization;
using System.Text;

namespace Y51_SE2Online.Sync;

public sealed class JoinProgress
{
	public const int PlayingToastSeconds = 8;

	public const string RejoinKeyName = "F3";

	private readonly object _sync = new object();

	private JoinStage _stage;

	private long _stageSinceMs;

	private long _loadingSinceMs;

	private string _host = "the host";

	private string? _world;

	private string? _detail;

	private int _attempt;

	private int _maxAttempts;

	private int _partsReceived;

	private int _partsTotal;

	private int _partsBuilt;

	private int _joinNo = 1;

	private long _received;

	private long _expected;

	private long _receivedBase = -1L;

	private bool _rejoinKey = true;

	private bool _autoRejoin = true;

	private bool _inGame;

	private long _receivingSinceMs;

	private int _entitiesBuilt;

	private int _entitiesTotal;

	private static readonly string[] Tips =
	{
		"Press Enter to chat with everyone on the server",
		"F3 shows who is online, their role and ping",
		"Admins open their panel with Alt+F10",
		"New grids are private; share them in Grid ownership",
		"Faction tags appear before names, like [TAG] Name",
		"F11 shows the Y51_SE2Online connection stats page",
		"Rebind chat, players and admin keys in Multiplayer > Settings"
	};

	public const int LoadingMax = 32;

	public const int ToastMax = 56;

	public const int WorldNameMax = 28;

	public JoinStage Stage
	{
		get
		{
			lock (_sync)
			{
				return _stage;
			}
		}
	}

	public int JoinNumber
	{
		get
		{
			lock (_sync)
			{
				return _joinNo;
			}
		}
	}

	public void Set(JoinStage stage, long now, string? detail = null)
	{
		lock (_sync)
		{
			if (stage != _stage)
			{
				_stage = stage;
				_stageSinceMs = now;
				if (stage == JoinStage.Loading)
				{
					_loadingSinceMs = now;
				}
				if (stage == JoinStage.Receiving)
				{
					_receivingSinceMs = now;
				}
			}
			_detail = detail;
		}
	}

	public bool Advance(JoinStage from, JoinStage to, long now, string? detail = null)
	{
		lock (_sync)
		{
			if (_stage != from)
			{
				return false;
			}
			Set(to, now, detail);
			return true;
		}
	}

	public void SetHost(string label)
	{
		if (string.IsNullOrWhiteSpace(label))
		{
			return;
		}
		lock (_sync)
		{
			_host = label.Trim();
		}
	}

	public void SetAttempt(int attempt, int max)
	{
		lock (_sync)
		{
			_attempt = attempt;
			_maxAttempts = max;
		}
	}

	public void Accepted(string world, long expectedBytes, int parts, long now, long? bulkBase = null)
	{
		lock (_sync)
		{
			_world = world;
			_expected = Math.Max(0L, expectedBytes);
			_partsTotal = Math.Max(0, parts);
			_partsReceived = (_partsBuilt = 0);
			_entitiesBuilt = 0;
			_received = 0L;
			_receivedBase = bulkBase ?? (-1);
			JoinStage stage = _stage;
			if (stage <= JoinStage.WaitingForHostWorld)
			{
				Set(JoinStage.Receiving, now);
			}
		}
	}

	public void BulkBytes(long total)
	{
		lock (_sync)
		{
			if (_receivedBase < 0)
			{
				_receivedBase = total;
			}
			_received = Math.Max(0L, total - _receivedBase);
		}
	}

	public void PartReceived()
	{
		lock (_sync)
		{
			_partsReceived++;
		}
	}

	public void PartBuilt(int entities = 0)
	{
		lock (_sync)
		{
			_partsBuilt++;
			_entitiesBuilt += Math.Max(0, entities);
		}
	}

	public void SetWorldEntities(int total)
	{
		lock (_sync)
		{
			_entitiesTotal = Math.Max(0, total);
		}
	}

	public void SetInGame(bool inGame)
	{
		lock (_sync)
		{
			_inGame = inGame;
		}
	}

	public void SetRejoin(bool key, bool auto)
	{
		lock (_sync)
		{
			_rejoinKey = key;
			_autoRejoin = auto;
		}
	}

	public void NewJoin(long now, string? detail = null)
	{
		lock (_sync)
		{
			_joinNo++;
			_world = null;
			_attempt = (_maxAttempts = (_partsReceived = (_partsTotal = (_partsBuilt = 0))));
			_received = (_expected = 0L);
			_receivedBase = -1L;
			_entitiesBuilt = (_entitiesTotal = 0);
			_inGame = false;
			_stage = JoinStage.Connecting;
			_stageSinceMs = now;
			_detail = detail;
		}
	}

	public override string ToString()
	{
		JoinText joinText = Describe(2305843009213693951L);
		return joinText.Toast ?? joinText.Loading ?? $"({Stage})";
	}

	public JoinText Describe(long now)
	{
		lock (_sync)
		{
			try
			{
				return DescribeLocked(now);
			}
			catch (Exception)
			{
				return JoinText.None;
			}
		}
	}

	private JoinText DescribeLocked(long now)
	{
		string text = Cut(_host, 20);
		string text2 = " Open Multiplayer to retry.";
		string text3 = ((_expected > 0) ? (Mib(Math.Min(_received, _expected)) + "/" + Mib(_expected) + " MiB") : (Mib(_received) + " MiB"));
		bool flag = _expected > 0 && _received >= _expected;
		string text4 = (string.IsNullOrWhiteSpace(_detail) ? null : _detail.Trim());
		switch (_stage)
		{
		case JoinStage.Idle:
			return JoinText.None;
		case JoinStage.WaitingForSteam:
			return Toast("Waiting for Steam to start...");
		case JoinStage.LookingForHost:
			return Toast(text4 ?? "Looking for a friend who is hosting...");
		case JoinStage.Connecting:
		{
			string text8 = ((_attempt > 1) ? $" ({_attempt}{((_maxAttempts > 0) ? $"/{_maxAttempts}" : "")})" : "");
			return Toast((text4 != null) ? $"Connecting to {text} ({Cut(text4, 18)})..." : ("Connecting to " + text + text8 + "..."));
		}
		case JoinStage.WaitingForHostWorld:
			return Toast("Connected; " + text + " is not in a world yet.");
		case JoinStage.Receiving:
			return new JoinText(Cut("Joining: receiving " + text3, 56), Detailed(now), 0);
		case JoinStage.Loading:
		{
			string text5 = Clock(now - _loadingSinceMs);
			bool flag2 = now - _loadingSinceMs > 45000;
			string toast;
			if (flag2)
			{
				toast = "Loading (" + text5 + "). A first join can take minutes.";
			}
			else
			{
				toast = (flag ? ("Loading the game (" + text5 + ")...") : ("Loading the game (" + text5 + "), " + text3));
			}
			string line;
			if (flag2)
			{
				line = "Loading " + text5 + ", please wait";
			}
			else
			{
				line = (flag ? ("Loading " + text5) : ("Loading " + text5 + ", " + text3));
			}
			return new JoinText(Cut(toast, 56), Detailed(now), 0);
		}
		case JoinStage.Building:
		{
			int num = Math.Max(_partsTotal, 1);
			string text9 = $"part {Math.Min(_partsBuilt + 1, num)} of {num}";
			return new JoinText(Cut("Joining: " + text9 + ((_partsReceived < _partsTotal && !flag) ? (" (" + text3 + ")") : "") + "...", 56), _inGame ? null : Detailed(now), 0);
		}
		case JoinStage.WaitingForCharacter:
			return new JoinText("World ready. Waiting for your character...", _inGame ? null : Detailed(now), 0);
		case JoinStage.Playing:
		{
			string text6 = ((_world != null) ? ("'" + Cut(_world, 28) + "'") : (text + "'s world"));
			if (now - _stageSinceMs > 8000)
			{
				return JoinText.None;
			}
			return new JoinText(Cut("Joined " + text6 + "." + ((text4 != null) ? (" " + text4) : ""), 56), null, 8);
		}
		case JoinStage.Left:
			return Toast("You left the game." + text2);
		case JoinStage.Lost:
		{
			string text7;
			if (_autoRejoin)
			{
				text7 = (_inGame ? " Back to the menu, then rejoining." : " Rejoining in a moment...");
			}
			else
			{
				text7 = text2;
			}
			return Toast("Connection lost." + text7);
		}
		case JoinStage.Rejected:
			return Toast("Refused: " + Cut((text4 ?? "no reason given").TrimEnd('.'), 45) + ".");
		case JoinStage.GaveUp:
			return Toast("No answer from " + text + "." + text2);
		case JoinStage.Failed:
			return Toast("Load failed" + ((text4 != null) ? (" (" + Cut(text4, 18) + ")") : "") + "." + text2);
		default:
			return JoinText.None;
		}
		static JoinText Both(string s, string s2)
		{
			return new JoinText(Cut(s, 56), Cut(s2, 32), 0);
		}
		static JoinText Toast(string s)
		{
			return new JoinText(Cut(s, 56), null, 0);
		}
	}

	/// <summary>One-line summary for logs.</summary>
	private string Detailed(long now)
	{
		var view = LoadingScreen(now);
		return view == null ? _stage.ToString() : view.Value.Status + " " + view.Value.Detail.Split('\n')[0];
	}

	/// <summary>
	/// What the native loading screen shows while joining: the world name in its large title, the step with a
	/// progress bar and details in the centre (where the game's own tip sits), and the percentage beside the spinner.
	/// </summary>
	public (string Title, string Detail, string Status)? LoadingScreen(long now)
	{
		lock (_sync)
		{
			if (_stage < JoinStage.Receiving || _stage > JoinStage.WaitingForCharacter) return null;
			string world = _world == null ? Cut(_host, 40) : Cut(_world, 40);
			string what;
			double percent;
			int step;
			switch (_stage)
			{
			case JoinStage.Receiving:
			{
				step = 1;
				double seconds = Math.Max(0.5, (now - _receivingSinceMs) / 1000.0);
				double rate = _received / seconds;
				double got = _expected > 0 ? Math.Min(1.0, (double)_received / _expected) : 0;
				percent = got * 40;
				string eta = _expected > 0 && rate > 1024 && _received < _expected ? "  /  " + Clock((long)((_expected - _received) / rate * 1000)) + " left" : "";
				what = $"Downloading the world from {Cut(_host, 32)}\n{Mib(Math.Min(_received, Math.Max(_expected, _received)))} of {Mib(_expected)} MiB  /  {Mib((long)rate)} MiB/s{eta}";
				break;
			}
			case JoinStage.Loading:
				step = 2;
				percent = 40 + Math.Min(18, (now - _loadingSinceMs) / 2000.0);
				what = $"Starting the engine\nTerrain, planets and physics  /  {Clock(now - _loadingSinceMs)}";
				break;
			case JoinStage.Building:
			{
				step = 3;
				int parts = Math.Max(_partsTotal, 1);
				double built = _entitiesTotal > 0 ? Math.Min(1.0, (double)_entitiesBuilt / _entitiesTotal) : Math.Min(1.0, (double)_partsBuilt / parts);
				percent = 60 + built * 35;
				string entities = _entitiesTotal > 0 ? $"{_entitiesBuilt:N0} of {_entitiesTotal:N0} grids, characters and objects" : $"{_entitiesBuilt:N0} objects";
				what = $"Building world section {Math.Min(_partsBuilt + 1, parts)} of {parts}\n{entities}";
				break;
			}
			default:
				step = 4;
				percent = 97;
				what = "World ready\nSpawning your character";
				break;
			}
			int filled = (int)Math.Round(Math.Clamp(percent, 0, 100) / 5);
			string tip = Tips[(int)(Math.Max(0, now) / 6000 % Tips.Length)];
			string detail = $"Step {step} of 4   [{new string('|', filled)}{new string('.', 20 - filled)}]   {(int)percent}%\n{what}\n\nTip: {tip}";
			return (world, detail, $"{(int)percent}%");
		}
	}

	public enum DialogKind { None, Progress, Notice, Retry }

	/// <summary>The native dialog to show at the menu for this stage: a cancellable progress dialog or a one-time notice.</summary>
	public (DialogKind Kind, string Title, string Text, string Key) Dialog(long now)
	{
		lock (_sync)
		{
			string host = Cut(_host, 40);
			string key = _joinNo + ":" + _stage;
			string detail = string.IsNullOrWhiteSpace(_detail) ? null : _detail.Trim();
			string attempt = _attempt > 1 ? $" (attempt {_attempt}{(_maxAttempts > 0 ? " of " + _maxAttempts : "")})" : "";
			switch (_stage)
			{
			case JoinStage.WaitingForSteam: return (DialogKind.Progress, "Joining", "Waiting for Steam to start...", key);
			case JoinStage.LookingForHost: return (DialogKind.Progress, "Joining", detail ?? "Looking for a friend who is hosting...", key);
			case JoinStage.Connecting: return (DialogKind.Progress, "Joining " + host, "Connecting" + attempt + "..." + (detail != null ? "\n" + detail : ""), key);
			case JoinStage.WaitingForHostWorld: return (DialogKind.Progress, "Joining " + host, "Connected. Waiting for the host to open a world...", key);
			case JoinStage.Lost when _autoRejoin: return (DialogKind.Progress, "Connection lost", "Lost the connection to " + host + ".\n" + (_inGame ? "Returning to the main menu, then rejoining..." : "Rejoining..."), key);
			case JoinStage.Lost: return (DialogKind.Retry, "Connection lost", "Lost the connection to " + host + "." + (detail != null ? "\n\n" + detail : ""), key);
			case JoinStage.GaveUp: return (DialogKind.Retry, "Could not connect", "No answer from " + host + ".\n\nCheck the address and that the server is running.", key);
			case JoinStage.Failed: return (DialogKind.Retry, "Join failed", "The multiplayer world could not be loaded" + (detail != null ? ":\n" + detail : "."), key);
			default: return (DialogKind.None, "", "", key);
			}
		}
	}

	public static string Cut(string s, int max)
	{
		if (s.Length > max)
		{
			return s.Substring(0, Math.Max(1, max - 3)).TrimEnd() + "...";
		}
		return s;
	}

	private static string Sentence(string s)
	{
		s = s.Trim().TrimEnd('.');
		if (s.Length != 0)
		{
			return char.ToUpperInvariant(s[0]) + s.Substring(1) + ".";
		}
		return "";
	}

	private static string Mib(long bytes)
	{
		return ((double)bytes / 1048576.0).ToString((bytes < 10485760) ? "0.0" : "0", CultureInfo.InvariantCulture);
	}

	private static string Clock(long ms)
	{
		long num = Math.Max(0L, ms) / 1000;
		return $"{num / 60}:{num % 60:00}";
	}

	public static string Sanitize(string? s, int max = 400)
	{
		if (string.IsNullOrEmpty(s))
		{
			return "";
		}
		StringBuilder stringBuilder = new StringBuilder(Math.Min(s.Length, max));
		foreach (char c in s)
		{
			if (stringBuilder.Length >= max)
			{
				break;
			}
			StringBuilder stringBuilder2 = stringBuilder;
			stringBuilder2.Append(c switch
			{
				'{' => '(', 
				'}' => ')', 
				_ => (c == (char)10 || !char.IsControl(c)) ? c : ' ', 
			});
		}
		return stringBuilder.ToString();
	}
}
