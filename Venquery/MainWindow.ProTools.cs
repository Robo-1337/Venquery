using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Venquery.Controls;

namespace Venquery;

// Pro Tools lives entirely in this partial + Controls/ProToolsView + Services/ProToolsService. All three
// are excluded from the build unless PROTOOLS is defined (every .NET 8 edition since 1.1.0; Legacy never
// has it), so an edition without the constant carries none of the page, its nav entry or the elevated read.
public partial class MainWindow
{
    private ProToolsView? _proToolsView;

    /// <summary>Build the Pro Tools page (1.4.0: a tab of Health &amp; Repair, named per edition by
    /// Edition.ToolsName, so no rail entry of its own). Called from the ctor only under <c>#if PROTOOLS</c>.</summary>
    private void InitProTools()
    {
        _proToolsView = new ProToolsView();
        _proToolsView.StatusChanged += s => StatusLine.Text = s;
        ProToolsHost.Content = _proToolsView;
    }
}
