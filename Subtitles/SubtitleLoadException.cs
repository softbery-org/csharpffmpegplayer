// Version: 0.1.17.22
using System;

namespace Subtitles;

public class SubtitleLoadException : Exception
{
	public SubtitleLoadException()
	{
	}

	public SubtitleLoadException(string message)
		: base(message)
	{
	}

	public SubtitleLoadException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
