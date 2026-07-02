// Version: 0.1.17.22
using System;

namespace Subtitles;

public class SubtitleParseException : Exception
{
	public SubtitleParseException()
	{
	}

	public SubtitleParseException(string message)
		: base(message)
	{
	}

	public SubtitleParseException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
