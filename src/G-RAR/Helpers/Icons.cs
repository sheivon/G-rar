using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GRAR.Helpers;

/// <summary>Resolves icon resources (DrawingImage) declared in Styles/Icons.axaml.</summary>
public static class Icons
{
    private static readonly Dictionary<string, IImage?> Cache = new(StringComparer.Ordinal);

    public static IImage? Get(string key)
    {
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        IImage? image = null;
        try
        {
            if (Application.Current?.TryFindResource(key, out var resource) == true)
                image = resource as IImage;
        }
        catch
        {
            // Missing icon resources are non-fatal.
        }

        Cache[key] = image;
        return image;
    }
}
