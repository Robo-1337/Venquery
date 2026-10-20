using Venquery.Controls;

namespace Venquery;

// The Organize page (File Organizer) is in every edition, Legacy included: it only uses the file system.
// The page lives in Controls/OrganizerView; the engine in Services/OrganizerService.
public partial class MainWindow
{
    private OrganizerView? _organizerView;

    private void InitOrganizer()
    {
        _organizerView = new OrganizerView(_settings)
        {
            BeginTask = (title, determinate) => BeginTask(title, determinate),
        };
        _organizerView.StatusChanged += s => StatusLine.Text = s;
        OrganizerHost.Content = _organizerView;
    }

    /// <summary>The Files page tabs: Organize or "What's using space".</summary>
    private void ShowOrganizerMode(bool space) => _organizerView?.ShowMode(space);
}
