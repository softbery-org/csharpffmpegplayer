// Version: 0.1.17.22
using System;

namespace Subtitles;

public class SubtitleFontArgs : EventArgs
{
	public string FontFamily { get; set; } = "DejaVu Sans";

	public double FontSize { get; set; } = 24.0;

	public bool Bold { get; set; } = false;

	public bool Italic { get; set; } = false;
}
