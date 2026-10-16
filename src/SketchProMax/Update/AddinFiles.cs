using System.Xml.Linq;

namespace SketchProMax.Update;

/// <summary>
/// What Revit sees of Sketch ProMax, kept the same from release to release. Revit remembers "Always Load" for an unsigned
/// add-in by its add-in ID and the place it loads from (and asks again when the file there changes), so:
///   - SketchProMax.addin always names the loader at one fixed path, SketchProMax\SketchProMax.Loader.dll next to it, and
///     never a version folder or the core;
///   - the loader there is replaced only when its bytes differ, and never by an older one;
///   - a file that is already right is never written again.
/// Pure logic, no Revit types, so the tests compile it.
/// </summary>
internal static class AddinFiles
{
    public const string ManifestName = "SketchProMax.addin";
    public const string LoaderDll = "SketchProMax.Loader.dll";
    public const string CoreDll = "SketchProMax.dll";
    public const string LoaderClass = "SketchProMax.Loader.LoaderApp";
    public const string LoaderFolder = "SketchProMax";

    /// <summary>The manifest's ClientId: Revit's "Always Load" is remembered under it. Never change it.</summary>
    public const string ClientId = "6f3c2b1e-8a47-4d2f-9c51-3e7b9d0a4f12";

    /// <summary>The one Assembly path the manifest ever names, relative to the Revit version's add-ins folder.</summary>
    public const string LoaderPath = LoaderFolder + "\\" + LoaderDll;

    public enum LoaderAction
    {
        /// <summary>Nothing to do: no new loader, or the installed one is byte for byte the same.</summary>
        Keep,
        /// <summary>Put the new loader in place.</summary>
        Replace,
        /// <summary>The installed loader is newer (an older core is running); leave it.</summary>
        KeepNewer,
    }

    /// <summary>
    /// Whether to replace the installed loader (hash and file version <paramref name="installedHash"/>,
    /// <paramref name="installedVersion"/>; null hash: none installed) with the one a package carries.
    /// </summary>
    public static LoaderAction DecideLoader(string? packageHash, Version? packageVersion, string? installedHash, Version? installedVersion)
    {
        if (string.IsNullOrEmpty(packageHash)) return LoaderAction.Keep;
        if (string.IsNullOrEmpty(installedHash)) return LoaderAction.Replace;
        if (string.Equals(packageHash, installedHash, StringComparison.OrdinalIgnoreCase)) return LoaderAction.Keep;
        if (packageVersion is not null && installedVersion is not null && packageVersion < installedVersion) return LoaderAction.KeepNewer;
        return LoaderAction.Replace;
    }

    /// <summary>True when <paramref name="template"/> is a manifest with one add-in entry naming the loader at the fixed path.</summary>
    public static bool IsSettled(string? template)
    {
        var doc = TryParse(template);
        if (doc?.Root is null) return false;
        var entries = doc.Root.Elements("AddIn").ToList();
        return entries.Count == 1
               && SamePath(Value(entries[0], "Assembly"), LoaderPath)
               && Value(entries[0], "FullClassName") == LoaderClass;
    }

    /// <summary>
    /// <paramref name="existing"/> with one add-in entry naming the loader at the fixed path, for when the package has no
    /// settled manifest to copy. Other Sketch ProMax entries are dropped; other add-ins' entries are kept. Null when it
    /// can't be read.
    /// </summary>
    public static string? Settle(string? existing)
    {
        var doc = TryParse(existing);
        if (doc?.Root is null) return null;
        var ours = doc.Root.Elements("AddIn").Where(IsOurs).ToList();
        if (ours.Count == 0) return null;
        foreach (var extra in ours.Skip(1)) extra.Remove();
        Set(ours[0], "Assembly", LoaderPath);
        Set(ours[0], "FullClassName", LoaderClass);
        return doc.Declaration is null ? doc.ToString() : doc.Declaration + "\n" + doc;
    }

    /// <summary>True when two manifests say the same thing (layout and whitespace aside), so the file is left as it is.</summary>
    public static bool SameManifest(string? a, string? b)
    {
        var x = TryParse(a);
        var y = TryParse(b);
        if (x?.Root is null || y?.Root is null) return false;
        Tidy(x.Root);
        Tidy(y.Root);
        return XNode.DeepEquals(x.Root, y.Root);
    }

    /// <summary>
    /// True when a manifest (another .addin file than ours, typically left by a test or an older install) would load
    /// Sketch ProMax too: it has our ClientId or names the loader or the core. Revit would then load it twice, or refuse
    /// the second for its duplicate ID.
    /// </summary>
    public static bool NamesSketchProMax(string? manifest)
    {
        var doc = TryParse(manifest);
        return doc?.Root is not null && doc.Root.Elements("AddIn").Any(IsOurs);
    }

    private static bool IsOurs(XElement addIn)
    {
        var id = Value(addIn, "ClientId") ?? Value(addIn, "AddInId");
        if (id is not null && id.Trim('{', '}').Equals(ClientId, StringComparison.OrdinalIgnoreCase)) return true;
        var file = FileName(Value(addIn, "Assembly"));
        return file.Equals(LoaderDll, StringComparison.OrdinalIgnoreCase) || file.Equals(CoreDll, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Value(XElement parent, string name) => parent.Element(name)?.Value.Trim();

    private static void Set(XElement parent, string name, string value)
    {
        var element = parent.Element(name);
        if (element is null) parent.Add(new XElement(name, value));
        else element.Value = value;
    }

    private static bool SamePath(string? a, string b) =>
        a is not null && a.Replace('/', '\\').Trim().Equals(b, StringComparison.OrdinalIgnoreCase);

    // Manifests use Windows paths; Path.GetFileName on another OS (the tests) would not split them.
    private static string FileName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var at = path!.LastIndexOfAny(['\\', '/']);
        return at < 0 ? path : path.Substring(at + 1);
    }

    private static void Tidy(XElement element)
    {
        foreach (var e in element.DescendantsAndSelf())
            if (!e.HasElements && e.Value.Trim() != e.Value) e.Value = e.Value.Trim();
    }

    private static XDocument? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return XDocument.Parse(text!.TrimStart('﻿')); }
        catch (System.Xml.XmlException) { return null; }
    }
}
