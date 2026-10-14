namespace SketchProMax.Tools;

/// <summary>
/// The class wrapped around every C# snippet Claude writes. Kept free of Revit types so the tests can compile it.
/// </summary>
internal static class ScriptSource
{
    internal const string Header = """
        using System;
        using System.Linq;
        using System.Collections.Generic;
        using Autodesk.Revit.DB;
        using Autodesk.Revit.DB.Architecture;
        using Autodesk.Revit.DB.Structure;
        using Autodesk.Revit.DB.Mechanical;
        using Autodesk.Revit.DB.Plumbing;
        using Autodesk.Revit.DB.Electrical;
        using Autodesk.Revit.UI;
        public static class SketchProMaxScript
        {
            public static object Run(UIApplication app, UIDocument uidoc, Document doc, System.Text.StringBuilder log, IDictionary<string, object> args)
            {
        #line 1 "snippet"
        """;

    internal const string Footer = """

        #line default
            }
        }
        """;

    /// <summary>The full source for a snippet. The newline matters: without it the snippet's first line lands on the #line directive.</summary>
    public static string Wrap(string code) => Header + "\n" + code + Footer;
}
