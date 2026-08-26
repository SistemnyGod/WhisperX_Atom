using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WhisperX_Atom_Desktop.Services;

namespace WhisperX_Atom_Desktop.Pages;

public sealed partial class ComingSoonPage : Page
{
    public ComingSoonPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is ComingSoonNavigationRequest request)
        {
            TitleText.Text = request.Title;
            DescriptionText.Text = request.Description;
        }
    }
}

public sealed record ComingSoonNavigationRequest(FrontendServices Services, string Title, string Description);
