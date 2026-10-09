using Altruist;
using Altruist.Dashboard;
using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Moq;

namespace Tests.Gaming.Dashboard;

public class DashboardPortalConnectionTests
{
    [Fact]
    public async Task Each_viewer_keeps_its_own_connection_id()
    {
        var portal = new DashboardPortal(
            Mock.Of<IGameWorldOrganizer3D>(), Mock.Of<IDashboardGizmoRegistry>(),
            Mock.Of<IAltruistRouter>(), Mock.Of<IConnectionManager>());
        var first = new AltruistConnection();
        first.SetId("viewer-1");
        var second = new AltruistConnection();
        second.SetId("viewer-2");

        if ((object)portal is OnConnectingAsync connecting)
        {
            await connecting.OnConnectingAsync("viewer-1", null!, first);
            await connecting.OnConnectingAsync("viewer-2", null!, second);
        }

        Assert.NotEqual(first.ConnectionId, second.ConnectionId);
    }
}
