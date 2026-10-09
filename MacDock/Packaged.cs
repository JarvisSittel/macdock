using System.Xml.Linq;
using IOPath = System.IO.Path;

namespace MacDock;

/// <summary>
/// Desktop apps installed as MSIX packages (e.g. Arc) run from C:\Program Files\WindowsApps\Name_Version_Arch__PublisherId.
/// Their exe may carry no icon (the package's logo is the icon), and the folder changes on every update.
/// </summary>
static class Packaged
{
    static readonly string Root = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + IOPath.DirectorySeparatorChar;

    /// <summary>Splits a packaged exe path into its package folder name and the exe's path inside the package.</summary>
    static bool TrySplit(string path, out string fullName, out string relative)
    {
        fullName = relative = null;
        if (path == null || !path.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = path[Root.Length..];
        int slash = rest.IndexOf(IOPath.DirectorySeparatorChar);
        if (slash <= 0) return false;
        fullName = rest[..slash];
        relative = rest[(slash + 1)..];
        return true;
    }

    /// <summary>Name_Version_Arch_ResourceId_PublisherId → Name_PublisherId (package names can't contain '_').</summary>
    static string FamilyName(string fullName)
    {
        var parts = fullName.Split('_');
        return parts.Length >= 2 ? parts[0] + "_" + parts[^1] : null;
    }

    /// <summary>The AppUserModelID of the package app that runs this exe, e.g. "TheBrowserCompany.Arc_ttt1ap7aakyb4!Arc".</summary>
    public static string AppIdForExe(string path)
    {
        if (!TrySplit(path, out var fullName, out var relative)) return null;
        if (AppIds.TryGetValue(path, out var cached)) return cached;
        string result = null;
        try
        {
            var manifest = XDocument.Load(IOPath.Combine(Root, fullName, "AppxManifest.xml"));
            var app = manifest.Descendants().FirstOrDefault(e => e.Name.LocalName == "Application"
                && string.Equals(((string)e.Attribute("Executable"))?.Replace('/', '\\'), relative, StringComparison.OrdinalIgnoreCase));
            var id = (string)app?.Attribute("Id");
            if (id != null) result = FamilyName(fullName) + "!" + id;
        }
        catch (Exception e) { Log.Error("package manifest " + path, e); }
        return AppIds[path] = result;
    }

    static readonly Dictionary<string, string> AppIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether two exe paths are the same app from (possibly different versions of) the same package.</summary>
    public static bool SameApp(string a, string b) =>
        TrySplit(a, out var fa, out var ra) && TrySplit(b, out var fb, out var rb)
        && string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase)
        && string.Equals(FamilyName(fa), FamilyName(fb), StringComparison.OrdinalIgnoreCase);

    /// <summary>A packaged exe path from before an update, moved to where the installed version lives now.</summary>
    public static string Current(string path)
    {
        if (!TrySplit(path, out var fullName, out var relative) || File.Exists(path)) return path;
        try
        {
            var pkg = new global::Windows.Management.Deployment.PackageManager()
                .FindPackagesForUser("", FamilyName(fullName)).FirstOrDefault();
            var moved = pkg != null ? IOPath.Combine(pkg.InstalledPath, relative) : null;
            if (moved != null && File.Exists(moved)) return moved;
        }
        catch (Exception e) { Log.Error("package lookup " + path, e); }
        return path;
    }
}
