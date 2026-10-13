using System;
using System.Linq;
using Keen.Game2;
using Keen.Game2.Client.UI.Library;
using Keen.Game2.Client.UI.Library.Dialogs.LoadingDialog;
using Keen.Game2.Client.UI.Library.Dialogs.OneOptionDialog;
using Keen.Game2.Client.UI.Overlays;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Localization;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Join progress in SE2's own UI: details on the native loading screen, a cancellable native loading dialog while
/// connecting at the menu, and a native OK dialog when a join fails.
/// </summary>
internal sealed class JoinStatusUi
{
	private const int RefreshMs = 250;

	private const int FindSharedEveryMs = 2000;

	private readonly IMpLog _log;

	private SharedUIComponent? _shared;

	private long _nextMs;

	private long _nextFindMs;

	private bool _failed;

	private LoadingTransitionScreenViewModel? _screen;

	private string? _lastDetail;

	private IObservableDisposable? _dialog;

	private string? _dialogText;

	private string? _noticeKey;

	public long LoadingUpdates;

	public long DialogsShown;

	public bool Failed => _failed;

	public JoinStatusUi(IMpLog log)
	{
		_log = log;
	}

	public void Update(GameAppComponent app, JoinProgress progress, long now)
	{
		if (!_failed && now >= _nextMs)
		{
			_nextMs = now + RefreshMs;
			SharedUIComponent shared = Shared(app, now);
			if (shared != null)
			{
				Apply(shared, progress, now);
			}
		}
	}

	private static long _holdSinceMs = -1;

	/// <summary>Keeps SE2's loading screen up after its own load ends, until the joiner is in their character.</summary>
	internal static void Install(HarmonyLib.Harmony harmony)
	{
		var end = HarmonyLib.AccessTools.GetDeclaredMethods(typeof(SharedUIComponent)).Single(m => m.Name.EndsWith("ITransitionListener.OnTransitionToGameEnd", StringComparison.Ordinal));
		harmony.Patch(end, prefix: new HarmonyLib.HarmonyMethod(typeof(JoinStatusUi), nameof(GameEndPrefix)));
	}

	private static bool GameEndPrefix(Exception? loadingErrorInfo)
	{
		if (loadingErrorInfo != null || MpPlugin.Controller?.Current?.Join != true || ClientWorld.Progress.Stage >= JoinStage.Playing) return true;
		_holdSinceMs = MpClock.NowMs();
		return false;
	}

	private static void ReleaseHold(SharedUIComponent shared, JoinProgress progress, long now)
	{
		if (_holdSinceMs < 0) return;
		bool joining = MpPlugin.Controller?.Current?.Join == true && progress.Stage is >= JoinStage.Receiving and < JoinStage.Playing;
		if (joining && now - _holdSinceMs < 180_000 && ClientWorld.Current?.BoundSession?.SpawnMenu == null) return;
		_holdSinceMs = -1;
		shared.CloseLoadingTransitionScreenIfNeeded();
	}

	internal void Apply(SharedUIComponent shared, JoinProgress progress, long now)
	{
		if (_failed)
		{
			return;
		}
		try
		{
			ReleaseHold(shared, progress, now);
			bool loading = UpdateLoading(shared, progress.LoadingScreen(now));
			// Cancelling leaves the join; never reopen a dialog for a session that is going away.
			UpdateDialog(shared, loading || MpPlugin.Controller?.Current?.Join != true ? default : progress.Dialog(now));
		}
		catch (Exception ex)
		{
			_failed = true;
			CloseDialog();
			_log.Warn($"client: showing the join progress on screen failed and is switched off ({ex.GetType().Name}: {ex.Message}); the log still has every step");
		}
	}

	private SharedUIComponent? Shared(GameAppComponent app, long now)
	{
		try
		{
			if (_shared != null || now < _nextFindMs) return _shared;
			_nextFindMs = now + FindSharedEveryMs;
			return _shared = FindShared(app);
		}
		catch (Exception ex)
		{
			_failed = true;
			_log.Warn($"client: finding the game's UI for the join progress failed ({ex.GetType().Name}: {ex.Message}); the log still has every step");
			return null;
		}
	}

