using System.IO;
using System.Reflection;

namespace CSharpFFmpeg;

public static class PluginLoader
{
    private static readonly List<IPlayerPlugin> _plugins = new();
    public static IReadOnlyList<IPlayerPlugin> Plugins => _plugins;

    public static void LoadPlugins(string? pluginsDir = null)
    {
        pluginsDir ??= Path.Combine(AppContext.BaseDirectory, "plugins");

        if (!Directory.Exists(pluginsDir))
        {
            Console.Error.WriteLine($"[Plugins] Directory not found: {pluginsDir}");
            return;
        }

        foreach (var dll in Directory.GetFiles(pluginsDir, "*.dll"))
        {
            try
            {
                var asm = Assembly.LoadFrom(dll);
                foreach (var type in asm.GetTypes())
                {
                    if (typeof(IPlayerPlugin).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
                    {
                        var plugin = (IPlayerPlugin)Activator.CreateInstance(type)!;
                        _plugins.Add(plugin);
                        Console.Error.WriteLine($"[Plugins] Loaded: {plugin.Name} — {plugin.Description}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Plugins] Failed to load {Path.GetFileName(dll)}: {ex.Message}");
            }
        }

        if (_plugins.Count == 0)
            Console.Error.WriteLine("[Plugins] No plugins found");
    }

    public static IPlayerPlugin? FindPluginForUrl(string url)
    {
        foreach (var p in _plugins)
        {
            if (p.CanHandle(url))
                return p;
        }
        return null;
    }
}
