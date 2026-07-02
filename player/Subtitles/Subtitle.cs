// Version: 0.1.17.22
namespace Subtitles;

public class Subtitle
{
	public int Id { get; }

	public TimeSpan StartTime { get; }

	public TimeSpan EndTime { get; }

	public string[] TextLines { get; }

	public Subtitle(int id, TimeSpan startTime, TimeSpan endTime, string[] text)
	{
		Id = id;
		StartTime = startTime;
		EndTime = endTime;
		TextLines = text;
	}

	public override string ToString()
	{
		return string.Format("[{0}] {1} --> {2}: {3}", Id, StartTime, EndTime, string.Join(" ", TextLines));
	}
}
