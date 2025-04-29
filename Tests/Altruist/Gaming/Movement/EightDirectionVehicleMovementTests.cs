using Altruist.Physx;
using FarseerPhysics.Dynamics;
using Microsoft.Extensions.Logging;
using Moq;

namespace Altruist.Gaming.Movement;

public class EightDirectionVehicleMovementServiceTests
{
    private readonly TestVehicleMovementService _movementService;

    public EightDirectionVehicleMovementServiceTests()
    {
        var contextMock = new Mock<IPortalContext>();
        var playerServiceMock = new Mock<IPlayerService<TestVehicle>>();
        var cacheProviderMock = new Mock<ICacheProvider>();
        var loggerFactoryMock = new Mock<ILoggerFactory>();
        var movementPhysxMock = new Mock<MovementPhysx>();

        _movementService = new TestVehicleMovementService(
            playerServiceMock.Object,
            movementPhysxMock.Object,
            cacheProviderMock.Object,
            loggerFactoryMock.Object
        );
    }

    [Fact]
    public void HandleTurboFuel_WhenTurboActive_ShouldDecreaseFuel()
    {
        // Arrange
        var vehicle = new TestVehicle { TurboFuel = 10, MaxTurboFuel = 20 };

        // Act
        _movementService.TestHandleTurboFuel(vehicle, turbo: true);

        // Assert
        Assert.Equal(9, vehicle.TurboFuel);
    }

    [Fact]
    public void HandleTurboFuel_WhenNotTurbo_ShouldRecoverFuel()
    {
        // Arrange
        var vehicle = new TestVehicle { TurboFuel = 10, MaxTurboFuel = 20 };

        // Act
        _movementService.TestHandleTurboFuel(vehicle, turbo: false);

        // Assert
        Assert.True(vehicle.TurboFuel > 10);
        Assert.True(vehicle.TurboFuel <= 20);
    }

    // Test Classes
    private class TestVehicleMovementService : EightDirectionVehicleMovementService<TestVehicle>
    {
        public TestVehicleMovementService(IPlayerService<TestVehicle> playerService, MovementPhysx movementPhysx, ICacheProvider cacheProvider, ILoggerFactory loggerFactory)
            : base(playerService, movementPhysx, cacheProvider, loggerFactory) { }

        public void TestHandleTurboFuel(TestVehicle vehicle, bool turbo) =>
            typeof(EightDirectionVehicleMovementService<TestVehicle>)
                .GetMethod("HandleTurboFuel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(this, [vehicle, turbo]);
    }
}


public class TestVehicle : Vehicle
{
    public override string SysId { get; set; } = "test";
}