namespace AudioBookReader.App.Services;

// CarPlay is out of scope for v1 (decided during iOS-port planning — Android Auto's
// content://androidx.car.app.connection query has no CarPlay equivalent; that would need
// CPTemplateApplicationSceneDelegate/MPPlayableContentManager, a separate feature). Never
// connected is the honest answer until that's built.
public partial class CarConnection
{
    public partial bool IsConnected => false;
}
