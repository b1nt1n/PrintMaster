using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Xml.Linq;

namespace PrintMaster.Tests;

// Share the real application resources, but never launch an extra production window,
// offer the user's recovery session, or create recovery files in the user's profile.
internal sealed class TestApplication : App
{
    protected override void OnStartup(StartupEventArgs e) { }

    internal static TestApplication Create()
    {
        var app=new TestApplication { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        var source=XDocument.Load(Path.GetFullPath(Path.Combine(Fixtures.Root,"..","..","src","PrintMaster","App.xaml")));
        XNamespace ns="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources=new XElement(ns+"ResourceDictionary",source.Root!.Attributes().Where(a=>a.IsNamespaceDeclaration),source.Root.Element(ns+"Application.Resources")!.Elements());
        resources.SetAttributeValue(XNamespace.Xmlns+"views","clr-namespace:PrintMaster.Views;assembly=PrintMaster");
        foreach(var element in resources.Descendants().Where(e=>e.Name.NamespaceName=="clr-namespace:PrintMaster.Views").ToArray())
            element.Name=XNamespace.Get("clr-namespace:PrintMaster.Views;assembly=PrintMaster")+element.Name.LocalName;
        app.Resources=(ResourceDictionary)XamlReader.Parse(resources.ToString());
        return app;
    }
}
