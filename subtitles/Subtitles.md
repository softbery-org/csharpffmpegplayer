# Subtitles
Created by Softbery by Paweł Tobis

---

# Subtitles Documentation

This documentation covers the classes, enums, and exceptions in the `Subtitles` namespace, which provides functionality for managing and parsing subtitles in media applications. The components support loading SRT subtitle files, parsing subtitle blocks, and handling related events and exceptions.

- **Version Information**: The classes are based on versions around 0.1.11.* and 0.1.17.*.
- **Namespace**: `Subtitles`

## Classes

### Subtitle

Represents a single subtitle entry with an ID, start and end times, and text content.

#### Properties
- `Id` (int): The unique identifier for the subtitle.
- `StartTime` (TimeSpan): The start time of the subtitle.
- `EndTime` (TimeSpan): The end time of the subtitle.
- `Items` (string[]): An array of text lines for the subtitle.

#### Constructors
- `Subtitle(int id, TimeSpan startTime, TimeSpan endTime, string[] text)`: Initializes a new subtitle with the given ID, times, and text.

#### Methods
- `override string ToString()`: Returns a formatted string representation of the subtitle, e.g., "[ID] StartTime --> EndTime: Text".

### SubtitleFontArgs : EventArgs

Event arguments for subtitle font changes, providing font-related properties.

#### Properties
- `FontFamily` (FontFamily): The font family (default: "Calibri").
- `FontSize` (double): The font size (default: 24.0).
- `FontWeight` (FontWeight): The font weight (default: Normal).
- `FontDecoration` (TextDecorationCollection): The text decorations (default: Baseline).

### SubtitleManager

Manages a collection of subtitles loaded from a file (e.g., SRT format). Handles loading, parsing, and querying subtitles.

#### Properties
- `Subtitles` (List<Subtitle>): A read-only copy of the loaded subtitles.
- `Count` (int): The number of subtitles.
- `Path` (string): Gets or sets the path to the subtitle file. Setting this loads the subtitles and may throw exceptions on failure.

#### Constructors
- `SubtitleManager(string path)`: Initializes and loads subtitles from the specified path. Throws exceptions on load or parse failures.

#### Methods
- `List<Subtitle> GetSubtitles()`: Returns a copy of all loaded subtitles.
- `List<Subtitle> GetStartToEndTimeSpan(TimeSpan start, TimeSpan end)`: Returns subtitles within the specified time range.

#### Private Methods
- `string ReadFileContent(string path)`: Reads the content of the subtitle file, throwing exceptions if the file is not found or unreadable.
- `void LoadSubtitlesFromFile(string path)`: Parses the file content using regex to extract subtitle blocks. Throws `SubtitleParseException` on parsing errors.

**Notes**: Uses a regex to parse SRT format: `^(\d+)\r?\n(\d{2}:\d{2}:\d{2},\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2},\d{3})\r?\n((?:.*\r?\n)*?)(?=\r?\n\d+|$)`.

## Enums

### SubtitleExtensions

Defines supported subtitle file extensions.

#### Values
- `txt`
- `sub`
- `srt`

## Exceptions

### SubtitleLoadException : Exception

Thrown when there is an error loading a subtitle file.

#### Constructors
- `SubtitleLoadException()`: Default constructor.
- `SubtitleLoadException(string message)`: Initializes with a message.
- `SubtitleLoadException(string message, Exception innerException)`: Initializes with a message and inner exception.

### SubtitleParseException : Exception

Thrown when there is an error parsing subtitle content.

#### Constructors
- `SubtitleParseException()`: Default constructor.
- `SubtitleParseException(string message)`: Initializes with a message.
- `SubtitleParseException(string message, Exception innerException)`: Initializes with a message and inner exception.

## Usage Example

```csharp
using Subtitles;

try
{
    var manager = new SubtitleManager("path/to/subtitles.[srt, txt, sub]");
    var subtitles = manager.GetSubtitles();
    foreach (var subtitle in subtitles)
    {
        Console.WriteLine(subtitle.ToString());
    }
}
catch (SubtitleLoadException ex)
{
    Console.WriteLine($"Load error: {ex.Message}");
}
catch (SubtitleParseException ex)
{
    Console.WriteLine($"Parse error: {ex.Message}");
}
```

This setup integrates with WPF for UI-related subtitle display (e.g., via FontFamily in events). For error handling, the manager displays message boxes on exceptions during initialization or path setting.