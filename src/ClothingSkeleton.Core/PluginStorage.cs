namespace ClothingSkeleton.Core;

/// <summary>Runtime files are rooted at the installed plugin, never at the build checkout.</summary>
public sealed class PluginStorage
{
    public PluginStorage(string directory) => Root=Path.GetFullPath(directory);
    public string Root { get; }
    public string Mods=>Path.Combine(Root,"Mods");
    public string Projects=>Path.Combine(Root,"Projects");
    public string ReturnFile=>Path.Combine(Root,"BlenderReturn","edited.gltf");
    public string Settings=>Path.Combine(Root,"workshop-settings.json");
    public string Bridge=>Path.Combine(Root,"BlenderBridge");
    public string Session=>Path.Combine(Root,"BlenderSession");

    public string? Relative(string? path)
    {
        if(string.IsNullOrWhiteSpace(path))return null;
        var relative=Path.GetRelativePath(Root,Path.GetFullPath(path));
        return IsRelativeInside(relative)?relative.Replace('\\','/'):null;
    }

    public string? Resolve(string? relative)
    {
        if(string.IsNullOrWhiteSpace(relative) || !IsRelativeInside(relative))return null;
        var path=Path.GetFullPath(Path.Combine(Root,relative));
        return Relative(path) is not null?path:null;
    }

    private static bool IsRelativeInside(string path)=>!Path.IsPathRooted(path) && path!=".." &&
        !path.StartsWith("../",StringComparison.Ordinal) && !path.StartsWith("..\\",StringComparison.Ordinal);

    public static string NormalizeBlenderPath(string input)
    {
        var path=Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'));
        return Directory.Exists(path)?Path.Combine(path,"blender.exe"):path;
    }

    public static bool IsBlenderExecutable(string? path)=>!string.IsNullOrWhiteSpace(path) && File.Exists(path)
        && Path.GetFileName(path).Equals("blender.exe",StringComparison.OrdinalIgnoreCase);
}