	internal static SharedUIComponent? FindShared(GameAppComponent app)
	{
		return app.Scene.Entities.Select((Entity x) => x.TryGet<SharedUIComponent>()).FirstOrDefault((SharedUIComponent x) => x != null);
	}

	/// <summary>Returns true while the native loading screen is up.</summary>
	private bool UpdateLoading(SharedUIComponent shared, (string Title, string Detail, string Status)? view)
	{
		LoadingTransitionScreenViewModel screen = shared._loadingTransitionScreenViewModel;
		if (screen != _screen)
		{
			_screen = screen;
			_lastDetail = null;
		}
		if (screen == null || view == null)
		{
			return screen != null;
		}
		var (title, detail, status) = view.Value;
		if (detail == _lastDetail)
		{
			return true;
		}
		_lastDetail = detail;
		screen.Name = new LocalizableText(JoinProgress.Sanitize(title, 60));
		screen.FrontText = LocKey.FromString(JoinProgress.Sanitize(detail, 400));
		screen.LoadingText = LocKey.FromString(JoinProgress.Sanitize(status, 20));
		LoadingUpdates++;
		return true;
	}

	private void UpdateDialog(SharedUIComponent shared, (JoinProgress.DialogKind Kind, string Title, string Text, string Key) view)
	{
		if (view.Kind != JoinProgress.DialogKind.Progress)
		{
			CloseDialog();
		}
		if (view.Kind == JoinProgress.DialogKind.Retry)
		{
			if (view.Key == _noticeKey)
			{
				return;
			}
			_noticeKey = view.Key;
			shared.ShowDialog(new Keen.Game2.Client.UI.Library.Dialogs.TwoOptionsDialog.TwoOptionsDialogViewModel(new Keen.Game2.Client.UI.Library.Dialogs.TwoOptionsDialog.TwoOptionsDialogDefinition
			{
				Title = LocKey.FromString(view.Title),
				Content = LocKey.FromString(JoinProgress.Sanitize(view.Text, 400)),
				ConfirmOption = LocKey.FromString("Retry"),
				CancelOption = LocKey.FromString("Close")
			}) { ConfirmAction = MpMenu.Retry });
			DialogsShown++;
		}
		else if (view.Kind == JoinProgress.DialogKind.Notice)
		{
			if (view.Key == _noticeKey)
			{
				return;
			}
			_noticeKey = view.Key;
			shared.ShowDialog(new OneOptionDialogViewModel(new OneOptionDialogDefinition
			{
				Title = LocKey.FromString(view.Title),
				Content = LocKey.FromString(JoinProgress.Sanitize(view.Text, 400)),
				ConfirmOption = LocKey.FromString("OK")
			}));
			DialogsShown++;
		}
		else if (view.Kind == JoinProgress.DialogKind.Progress)
		{
			string text = view.Title + "\n" + view.Text;
			if (_dialog != null && text == _dialogText)
			{
				return;
			}
			CloseDialog();
			_dialogText = text;
			_dialog = shared.ShowDialog(new LoadingDialogViewModel(new LoadingDialogDefinition
			{
				Title = LocKey.FromString(view.Title),
				Content = LocKey.FromString(JoinProgress.Sanitize(view.Text, 400)),
				AllowAbort = true,
				AbortOption = LocKey.FromString("Cancel")
			})
			{
				AbortAction = MpMenu.Leave
			});
			IObservableDisposable dialog = _dialog;
			dialog.OnDisposed += _ =>
			{
				if (_dialog == dialog)
				{
					_dialog = null;
				}
			};
			DialogsShown++;
		}
	}

	private void CloseDialog()
	{
		IObservableDisposable dialog = _dialog;
		_dialog = null;
		_dialogText = null;
		try
		{
			dialog?.Dispose();
		}
		catch (Exception ex)
		{
			_log.WarnOnce("client-dialog-dispose", "client: closing the join dialog threw " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public string Describe()
	{
		if (!_failed)
		{
			return $"on-screen progress: loading screen {LoadingUpdates} updates, {DialogsShown} native dialogs";
		}
		return "on-screen progress off (failed)";
	}
}
