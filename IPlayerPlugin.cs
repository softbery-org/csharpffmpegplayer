namespace CSharpFFmpeg;

public interface IPlayerPlugin
{
    string Name { get; }
    string Description { get; }

    bool CanHandle(string url);
    List<PlaylistEntry> Resolve(string url);
}
