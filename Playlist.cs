using System.IO;

namespace CSharpFFmpeg;

public enum RepeatMode
{
    Once,       // Play list once, then stop
    All,        // Repeat entire list
    One,        // Repeat single track
    Shuffle,    // Random order, loop forever
}

public sealed class Playlist
{
    private readonly List<PlaylistEntry> _entries = new();
    private int _currentIndex;
    private readonly Random _rng = new();

    public int Count => _entries.Count;
    public int CurrentIndex => _currentIndex;
    public PlaylistEntry? CurrentEntry => _currentIndex >= 0 && _currentIndex < _entries.Count ? _entries[_currentIndex] : null;
    public string Current => CurrentEntry?.Url ?? "";
    public bool HasNext => _currentIndex < _entries.Count - 1;
    public bool HasPrev => _currentIndex > 0;
    public IReadOnlyList<PlaylistEntry> Entries => _entries;
    public RepeatMode RepeatMode { get; set; } = RepeatMode.Once;

    public Playlist()
    {
        _currentIndex = -1;
    }

    public void Add(string file, string? displayName = null, string? sourceUrl = null)
    {
        if (string.IsNullOrWhiteSpace(file)) return;
        if (file.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            file.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _entries.Add(new PlaylistEntry(file, displayName, sourceUrl));
        }
        else if (File.Exists(file))
        {
            _entries.Add(new PlaylistEntry(Path.GetFullPath(file), displayName, sourceUrl));
        }
    }

    public void Add(PlaylistEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Url)) return;
        _entries.Add(entry);
    }

    public void AddUrl(string url, string? displayName = null, string? sourceUrl = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        _entries.Add(new PlaylistEntry(url, displayName, sourceUrl));
    }

    public void AddRange(IEnumerable<string> files)
    {
        foreach (var f in files)
            Add(f);
    }

    public void AddRange(IEnumerable<PlaylistEntry> entries)
    {
        foreach (var e in entries)
            Add(e);
    }

    private static readonly string[] MediaExtensions =
    {
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".flv", ".wmv", ".mpg", ".mpeg",
        ".m4v", ".ts", ".m2ts", ".mts", ".vob", ".ogv", ".3gp", ".rm", ".rmvb",
        ".asf", ".f4v", ".dv", ".mp3", ".aac", ".flac", ".wav", ".ogg", ".opus",
        ".m4a", ".wma", ".ac3", ".dts", ".amr", ".aiff", ".aif", ".alac",
        ".m3u8", ".m3u",
    };

    public void AddFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return;
        AddFolderRecursive(folderPath);
    }

    private void AddFolderRecursive(string folderPath)
    {
        // If folder contains .m3u8 playlists, add those directly (HLS support)
        var m3u8Files = Directory.EnumerateFiles(folderPath, "*.m3u8")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (m3u8Files.Count > 0)
        {
            foreach (var m in m3u8Files)
                if (File.Exists(m)) _entries.Add(new PlaylistEntry(Path.GetFullPath(m)));
            // Still recurse into subdirs but skip this folder's .ts segments
            foreach (var sub in Directory.EnumerateDirectories(folderPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                AddFolderRecursive(sub);
            return;
        }

        // No .m3u8 — add individual media files
        var files = Directory.EnumerateFiles(folderPath, "*")
            .Where(f => MediaExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
            _entries.Add(new PlaylistEntry(Path.GetFullPath(f)));

        foreach (var sub in Directory.EnumerateDirectories(folderPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            AddFolderRecursive(sub);
    }

    public void LoadFromM3U(string path)
    {
        foreach (var line in File.ReadAllLines(path))
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;
            Add(trimmed);
        }
    }

    // Returns true if advanced to next track, false if playlist ended
    public bool AdvanceToNext()
    {
        if (_entries.Count == 0) return false;
        switch (RepeatMode)
        {
            case RepeatMode.One:
                return true;
            case RepeatMode.Shuffle:
                int next;
                do { next = _rng.Next(_entries.Count); } while (_entries.Count > 1 && next == _currentIndex);
                _currentIndex = next;
                return true;
            case RepeatMode.All:
                _currentIndex = (_currentIndex + 1) % _entries.Count;
                return true;
            case RepeatMode.Once:
            default:
                if (_currentIndex < _entries.Count - 1) { _currentIndex++; return true; }
                return false;
        }
    }

    // Returns true if advanced to prev track, false if at start
    public bool AdvanceToPrev()
    {
        if (_entries.Count == 0) return false;
        switch (RepeatMode)
        {
            case RepeatMode.One:
                return true;
            case RepeatMode.Shuffle:
                int prev;
                do { prev = _rng.Next(_entries.Count); } while (_entries.Count > 1 && prev == _currentIndex);
                _currentIndex = prev;
                return true;
            case RepeatMode.All:
                _currentIndex = (_currentIndex - 1 + _entries.Count) % _entries.Count;
                return true;
            case RepeatMode.Once:
            default:
                if (_currentIndex > 0) { _currentIndex--; return true; }
                return false;
        }
    }

    public bool MoveNext()
    {
        if (!HasNext) return false;
        _currentIndex++;
        return true;
    }

    public bool MovePrev()
    {
        if (!HasPrev) return false;
        _currentIndex--;
        return true;
    }

    public void MoveTo(int index)
    {
        if (index >= 0 && index < _entries.Count)
            _currentIndex = index;
    }

    public void Reset()
    {
        _currentIndex = _entries.Count > 0 ? 0 : -1;
    }

    public void Clear()
    {
        _entries.Clear();
        _currentIndex = -1;
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _entries.Count) return;
        _entries.RemoveAt(index);
        if (_entries.Count == 0)
        {
            _currentIndex = -1;
            return;
        }
        if (_currentIndex > index)
            _currentIndex--;
        else if (_currentIndex == index)
            _currentIndex = Math.Min(_currentIndex, _entries.Count - 1);
    }

    // Move item from fromIndex to toIndex, adjust currentIndex
    public void Move(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= _entries.Count) return;
        if (toIndex < 0 || toIndex >= _entries.Count) return;
        if (fromIndex == toIndex) return;

        var item = _entries[fromIndex];
        _entries.RemoveAt(fromIndex);
        _entries.Insert(toIndex, item);

        // Adjust current index
        if (_currentIndex == fromIndex)
            _currentIndex = toIndex;
        else if (fromIndex < toIndex)
        {
            if (_currentIndex > fromIndex && _currentIndex <= toIndex) _currentIndex--;
        }
        else
        {
            if (_currentIndex >= toIndex && _currentIndex < fromIndex) _currentIndex++;
        }
    }

    public string GetDisplayName(int index)
    {
        if (index < 0 || index >= _entries.Count) return "";
        return _entries[index].DisplayName;
    }
}
