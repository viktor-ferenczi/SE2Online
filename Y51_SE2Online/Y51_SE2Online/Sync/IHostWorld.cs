using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public interface IHostWorld
{
	void OnSocialRequest(PeerId peer, ushort clientId, SocialRequest request) { }
	void OnAdminRequest(PeerId? peer, ushort clientId, AdminRequest request) { }
	bool WorldReady { get; }

	string WorldName { get; }

	void RequestCapture(PeerId client);

	void CancelCapture(PeerId client);

	void RequestCharacter(PeerId client, ushort clientId, string name, int choice, bool respawn)
	{
	}

	void ReleaseCharacter(PeerId client, ushort clientId)
	{
	}

	void OnClientStory(PeerId client, ushort clientId, StoryMove move)
	{
	}

	void ForgetStories(PeerId client)
	{
	}

	void RegisterPlayer(PeerId client, ushort clientId, string identityKey, string name)
	{
	}

	void OnObservableEdits(PeerId client, ushort clientId, IReadOnlyList<MemberEdit> edits)
	{
	}
}
