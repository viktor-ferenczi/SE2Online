namespace Y51_SE2Online.Core;

public interface IMpLog
{
	void Info(string message);

	void Warn(string message);

	void Error(string message);
}
