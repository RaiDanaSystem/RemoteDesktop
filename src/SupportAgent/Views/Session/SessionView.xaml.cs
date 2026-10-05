using System.Windows;
using System.Windows.Controls;
using SupportAgent.ViewModels.Session;

namespace SupportAgent.Views.Session;

public partial class SessionView : UserControl
{
    public SessionView()
    {
        InitializeComponent();
    }

    public UIElement ScreenCaptureElement => RemoteScreenImage;
}
