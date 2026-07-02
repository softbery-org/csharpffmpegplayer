// Version: 0.1.17.24
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Subtitles;

public class SubtitleManager
{
	private string _subtitlesFile = "";

	private List<Subtitle> _subtitles = new List<Subtitle>();

	public List<Subtitle> Subtitles => new List<Subtitle>(_subtitles);

	public int Count => _subtitles.Count;

	public string Path
	{
		get
		{
			return _subtitlesFile;
		}
		set
		{
			if (_subtitlesFile != value)
			{
				LoadSubtitlesFromFile(value);
				_subtitlesFile = value;
			}
		}
	}

	public SubtitleManager(string path)
	{
		LoadSubtitlesFromFile(path);
		_subtitlesFile = path;
	}

	public SubtitleManager()
	{
	}

	public List<Subtitle> GetSubtitles()
	{
		return new List<Subtitle>(_subtitles);
	}

	public Subtitle? GetSubtitleAtTime(TimeSpan time)
	{
		return _subtitles.FirstOrDefault(s => time >= s.StartTime && time <= s.EndTime);
	}

	public List<Subtitle> GetStartToEndTimeSpan(TimeSpan start, TimeSpan end)
	{
		return _subtitles.Where((item) => item.StartTime >= start && item.EndTime <= end).ToList();
	}

	private string ReadFileContent(string path)
	{
		FileInfo file = new FileInfo(path);
		if (!file.Exists)
		{
			throw new FileNotFoundException("Subtitle file not found.", path);
		}
		try
		{
			return ReadFileDetectEncoding(file.FullName);
		}
		catch (Exception innerException)
		{
			throw new IOException("Could not read subtitle file '" + path + "'.", innerException);
		}
	}

	private static string ReadFileDetectEncoding(string path)
	{
		byte[] bytes = File.ReadAllBytes(path);

		if (bytes.Length >= 3 &&
			bytes[0] == 0xEF &&
			bytes[1] == 0xBB &&
			bytes[2] == 0xBF)
		{
			return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
		}

		try
		{
			return Encoding.UTF8.GetString(bytes);
		}
		catch
		{
			return Encoding.GetEncoding(1250).GetString(bytes);
		}
	}

	private void LoadSubtitlesFromFile(string path)
	{
		_subtitles.Clear();
		string fileContent;
		try
		{
			fileContent = ReadFileContent(path);
		}
		catch (Exception innerException)
		{
			throw new SubtitleLoadException("Failed to load file '" + path + "'.", innerException);
		}
		Regex regex = new Regex("^(\\d+)\\r?\\n(\\d{2}:\\d{2}:\\d{2},\\d{3})\\s*-->\\s*(\\d{2}:\\d{2}:\\d{2},\\d{3})\\r?\\n((?:.*\\r?\\n)*?)(?=\\r?\\n\\d+|$)", RegexOptions.Multiline);
		MatchCollection matches = regex.Matches(fileContent);
		if (matches.Count == 0 && !string.IsNullOrWhiteSpace(fileContent))
		{
			throw new SubtitleParseException("No subtitle blocks found or file is malformed.");
		}
		foreach (Match match in matches)
		{
			try
			{
				int id = int.Parse(match.Groups[1].Value);
				TimeSpan startTime = TimeSpan.Parse(match.Groups[2].Value.Replace(',', '.'));
				TimeSpan endTime = TimeSpan.Parse(match.Groups[3].Value.Replace(',', '.'));
				string[] text = match.Groups[4].Value.Trim().Split(new string[2] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
				_subtitles.Add(new Subtitle(id, startTime, endTime, text));
			}
			catch (FormatException innerException2)
			{
				throw new SubtitleParseException("Error parsing subtitle block (ID: " + match.Groups[1].Value + "). Check format of times or text. Raw block: \n" + match.Value, innerException2);
			}
			catch (Exception innerException3)
			{
				throw new SubtitleParseException("An unexpected error occurred while processing subtitle block (ID: " + match.Groups[1].Value + ").", innerException3);
			}
		}
	}
}
