using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public interface IStorySink
{
	void SendStoryMove(PeerId peer, StoryMove move);

	void SendStoryFinish(PeerId peer, StoryFinish finish);
}
